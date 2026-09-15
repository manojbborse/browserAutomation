using Dapper;
using Microsoft.Data.SqlClient;

namespace Novaritz.WebCapture;

/// <summary>A brand that is due for capture in the current cycle (one row of dbo.fn_capture_queue).</summary>
public sealed record QueuedBrand(
    Guid BrandId, string Brand, Guid OrganizationId, string Client, int PromptsTotal,
    string Frequency, int CycleDays, DateTime? LastDoneCycle, int? DaysSinceDone,
    int? RunningRunId, DateTime? RunningSince, int? RunningAnswered);

/// <summary>One of a brand's stored prompts (dbo.prompts) not yet answered in this run.</summary>
public sealed record BrandPrompt(Guid PromptId, string PromptKey, string Text, string? Intent);

/// <summary>Where the LLM stands: brands due now, and runs finished in the current cycle.</summary>
public sealed record Backlog(int BrandsDue, int BrandsDoneThisCycle, long AnswersToday);

/// <summary>
/// Database access — plain T-SQL against the Novaritz schema on SQL Server
/// (Azure SQL Database in production, SQL Server 2019 locally).
///
/// The tool works per BRAND and per CYCLE (script 010):
///   * <c>dbo.fn_capture_queue(@llm)</c> lists the approved brands whose
///     prompt set (dbo.prompts) has not been captured by this LLM inside the
///     current cycle (cycle length = the client's plan frequency, Daily = 1).
///   * A brand is claimed with a <c>brand_capture_runs</c> row for today
///     (RUNNING), its prompts are answered one by one into
///     <c>LLM_web_responses</c> (one row per prompt per run, so every cycle
///     keeps its own answers), and the run is marked DONE only when every
///     prompt has an answer. A run that stops early is left PENDING with its
///     answers kept, and is resumed — not restarted — by the next start.
///
/// Nothing the backend owns is modified; the backend reads brand_capture_runs
/// for display only.
/// </summary>
public sealed class PromptRepository
{
    public const string Table = "LLM_web_responses";
    public const string RunsTable = "brand_capture_runs";

    /// <summary>A RUNNING row older than this is assumed abandoned and taken over.</summary>
    public static readonly TimeSpan StaleRunAfter = TimeSpan.FromHours(2);

    private readonly string _connectionString;
    private readonly string _llmType;

    /// <param name="llmType">Value written to LLMType and used to decide what is due, e.g. "chatgpt".</param>
    public PromptRepository(string connectionString, string llmType)
    {
        _connectionString = connectionString;
        _llmType = llmType;
    }

    public string LlmType => _llmType;

    private SqlConnection Open() => new(_connectionString);

    /// <summary>Refuse to run against a database that has not had script 010 applied.</summary>
    public async Task EnsureSchemaAsync()
    {
        const string sql = """
            SELECT CASE WHEN OBJECT_ID(N'dbo.brand_capture_runs', N'U') IS NOT NULL
                         AND OBJECT_ID(N'dbo.fn_capture_queue') IS NOT NULL
                         AND COL_LENGTH('dbo.LLM_web_responses', 'capture_run_id') IS NOT NULL
                        THEN 1 ELSE 0 END
            """;
        await using var conn = Open();
        if (await conn.ExecuteScalarAsync<int>(sql) != 1)
            throw new InvalidOperationException(
                "This database is missing the capture-cycle objects (brand_capture_runs, fn_capture_queue, " +
                "LLM_web_responses.capture_run_id). Apply D:\\Novaritz-SqlServer-Migration\\010_brand_capture_cycles.sql first.");
    }

    /// <summary>Brands due for this LLM now, never-captured first, then the longest-waiting.</summary>
    public async Task<IReadOnlyList<QueuedBrand>> QueueAsync(string? brand = null, string? client = null)
    {
        const string sql = """
            SELECT brandId AS BrandId, brand AS Brand, organization_id AS OrganizationId, client AS Client,
                   prompts_total AS PromptsTotal, frequency AS Frequency, cycle_days AS CycleDays,
                   last_done_cycle AS LastDoneCycle, days_since_done AS DaysSinceDone,
                   running_run_id AS RunningRunId, running_since AS RunningSince, running_answered AS RunningAnswered
            FROM dbo.fn_capture_queue(@llm)
            WHERE (@brand  IS NULL OR brand  = @brand)
              AND (@client IS NULL OR client = @client)
            ORDER BY CASE WHEN last_done_cycle IS NULL THEN 0 ELSE 1 END, last_done_cycle, brand
            """;
        await using var conn = Open();
        return (await conn.QueryAsync<QueuedBrand>(sql, new { llm = _llmType, brand, client })).ToList();
    }

    /// <summary>
    /// Take the brand for today's cycle: reuse today's RUNNING/PENDING row (resume),
    /// take over a stale one, or insert a new one. Returns null when another
    /// instance is actively running it, or when today is already DONE.
    /// </summary>
    public async Task<int?> ClaimRunAsync(QueuedBrand q, string runLog)
    {
        const string sql = """
            SET NOCOUNT ON;
            DECLARE @today DATE = CAST(SYSUTCDATETIME() AS date);
            DECLARE @id INT, @status NVARCHAR(16), @started DATETIME2(3);
            SELECT @id = id, @status = status, @started = started_at
            FROM dbo.brand_capture_runs
            WHERE brandId = @brandId AND LLMType = @llm AND cycle_date = @today;

            IF @id IS NULL
            BEGIN
                INSERT INTO dbo.brand_capture_runs
                    (brandId, organization_id, LLMType, cycle_date, status, prompts_total, prompts_answered, started_at, run_log)
                VALUES (@brandId, @orgId, @llm, @today, N'RUNNING', @total, 0, SYSUTCDATETIME(), @runLog);
                SELECT SCOPE_IDENTITY();
            END
            ELSE IF @status = N'DONE'
                SELECT NULL;
            ELSE IF @status = N'RUNNING' AND @started > DATEADD(second, -@staleSeconds, SYSUTCDATETIME()) AND @runLog <> ISNULL((SELECT run_log FROM dbo.brand_capture_runs WHERE id = @id), N'')
                SELECT NULL;    -- someone else is on it right now
            ELSE
            BEGIN
                UPDATE dbo.brand_capture_runs
                SET status = N'RUNNING', started_at = SYSUTCDATETIME(), prompts_total = @total, run_log = @runLog,
                    last_error = NULL, finished_at = NULL
                WHERE id = @id;
                SELECT @id;
            END
            """;
        await using var conn = Open();
        var id = await conn.ExecuteScalarAsync<int?>(sql, new
        {
            brandId = q.BrandId, orgId = q.OrganizationId, llm = _llmType, total = q.PromptsTotal, runLog,
            staleSeconds = (int)StaleRunAfter.TotalSeconds,
        });
        return id;
    }

    /// <summary>The brand's prompts (dbo.prompts, key order) not yet answered in this run.</summary>
    public async Task<IReadOnlyList<BrandPrompt>> PendingPromptsAsync(int runId, Guid brandId)
    {
        const string sql = $"""
            SELECT p.id AS PromptId, p.prompt_key AS PromptKey, p.[text] AS Text, p.intent AS Intent
            FROM dbo.prompts p
            WHERE p.brand_id = @brandId
              AND NOT EXISTS (SELECT 1 FROM dbo.{Table} r WHERE r.capture_run_id = @runId AND r.prompt_key = p.prompt_key)
            ORDER BY p.prompt_key
            """;
        await using var conn = Open();
        return (await conn.QueryAsync<BrandPrompt>(sql, new { brandId, runId })).ToList();
    }

    /// <summary>Insert one answer for this run and bump the run's counter. Returns the row id (0 if already answered).</summary>
    public async Task<long> SaveResponseAsync(int runId, QueuedBrand q, BrandPrompt p, string answer, int latencyMs,
                                              string? rawText, string? citationsJson, string? placesJson)
    {
        const string sql = $"""
            SET NOCOUNT ON;
            IF NOT EXISTS (SELECT 1 FROM dbo.{Table} WHERE capture_run_id = @runId AND prompt_key = @PromptKey)
            BEGIN
                INSERT INTO dbo.{Table}
                    (capture_run_id, prompt_id, audit_job_id, organization_id, LLMType, brandId, prompt_key, prompt_text, intent,
                     answer, latency_ms, raw_text, citations, places)
                OUTPUT INSERTED.id
                VALUES
                    (@runId, @PromptId, NULL, @orgId, @llm, @brandId, @PromptKey, @Text, @Intent,
                     @answer, @latencyMs, @rawText, @citationsJson, @placesJson);
                UPDATE dbo.{RunsTable}
                SET prompts_answered = (SELECT COUNT(*) FROM dbo.{Table} WHERE capture_run_id = @runId)
                WHERE id = @runId;
            END
            """;
        await using var conn = Open();
        var id = await conn.ExecuteScalarAsync<long?>(sql, new
        {
            runId, p.PromptId, orgId = q.OrganizationId, llm = _llmType, brandId = q.BrandId, p.PromptKey, p.Text, p.Intent,
            answer, latencyMs, rawText, citationsJson, placesJson,
        });
        return id ?? 0;
    }

    /// <summary>
    /// Close the run: DONE when every prompt has an answer; otherwise PENDING
    /// (partly done — the next start resumes it) with the reason recorded;
    /// FAILED only for a hard error. RUNNING is only ever a live process.
    /// </summary>
    public async Task<string> FinishRunAsync(int runId, string? error, bool hardFailure = false)
    {
        const string sql = $"""
            SET NOCOUNT ON;
            DECLARE @answered INT = (SELECT COUNT(*) FROM dbo.{Table} WHERE capture_run_id = @runId);
            DECLARE @total INT = (SELECT prompts_total FROM dbo.{RunsTable} WHERE id = @runId);
            DECLARE @status NVARCHAR(16) =
                CASE WHEN @answered >= @total AND @total > 0 THEN N'DONE'
                     WHEN @hard = 1 THEN N'FAILED'
                     ELSE N'PENDING' END;    -- partly done: resumed by the next start
            UPDATE dbo.{RunsTable}
            SET prompts_answered = @answered,
                status = @status,
                finished_at = CASE WHEN @status IN (N'DONE', N'FAILED') THEN SYSUTCDATETIME() ELSE NULL END,
                last_error = @error
            WHERE id = @runId;
            SELECT @status;
            """;
        await using var conn = Open();
        return await conn.ExecuteScalarAsync<string>(sql, new { runId, error, hard = hardFailure ? 1 : 0 }) ?? "PENDING";
    }

    /// <summary>Printed at the end: what is still due, what finished this cycle, answers stored today.</summary>
    public async Task<Backlog> SummaryAsync()
    {
        const string sql = $"""
            SELECT (SELECT COUNT(*) FROM dbo.fn_capture_queue(@llm)) AS BrandsDue,
                   (SELECT COUNT(*) FROM dbo.{RunsTable} WHERE LLMType = @llm AND status = N'DONE'
                       AND cycle_date = CAST(SYSUTCDATETIME() AS date)) AS BrandsDoneThisCycle,
                   (SELECT COUNT_BIG(*) FROM dbo.{Table} WHERE LLMType = @llm
                       AND captured_at >= CAST(CAST(SYSUTCDATETIME() AS date) AS datetime2)) AS AnswersToday
            """;
        await using var conn = Open();
        return await conn.QuerySingleAsync<Backlog>(sql, new { llm = _llmType });
    }
}
