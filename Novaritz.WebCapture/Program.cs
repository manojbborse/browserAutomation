// Answer each approved brand's stored prompts on chatgpt.com, one cycle at a time.
//
//     dotnet run -- login [--llm gemini]  once per LLM: sign in by hand, session is saved
//     dotnet run -- list [--llm gemini] [--brand "…"] [--client "…"]
//                                         brands due in the current cycle; touches nothing
//     dotnet run -- run                   works through the due brands, up to BatchLimit prompts
//     dotnet run -- run --dry-run         asks, prints, writes nothing (no run rows either)
//     dotnet run -- run --headless        no browser window (expect more challenges)
//     dotnet run -- run --limit 30 --pause 45
//     dotnet run -- run --brand "Nyati Elysia"    only that brand
//     dotnet run -- run --client "Regency Group"  only that client's brands
//     dotnet run -- run --llm gemini              capture with Gemini instead of ChatGPT
//
// --llm picks the web client and the LLMType written to the database
// (default: Capture:LlmType in appsettings, "chatgpt"). Each LLM has its own
// saved session file (storage_state.json / storage_state.gemini.json) and its
// own cycle rows; a brand is due for an LLM only if the client's plan
// includes it (pricing.llmsupport, SQL script 011).
//
// A brand is DUE when it is approved, has a prompt set (dbo.prompts) and has
// no DONE capture inside the current cycle (cycle length = the client's plan
// frequency; Daily = 1 day). For each due brand: claim today's
// brand_capture_runs row, ask every prompt not yet answered in that run, save
// one LLM_web_responses row per answer, mark the run DONE when all are
// answered. Stopping early (--limit, logout, page changed) leaves the run
// PENDING with its answers kept; the next start resumes it — even on a later
// day: an unfinished earlier cycle is completed first, then today's begins.
//
// A run log goes to <ResultsPath>/run-<timestamp>.jsonl, with a screenshot
// for every prompt that did not end in an answer.

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
var resultsDir = Resolve(options.ResultsPath);

// Which LLM this start works for: --llm on the command line beats appsettings.
var llmType = (StrArg(args, "--llm") ?? options.LlmType).Trim().ToLowerInvariant();
if (!WebClients.Supported.Contains(llmType))
{
    Console.Error.WriteLine($"Unknown --llm '{llmType}'. Supported: {string.Join(", ", WebClients.Supported)}.");
    return 2;
}
var statePath = WebClients.StatePathFor(llmType, Resolve(options.StatePath));

var command = args.FirstOrDefault() ?? "run";
switch (command)
{
    case "login":
        await WebClients.LoginInteractiveAsync(llmType, statePath, options.BrowserChannel);
        return 0;
    case "list":
        return await ListAsync(args.Skip(1).ToArray());
    case "run":
        return await RunAsync(args.Skip(1).ToArray());
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use: login | list | run [--llm chatgpt|gemini] [--dry-run] [--headless] [--limit N] [--pause S] [--brand X] [--client X]");
        return 2;
}

async Task<int> ListAsync(string[] listArgs)
{
    // Read-only: the same queue the run works through, printed.
    var repo = new PromptRepository(connectionString, llmType);
    await repo.EnsureSchemaAsync();
    var queue = await repo.QueueAsync(StrArg(listArgs, "--brand"), StrArg(listArgs, "--client"));
    if (queue.Count == 0) { Console.WriteLine($"Nothing due for {repo.LlmType} in the current cycle."); }
    foreach (var (q, i) in queue.Select((q, i) => (q, i + 1)))
        Console.WriteLine($"{i,3}. {q.Client} / {q.Brand} ({q.BrandId.ToString()[..8]})  {q.PromptsTotal} prompts, {q.Frequency}  " +
                          (q.RunningRunId is not null ? $"[resume: {q.RunningAnswered}/{q.PromptsTotal} answered]"
                           : q.LastDoneCycle is null ? "[never captured]"
                           : $"[last done {q.LastDoneCycle:yyyy-MM-dd}, {q.DaysSinceDone} day(s) ago]"));
    var s = await repo.SummaryAsync();
    Console.WriteLine($"{Environment.NewLine}{repo.LlmType}: {s.BrandsDue} brand(s) due, {s.BrandsDoneThisCycle} done this cycle, {s.AnswersToday} answers stored today.");
    return 0;
}

async Task<int> RunAsync(string[] runArgs)
{
    bool dryRun = runArgs.Contains("--dry-run");
    bool headless = runArgs.Contains("--headless");
    int limit = IntArg(runArgs, "--limit") ?? options.BatchLimit;      // max prompts this start, across brands
    double pause = DoubleArg(runArgs, "--pause") ?? options.PauseSeconds;
    string? brand = StrArg(runArgs, "--brand");     // exact brand name, e.g. "Nyati Elysia"
    string? client = StrArg(runArgs, "--client");   // exact organization name, e.g. "Regency Group"

    var repo = new PromptRepository(connectionString, llmType);
    await repo.EnsureSchemaAsync();
    var queue = await repo.QueueAsync(brand, client);
    if (queue.Count == 0)
    {
        Console.WriteLine($"Nothing to do: no brand is due for {repo.LlmType} in the current cycle.");
        return 0;
    }
    Console.WriteLine($"{queue.Count} brand(s) due ({queue.Sum(q => q.PromptsTotal)} prompts in total); up to {limit} prompt(s) this start; " +
                      $"{pause:0}s pause between; {(dryRun ? "DRY RUN — nothing is written" : $"writing to '{PromptRepository.Table}' as LLMType '{repo.LlmType}'")}.");

    Directory.CreateDirectory(resultsDir);
    var runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
    var logName = $"run-{runId}.jsonl";
    var logPath = Path.Combine(resultsDir, logName);
    var tally = new Dictionary<string, int>();
    int asked = 0, shot = 0;
    bool stopAll = false;

    await using var web = WebClients.Create(llmType, statePath, options.AnswerTimeoutSeconds, options.BrowserChannel);
    await web.OpenAsync(headless);
    try
    {
        foreach (var q in queue)
        {
          // A brand may need two runs in one start: an unfinished earlier
          // cycle first, then today's. Loop until today's run is settled.
          for (var pass = 0; pass < 3; pass++)
          {
            if (stopAll || asked >= limit) break;

            int? captureRunId = null;
            ClaimedRun? claimed = null;
            IReadOnlyList<BrandPrompt> prompts;
            if (dryRun)
            {
                if (pass > 0) break;
                prompts = await repo.PendingPromptsAsync(q.RunningRunId ?? -1, q.BrandId);   // -1: nothing answered yet
            }
            else
            {
                claimed = await repo.ClaimRunAsync(q, logName);
                if (claimed is null)
                {
                    if (pass == 0) Console.WriteLine($"— {q.Client} / {q.Brand}: skipped (another instance is on it, or already done today).");
                    break;
                }
                captureRunId = claimed.Id;
                prompts = await repo.PendingPromptsAsync(captureRunId.Value, q.BrandId);
            }
            Console.WriteLine($"{Environment.NewLine}== {q.Client} / {q.Brand}: {prompts.Count} of {q.PromptsTotal} prompt(s) to answer" +
                              (captureRunId is null ? "" : $" (run #{captureRunId}, cycle {claimed!.CycleDate:yyyy-MM-dd}{(claimed.IsToday ? "" : " — finishing an earlier cycle")})"));

            string? stopReason = null;
            bool hardFailure = false;
            foreach (var p in prompts)
            {
                if (asked >= limit) { stopReason = $"--limit {limit} reached"; break; }
                asked++;
                Console.WriteLine($"[{asked}/{limit}] {q.Brand} / {p.PromptKey}: {Truncate(p.Text, 70)}…");

                AskResult r;
                try { r = await web.AskAsync(p.Text); }
                catch (Exception ex) { r = new AskResult(Outcome.Error, 0, Error: $"{ex.GetType().Name}: {ex.Message}"); }

                var record = new Dictionary<string, object?>
                {
                    ["run"] = runId, ["at"] = DateTime.UtcNow.ToString("O"),
                    ["capture_run_id"] = captureRunId, ["brand_id"] = q.BrandId, ["prompt_id"] = p.PromptId,
                    ["llm_type"] = repo.LlmType, ["prompt_key"] = p.PromptKey,
                    ["client"] = q.Client, ["brand"] = q.Brand,
                    ["intent"] = p.Intent, ["outcome"] = r.Outcome.ToString().ToLowerInvariant(),
                    ["seconds"] = r.Seconds, ["error"] = r.Error,
                };

                if (r.Outcome == Outcome.Answered && r.Answer is not null)
                {
                    record["chars"] = r.Answer.Length;
                    record["citations"] = r.CitationsJson is null ? 0 : JsonDocument.Parse(r.CitationsJson).RootElement.GetArrayLength();
                    if (dryRun)
                    {
                        Console.WriteLine($"    → answered in {r.Seconds}s ({r.Answer.Length} chars, {record["citations"]} source(s)) — not saved (dry run)");
                        if (r.CitationsJson is not null) Console.WriteLine($"      sources: {Truncate(r.CitationsJson, 600)}");
                        Console.WriteLine($"      ends with: …{r.Answer[Math.Max(0, r.Answer.Length - 160)..].Replace('\n', ' ')}");
                    }
                    else
                    {
                        var id = await repo.SaveResponseAsync(captureRunId!.Value, q, p, r.Answer, (int)(r.Seconds * 1000),
                                                              r.RawText, r.CitationsJson, r.PlacesJson);
                        record["response_id"] = id;
                        Console.WriteLine($"    → answered in {r.Seconds}s, saved as {PromptRepository.Table} id {id}");
                    }
                }
                else
                {
                    shot++;
                    var png = Path.Combine(resultsDir, $"run-{runId}-{shot:00}-{record["outcome"]}.png");
                    try { await web.ScreenshotAsync(png); record["screenshot"] = Path.GetFileName(png); }
                    catch { /* a failed screenshot must not hide the real outcome */ }
                    Console.WriteLine($"    → {record["outcome"]} after {r.Seconds}s{(r.Error is null ? "" : $": {r.Error}")}");
                }

                var key = (string)record["outcome"]!;
                tally[key] = tally.GetValueOrDefault(key) + 1;
                await File.AppendAllTextAsync(logPath, JsonSerializer.Serialize(record) + Environment.NewLine);

                if (r.ShouldStopRun)
                {
                    stopReason = $"page is no longer the chat ({record["outcome"]}); see the screenshot in results/";
                    stopAll = true;
                    break;
                }
                if (asked < limit)
                    await Task.Delay(TimeSpan.FromSeconds(pause));
            }

            if (captureRunId is not null)
            {
                var status = await repo.FinishRunAsync(captureRunId.Value, stopReason, hardFailure);
                Console.WriteLine($"== {q.Brand}: {status}{(stopReason is null ? "" : $" — {stopReason}")}");
                if (status == "DONE" && !claimed!.IsToday) continue;   // earlier cycle finished: now claim today's
            }
            else if (stopReason is not null)
            {
                Console.WriteLine($"== {q.Brand}: stopped — {stopReason}");
            }
            break;
          }
        }
    }
    finally
    {
        await web.SaveSessionAsync();
    }

    Console.WriteLine();
    Console.WriteLine("Outcomes: " + JsonSerializer.Serialize(tally));
    var s = await repo.SummaryAsync();
    Console.WriteLine($"{repo.LlmType}: {s.BrandsDue} brand(s) still due, {s.BrandsDoneThisCycle} done this cycle, {s.AnswersToday} answers stored today.");
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
