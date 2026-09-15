using Dapper;
using Microsoft.Data.SqlClient;

namespace Novaritz.WebCapture;

/// <summary>One prompt that still needs a chatgpt.com answer, with its owner for logging.</summary>
public sealed record PendingPrompt(
    Guid AuditJobId, Guid? OrganizationId, string? Client, string Brand, Guid BrandId,
    string PromptKey, string Text, string? Intent);

/// <summary>Progress through the backlog.</summary>
public sealed record Backlog(long Prompts, long Answered);

/// <summary>
/// Database access — plain T-SQL against the Novaritz schema on SQL Server
/// (Azure SQL Database in production, SQL Server 2019 locally).
///
/// The backend keeps each audit's prompts (and the API answers to them) as
/// JSON inside <c>audit_jobs.result</c>, under <c>prompts[]</c>; the
/// normalised <c>prompts</c> / <c>ai_executions</c> tables in its schema are
/// not populated. So this application reads the JSON with OPENJSON and
/// writes to a table of its own, <c>LLM_web_responses</c>, which it creates
/// if missing. Nothing the backend owns is modified.
///
/// Each row records which LLM answered (<c>LLMType</c>: chatgpt, claude,
/// gemini …) and which brand the prompt was generated for (<c>brandId</c> →
/// <c>brand_ai_profiles.id</c>). audit_jobs carries only the brand's <em>name</em>, so
/// the id is resolved by name at read time — with the audit's organization
/// as tie-breaker should the same name ever exist under two clients — and a
/// prompt whose brand cannot be resolved is left out rather than stored
/// against a wrong id. The client is reachable through
/// <c>audit_job_id → audit_jobs.organization_id → organizations</c>.
///
/// "Pending" is per LLM: a prompt already answered by chatgpt is still
/// pending for claude. That needs the unique key to be
/// (audit_job_id, prompt_key, LLMType); a table created by this class has
/// that, an older hand-made one with (audit_job_id, prompt_key) still works
/// for a single LLM.
/// </summary>
public sealed class PromptRepository
{
    public const string Table = "LLM_web_responses";

    private readonly string _connectionString;
    private readonly string _llmType;

    /// <param name="llmType">Value written to LLMType and used to decide what is still pending, e.g. "chatgpt".</param>
    public PromptRepository(string connectionString, string llmType)
    {
        _connectionString = connectionString;
        _llmType = llmType;
    }

    public string LlmType => _llmType;

    private SqlConnection Open() => new(_connectionString);

    /// <summary>Create this application's table if it does not exist yet. Safe to call every run.</summary>
    public async Task EnsureSchemaAsync()
    {
        const string sql = $"""
            IF OBJECT_ID(N'dbo.{Table}', N'U') IS NULL
            CREATE TABLE [dbo].[{Table}] (
                [id]              BIGINT IDENTITY(1,1) NOT NULL,
                [audit_job_id]    UNIQUEIDENTIFIER NOT NULL,
                [organization_id] UNIQUEIDENTIFIER NULL,
                [LLMType]         NVARCHAR(32)     NOT NULL,
                [brandId]         UNIQUEIDENTIFIER NOT NULL,
                [prompt_key]      NVARCHAR(32)     NOT NULL,
                [prompt_text]     NVARCHAR(MAX)    NOT NULL,
                [intent]          NVARCHAR(64)     NULL,
                [answer]          NVARCHAR(MAX)    NOT NULL,
                [latency_ms]      INT              NULL,
                [captured_at]     DATETIME2(3)     NOT NULL DEFAULT SYSUTCDATETIME(),
                [raw_text]        NVARCHAR(MAX)    NULL,
                [citations]       NVARCHAR(MAX)    NULL,
                [places]          NVARCHAR(MAX)    NULL,
                CONSTRAINT [PK_{Table}] PRIMARY KEY ([id]),
                CONSTRAINT [UQ_{Table}_job_prompt_llm] UNIQUE ([audit_job_id], [prompt_key], [LLMType]),
                CONSTRAINT [FK_{Table}_audit_job] FOREIGN KEY ([audit_job_id]) REFERENCES [dbo].[audit_jobs] ([id]),
                CONSTRAINT [FK_{Table}_brand_id] FOREIGN KEY ([brandId]) REFERENCES [dbo].[brand_ai_profiles] ([id]),
                CONSTRAINT [CK_{Table}_citations_json] CHECK ([citations] IS NULL OR ISJSON([citations]) = 1),
                CONSTRAINT [CK_{Table}_places_json]    CHECK ([places]    IS NULL OR ISJSON([places])    = 1)
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_{Table}_org' AND object_id = OBJECT_ID(N'dbo.{Table}'))
                CREATE INDEX [ix_{Table}_org] ON [dbo].[{Table}] ([organization_id], [captured_at] DESC);
            """;
        await using var conn = Open();
        await conn.ExecuteAsync(sql);
    }

    // The prompts array inside audit_jobs.result, one row per prompt.
    private const string PromptsFromJson = """
        CROSS APPLY OPENJSON(j.result, '$.prompts')
            WITH ([key] NVARCHAR(32) '$.id', [text] NVARCHAR(MAX) '$.text', [intent] NVARCHAR(64) '$.intent') AS p
        """;

    // The brand an audit was run for. audit_jobs stores the name only.
    private const string BrandForAudit = """
        OUTER APPLY (SELECT TOP (1) b.id FROM brand_ai_profiles b WHERE b.name = j.brand_name
                     ORDER BY CASE WHEN b.organization_id = j.organization_id THEN 0 ELSE 1 END) AS br
        """;

    /// <summary>
    /// Oldest audits first, prompts in their audit order, skipping any prompt
    /// that already has a row. Only completed audits with a result are read.
    /// </summary>
    public async Task<IReadOnlyList<PendingPrompt>> FetchUnansweredAsync(int limit, string? brand = null, string? client = null)
    {
        const string sql = $"""
            SELECT TOP (@limit)
                   j.id              AS AuditJobId,
                   j.organization_id AS OrganizationId,
                   o.name            AS Client,
                   j.brand_name      AS Brand,
                   br.id             AS BrandId,
                   p.[key]           AS PromptKey,
                   p.[text]          AS Text,
                   p.[intent]        AS Intent
            FROM audit_jobs j
            LEFT JOIN organizations o ON o.id = j.organization_id
            {BrandForAudit}
            {PromptsFromJson}
            WHERE j.status = 'COMPLETED'
              AND j.result IS NOT NULL
              AND p.[text] IS NOT NULL
              AND br.id IS NOT NULL
              AND (@brand  IS NULL OR j.brand_name = @brand)
              AND (@client IS NULL OR o.name = @client)
              AND NOT EXISTS (
                  SELECT 1 FROM {Table} r
                  WHERE r.audit_job_id = j.id AND r.prompt_key = p.[key] AND r.LLMType = @llm
              )
            ORDER BY j.created_at ASC, p.[key] ASC
            """;
        await using var conn = Open();
        var rows = await conn.QueryAsync<PendingPrompt>(sql, new { limit, brand, client, llm = _llmType });
        return rows.ToList();
    }

    /// <summary>Audits whose brand name has no brand_ai_profiles row — their prompts are never returned as pending.</summary>
    public async Task<IReadOnlyList<string>> UnresolvedBrandsAsync()
    {
        const string sql = $"""
            SELECT DISTINCT j.brand_name FROM audit_jobs j {BrandForAudit}
            WHERE j.status = 'COMPLETED' AND br.id IS NULL
            """;
        await using var conn = Open();
        return (await conn.QueryAsync<string>(sql)).ToList();
    }

    /// <summary>Insert one response row and return its id (0 if this LLM already answered the prompt).</summary>
    public async Task<long> SaveResponseAsync(PendingPrompt p, string answer, int latencyMs,
                                              string? rawText, string? citationsJson, string? placesJson)
    {
        const string sql = $"""
            IF NOT EXISTS (SELECT 1 FROM {Table} WHERE audit_job_id = @AuditJobId AND prompt_key = @PromptKey AND LLMType = @llm)
            INSERT INTO {Table}
                (audit_job_id, organization_id, LLMType, brandId, prompt_key, prompt_text, intent, answer, latency_ms,
                 raw_text, citations, places)
            OUTPUT INSERTED.id
            VALUES
                (@AuditJobId, @OrganizationId, @llm, @BrandId, @PromptKey, @Text, @Intent, @answer, @latencyMs,
                 @rawText, @citationsJson, @placesJson)
            """;
        await using var conn = Open();
        var id = await conn.ExecuteScalarAsync<long?>(sql, new
        {
            p.AuditJobId, p.OrganizationId, llm = _llmType, p.BrandId, p.PromptKey, p.Text, p.Intent, answer, latencyMs,
            rawText, citationsJson, placesJson,
        });
        return id ?? 0;
    }

    /// <summary>How far through the backlog we are — printed at the end of a run.</summary>
    public async Task<Backlog> SummaryAsync()
    {
        const string sql = $"""
            SELECT COUNT_BIG(*)   AS Prompts,
                   COUNT_BIG(r.id) AS Answered
            FROM audit_jobs j
            {PromptsFromJson}
            LEFT JOIN {Table} r ON r.audit_job_id = j.id AND r.prompt_key = p.[key] AND r.LLMType = @llm
            WHERE j.status = 'COMPLETED' AND j.result IS NOT NULL
            """;
        await using var conn = Open();
        return await conn.QuerySingleAsync<Backlog>(sql, new { llm = _llmType });
    }
}
