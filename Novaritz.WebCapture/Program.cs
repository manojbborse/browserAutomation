// Answer Novaritz prompts on chatgpt.com and store each answer against its client.
//
//     dotnet run -- login                 once: sign in by hand, session is saved
//     dotnet run -- list [--limit N] [--brand "…"] [--client "…"]
//                                         show the next pending prompts; touches nothing
//     dotnet run -- run                   answers up to BatchLimit unanswered prompts
//     dotnet run -- run --dry-run         same, but prints instead of writing to the DB
//     dotnet run -- run --headless        no browser window (expect more challenges)
//     dotnet run -- run --limit 3 --pause 45
//     dotnet run -- run --limit 5 --brand "GK Merai"     only that brand's prompts
//
// Each run: read the oldest prompts (from audit_jobs.result JSON) that have no
// chatgpt.com answer yet, ask chatgpt.com one at a time with a pause between,
// and write one chatgpt_web_responses row per answer. A prompt is attributed
// to its client through audit_job -> organization. A run log goes to
// <ResultsPath>/run-<timestamp>.jsonl, with a screenshot for every prompt that
// did not end in an answer.

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Novaritz.WebCapture;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddUserSecrets<CaptureOptions>(optional: true)
    .AddEnvironmentVariables(prefix: "NOVARITZ_")
    .Build();

var options = config.GetSection("Capture").Get<CaptureOptions>() ?? new CaptureOptions();
var connectionString = config.GetConnectionString("Novaritz")
    ?? throw new InvalidOperationException("ConnectionStrings:Novaritz is not set.");

// Relative paths in appsettings are relative to the project folder, not bin/.
string Resolve(string p) => Path.GetFullPath(p, ProjectDir());
var statePath = Resolve(options.StatePath);
var resultsDir = Resolve(options.ResultsPath);

var command = args.FirstOrDefault() ?? "run";
switch (command)
{
    case "login":
        await ChatGptWebClient.LoginInteractiveAsync(statePath, options.BrowserChannel);
        return 0;
    case "list":
        return await ListAsync(args.Skip(1).ToArray());
    case "run":
        return await RunAsync(args.Skip(1).ToArray());
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use: login | list | run [--dry-run] [--headless] [--limit N] [--pause S] [--brand X] [--client X]");
        return 2;
}

async Task<int> ListAsync(string[] listArgs)
{
    // Read-only: the same query the run uses, printed instead of answered.
    var repo = new PromptRepository(connectionString, options.LlmType);
    var prompts = await repo.FetchUnansweredAsync(
        IntArg(listArgs, "--limit") ?? options.BatchLimit, StrArg(listArgs, "--brand"), StrArg(listArgs, "--client"));
    var unresolved = await repo.UnresolvedBrandsAsync();
    if (unresolved.Count > 0)
        Console.WriteLine($"Skipped (no brands row for): {string.Join(", ", unresolved)}");
    if (prompts.Count == 0) { Console.WriteLine($"Nothing pending for {repo.LlmType}."); return 0; }
    foreach (var (pr, i) in prompts.Select((p, i) => (p, i + 1)))
        Console.WriteLine($"{i,3}. {pr.Client ?? "—"} / {pr.Brand} ({pr.BrandId.ToString()[..8]}) / {pr.PromptKey} [{pr.Intent}]{Environment.NewLine}     {pr.Text}");
    var s = await repo.SummaryAsync();
    Console.WriteLine($"{Environment.NewLine}Backlog: {s.Answered} of {s.Prompts} prompts have a {repo.LlmType} answer.");
    return 0;
}

async Task<int> RunAsync(string[] runArgs)
{
    bool dryRun = runArgs.Contains("--dry-run");
    bool headless = runArgs.Contains("--headless");
    int limit = IntArg(runArgs, "--limit") ?? options.BatchLimit;
    double pause = DoubleArg(runArgs, "--pause") ?? options.PauseSeconds;
    string? brand = StrArg(runArgs, "--brand");     // exact brand_name, e.g. "GK Merai"
    string? client = StrArg(runArgs, "--client");   // exact organization name, e.g. "GK Associates"

    var repo = new PromptRepository(connectionString, options.LlmType);
    await repo.EnsureSchemaAsync();   // creates only this app's own (empty) table; a dry run writes nothing else
    var prompts = await repo.FetchUnansweredAsync(limit, brand, client);
    if (prompts.Count == 0)
    {
        Console.WriteLine($"Nothing to do: every prompt already has a {repo.LlmType} answer.");
        return 0;
    }
    Console.WriteLine($"{prompts.Count} prompt(s) to answer; {pause:0}s pause between; " +
                      $"{(dryRun ? "DRY RUN — " : "")}writing to '{PromptRepository.Table}' as LLMType '{repo.LlmType}'.");

    Directory.CreateDirectory(resultsDir);
    var runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
    var logPath = Path.Combine(resultsDir, $"run-{runId}.jsonl");
    var tally = new Dictionary<string, int>();

    await using var web = new ChatGptWebClient(statePath, options.AnswerTimeoutSeconds, options.BrowserChannel);
    await web.OpenAsync(headless);
    try
    {
        for (int i = 0; i < prompts.Count; i++)
        {
            var pr = prompts[i];
            var n = i + 1;
            Console.WriteLine($"[{n}/{prompts.Count}] {pr.Client ?? "—"} / {pr.Brand} / {pr.PromptKey}: {Truncate(pr.Text, 70)}…");

            AskResult r;
            try { r = await web.AskAsync(pr.Text); }
            catch (Exception ex) { r = new AskResult(Outcome.Error, 0, Error: $"{ex.GetType().Name}: {ex.Message}"); }

            var record = new Dictionary<string, object?>
            {
                ["run"] = runId, ["at"] = DateTime.UtcNow.ToString("O"),
                ["audit_job_id"] = pr.AuditJobId, ["brand_id"] = pr.BrandId, ["llm_type"] = repo.LlmType, ["prompt_key"] = pr.PromptKey,
                ["client"] = pr.Client, ["brand"] = pr.Brand,
                ["intent"] = pr.Intent, ["outcome"] = r.Outcome.ToString().ToLowerInvariant(),
                ["seconds"] = r.Seconds, ["error"] = r.Error,
            };

            if (r.Outcome == Outcome.Answered && r.Answer is not null)
            {
                record["chars"] = r.Answer.Length;
                if (dryRun)
                {
                    Console.WriteLine($"    → answered in {r.Seconds}s ({r.Answer.Length} chars) — not saved (dry run)");
                }
                else
                {
                    var id = await repo.SaveResponseAsync(pr, r.Answer, (int)(r.Seconds * 1000), r.RawText, r.CitationsJson, r.PlacesJson);
                    record["response_id"] = id;
                    Console.WriteLine($"    → answered in {r.Seconds}s, saved as {PromptRepository.Table} id {id}");
                }
            }
            else
            {
                var shot = Path.Combine(resultsDir, $"run-{runId}-{n:00}-{record["outcome"]}.png");
                try { await web.ScreenshotAsync(shot); record["screenshot"] = Path.GetFileName(shot); }
                catch { /* a failed screenshot must not hide the real outcome */ }
                Console.WriteLine($"    → {record["outcome"]} after {r.Seconds}s{(r.Error is null ? "" : $": {r.Error}")}");
            }

            var key = (string)record["outcome"]!;
            tally[key] = tally.GetValueOrDefault(key) + 1;
            await File.AppendAllTextAsync(logPath, JsonSerializer.Serialize(record) + Environment.NewLine);

            if (r.ShouldStopRun)
            {
                Console.WriteLine("Stopping: the page is no longer the chat. See the screenshot in results/.");
                break;
            }
            if (n < prompts.Count)
                await Task.Delay(TimeSpan.FromSeconds(pause));
        }
    }
    finally
    {
        await web.SaveSessionAsync();
    }

    Console.WriteLine();
    Console.WriteLine("Outcomes: " + JsonSerializer.Serialize(tally));
    var s = await repo.SummaryAsync();
    Console.WriteLine($"Backlog: {s.Answered} of {s.Prompts} prompts have a {repo.LlmType} answer.");
    Console.WriteLine($"Log: {logPath}");
    return 0;
}

static string ProjectDir()
{
    // bin/Debug/net8.0 -> project folder. Falls back to the current directory
    // for a published single-folder deployment, where paths should be absolute.
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (int up = 0; up < 3 && dir.Parent is not null; up++) dir = dir.Parent;
    return File.Exists(Path.Combine(dir.FullName, "Novaritz.WebCapture.csproj"))
        ? dir.FullName
        : Directory.GetCurrentDirectory();
}

static int? IntArg(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : null;
}

static double? DoubleArg(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length && double.TryParse(a[i + 1], out var v) ? v : null;
}

static string? StrArg(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
