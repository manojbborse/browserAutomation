using System.Diagnostics;
using Microsoft.Playwright;

namespace Novaritz.WebCapture;

/// <summary>
/// The browser side for gemini.google.com — same contract as
/// <see cref="ChatGptWebClient"/>: submit one prompt in a fresh conversation,
/// wait for the answer to finish, return the prose plus any sources.
///
/// Gemini's page is an Angular app with custom elements; the selectors below
/// are the ones that have been stable across its 2025–26 layouts, each with
/// a fallback. When every prompt comes back <see cref="Outcome.Unknown"/> or
/// <see cref="Outcome.NoAnswer"/>, look here first and at the screenshot the
/// run left in results/.
///
/// Sign-in is a Google account (a dedicated one). Google treats automation
/// and datacenter addresses with suspicion; a challenge or sign-in wall is
/// reported as an outcome and the run stops — nothing here pretends to be a
/// person.
/// </summary>
public sealed class GeminiWebClient : IWebLlmClient
{
    private const string Home = "https://gemini.google.com/app";

    // The prompt box is a Quill editor inside <rich-textarea>.
    private const string Composer = "rich-textarea .ql-editor[contenteditable=\"true\"], div.ql-editor[contenteditable=\"true\"]";
    private const string Send = "button[aria-label=\"Send message\"], button.send-button";
    // While the model is answering, the send button turns into Stop.
    private const string Stop = "button[aria-label=\"Stop response\"], button.stop-button";
    // One <model-response> per answer; the prose lives in .model-response-text / message-content.
    private const string Response = "model-response";
    private const string ResponseText = ".model-response-text, message-content, .markdown";

    // Pull the prose out of the last answer. Gemini puts three kinds of
    // non-answer content inside the same container: source chips
    // (<source-inline-chip>, a button whose label is the site name), the
    // footer/actions, and interactive widgets (a question with radio
    // options, "Step 1/4 …") that it renders after some answers. All are
    // removed from a clone before reading innerText; inline anchors (rare)
    // are collected. Source URLs come from the chips' popovers, read by
    // ReadSourceChipsAsync, because the chip itself carries no href.
    private const string ExtractScript = """
        el => {
          const clean = t => (t || '').replace(/[ \t]+/g, ' ').replace(/\n{3,}/g, '\n\n').trim();
          const body = el.querySelector('.model-response-text') || el.querySelector('message-content') || el;
          const clone = body.cloneNode(true);
          const citations = [];
          const seen = new Set();
          clone.querySelectorAll('a[href]').forEach(a => {
            const href = a.href || '';
            if (!/^https?:/.test(href) || /google\.com\/(search|url)/.test(href)) return;
            if (seen.has(href)) return;
            seen.add(href);
            citations.push({ text: clean(a.innerText) || null, href });
          });
          // interactive widgets: forms, option groups, steppers and anything Gemini marks as a card the user acts on
          clone.querySelectorAll('form, [role="radiogroup"], [role="radio"], input, select, textarea, ' +
                                 '[class*="stepper"], [class*="quiz"], [class*="interactive"], [class*="questionnaire"], ' +
                                 'button, mat-icon, .response-footer, sources-list, source-inline-chip, source-footnote, .sources-container')
               .forEach(n => n.remove());
          let answer = clean(clone.innerText);
          // text fallback for a widget rendered as plain blocks: drop a trailing "Step n/m" block
          const m = answer.match(/\n[^\n]{0,120}\n?Step \d+\/\d+[\s\S]*$/);
          if (m && m.index > answer.length * 0.5) answer = answer.slice(0, m.index).trim();
          return { answer, citations, places: [] };
        }
        """;

    private static readonly string[] ChallengeText = { "unusual traffic", "Verify it's you", "Confirm you", "not a robot" };
    private static readonly string[] LoginText = { "Sign in", "Use your Google Account" };

    private readonly string _statePath;
    private readonly int _answerTimeoutSeconds;
    private readonly string? _channel;
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    public GeminiWebClient(string statePath, int answerTimeoutSeconds, string? channel)
    {
        _statePath = statePath;
        _answerTimeoutSeconds = answerTimeoutSeconds;
        _channel = string.IsNullOrWhiteSpace(channel) ? null : channel;
    }

    public async Task OpenAsync(bool headless)
    {
        if (!File.Exists(_statePath))
            throw new InvalidOperationException(
                $"No saved Gemini session at {_statePath} — run `dotnet run -- login --llm gemini` first.");

        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new() { Headless = headless, Channel = _channel });
        _context = await _browser.NewContextAsync(new() { StorageStatePath = _statePath });
        _page = await _context.NewPageAsync();
    }

    public static async Task LoginInteractiveAsync(string statePath, string? channel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new()
        {
            Headless = false, Channel = string.IsNullOrWhiteSpace(channel) ? null : channel,
        });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(Home, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        Console.WriteLine("A browser window is open. Sign in to Gemini with the dedicated Google account.");
        Console.WriteLine("When the 'Ask Gemini' box is visible, press Enter here to save the session.");
        Console.ReadLine();

        await context.StorageStateAsync(new() { Path = statePath });
        Console.WriteLine($"Saved session to {Path.GetFullPath(statePath)}");
    }

    public async Task SaveSessionAsync()
    {
        if (_context is not null)
            await _context.StorageStateAsync(new() { Path = _statePath });
    }

    public async Task ScreenshotAsync(string path)
    {
        if (_page is not null)
            await _page.ScreenshotAsync(new() { Path = path, FullPage = true });
    }

    public async Task<AskResult> AskAsync(string promptText)
    {
        var page = _page ?? throw new InvalidOperationException("Call OpenAsync first.");
        var sw = Stopwatch.StartNew();
        double Elapsed() => Math.Round(sw.Elapsed.TotalSeconds, 1);

        await page.GotoAsync(Home, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        try { await page.Locator(Composer).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20_000 }); }
        catch (TimeoutException) { }

        var state = await ClassifyPageAsync(page);
        if (state != Outcome.Answered)
            return new AskResult(state, Elapsed());

        var before = await page.Locator(Response).CountAsync();
        var composer = page.Locator(Composer).First;
        await composer.ClickAsync();
        await composer.FillAsync(promptText);
        var send = page.Locator(Send).First;
        try { await send.ClickAsync(new() { Timeout = 5_000 }); }
        catch (TimeoutException) { await composer.PressAsync("Enter"); }   // some layouts send on Enter only

        // Streaming: Stop appears while the model writes and disappears when done.
        try { await page.Locator(Stop).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }); }
        catch (TimeoutException) { /* short answers can finish before we look */ }
        try
        {
            await page.Locator(Stop).First.WaitForAsync(new()
            {
                State = WaitForSelectorState.Hidden,
                Timeout = _answerTimeoutSeconds * 1000f,
            });
        }
        catch (TimeoutException)
        {
            return new AskResult(Outcome.Timeout, Elapsed());
        }

        // Wait for a new response element, then let the final render settle.
        try
        {
            await page.Locator(Response).Nth(before).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        }
        catch (TimeoutException)
        {
            return new AskResult(Outcome.NoAnswer, Elapsed());
        }
        await page.WaitForTimeoutAsync(2000);

        var last = page.Locator(Response).Last;
        // NOVARITZ_DEBUG_DUMP=<folder>: save the answer's HTML so selector changes can be made from a real page.
        var dump = Environment.GetEnvironmentVariable("NOVARITZ_DEBUG_DUMP");
        if (!string.IsNullOrWhiteSpace(dump))
        {
            Directory.CreateDirectory(dump);
            await File.WriteAllTextAsync(Path.Combine(dump, $"gemini-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.html"),
                                         await last.EvaluateAsync<string>("el => el.outerHTML"));
        }
        var textNode = last.Locator(ResponseText).First;
        var raw = (await (await textNode.CountAsync() > 0 ? textNode : last).InnerTextAsync()).Trim();
        if (raw.Length == 0)
            return new AskResult(Outcome.NoAnswer, Elapsed());

        var extracted = await last.EvaluateAsync<System.Text.Json.JsonElement>(ExtractScript);
        var answer = extracted.GetProperty("answer").GetString();
        var citations = new List<Dictionary<string, string?>>();
        foreach (var c in extracted.GetProperty("citations").EnumerateArray())
            citations.Add(new() { ["text"] = c.GetProperty("text").GetString(), ["href"] = c.GetProperty("href").GetString() });
        citations.AddRange(await ReadSourceChipsAsync(page, last));
        var citationsJson = citations.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(citations) : null;
        return new AskResult(Outcome.Answered, Elapsed(),
            Answer: string.IsNullOrWhiteSpace(answer) ? raw : answer, RawText: raw,
            CitationsJson: citationsJson, PlacesJson: null);
    }

    private const string SourceChip = "source-inline-chip button";
    // The chip opens a hover card (div[role=dialog] > multi-source-hovered-card)
    // holding one <inline-source-card> per source, each with the real link.
    private const string HoverCardLink = "[role=\"dialog\"] inline-source-card a[href], multi-source-hovered-card a[href]";
    private const int MaxChips = 20;

    /// <summary>
    /// Gemini's sources are chips labelled with the site name ("MagicBricks",
    /// "SVTN Group + 2"); the URLs live in the hover card the chip opens, one
    /// card per source. Hover each chip once, read every link in the card,
    /// close it. Best effort: a chip whose card never opens is recorded by name.
    /// </summary>
    private static async Task<List<Dictionary<string, string?>>> ReadSourceChipsAsync(IPage page, ILocator response)
    {
        var found = new List<Dictionary<string, string?>>();
        var seen = new HashSet<string>();
        var chips = response.Locator(SourceChip);
        var count = Math.Min(await chips.CountAsync(), MaxChips);
        for (var i = 0; i < count; i++)
        {
            var chip = chips.Nth(i);
            string label;
            try { label = (await chip.InnerTextAsync(new() { Timeout = 2000 })).Replace("\n", " ").Trim(); }
            catch (TimeoutException) { continue; }

            var links = new List<(string? text, string href)>();
            try
            {
                await chip.HoverAsync(new() { Timeout = 3000 });
                var card = page.Locator(HoverCardLink);
                await card.First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 4000 });
                await page.WaitForTimeoutAsync(400);   // the card fills in a beat after it appears
                var n = await card.CountAsync();
                for (var k = 0; k < n; k++)
                {
                    var a = card.Nth(k);
                    var href = await a.GetAttributeAsync("href");
                    if (string.IsNullOrWhiteSpace(href)) continue;
                    var hash = href.IndexOf("#:~:text=", StringComparison.Ordinal);
                    if (hash > 0) href = href[..hash];            // drop the text-fragment highlight
                    string? text = null;
                    // the card shows site, page title and a snippet on separate lines; keep the title line
                    try
                    {
                        var lines = (await a.InnerTextAsync(new() { Timeout = 1000 })).Split('\n')
                            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                        text = lines.Count > 1 ? lines[1] : lines.FirstOrDefault();
                        if (text is { Length: > 160 }) text = text[..160];
                    }
                    catch { }
                    links.Add((string.IsNullOrWhiteSpace(text) ? null : text, href));
                }
            }
            catch (TimeoutException) { /* no card, or no link in it */ }
            finally
            {
                try { await page.Mouse.MoveAsync(0, 0); await page.Keyboard.PressAsync("Escape"); await page.WaitForTimeoutAsync(250); } catch { }
            }

            if (links.Count == 0)
            {
                if (label.Length > 0 && seen.Add(label)) found.Add(new() { ["text"] = label, ["href"] = null });
                continue;
            }
            foreach (var (text, href) in links)
                if (seen.Add(href)) found.Add(new() { ["text"] = text ?? label, ["href"] = href });
        }
        return found;
    }

    private static async Task<Outcome> ClassifyPageAsync(IPage page)
    {
        string body = "";
        try { body = await page.InnerTextAsync("body", new() { Timeout = 3000 }); }
        catch (TimeoutException) { }

        if (page.Url.Contains("accounts.google.com")) return Outcome.Login;
        if (ChallengeText.Any(t => body.Contains(t, StringComparison.OrdinalIgnoreCase))) return Outcome.Challenge;
        if (await page.Locator(Composer).CountAsync() > 0) return Outcome.Answered;
        if (LoginText.Any(t => body.Contains(t, StringComparison.Ordinal))) return Outcome.Login;
        return Outcome.Unknown;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _pw?.Dispose();
    }
}
