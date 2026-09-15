using System.Diagnostics;
using Microsoft.Playwright;

namespace Novaritz.WebCapture;

/// <summary>What one attempt to answer a prompt ended in.</summary>
public enum Outcome { Answered, Challenge, Login, Unknown, Timeout, NoAnswer, Error }

public sealed record AskResult(Outcome Outcome, double Seconds, string? Answer = null, string? Error = null,
                               string? RawText = null, string? CitationsJson = null, string? PlacesJson = null)
{
    /// <summary>The page is no longer the chat; the run should stop rather than retry.</summary>
    public bool ShouldStopRun => Outcome is Outcome.Challenge or Outcome.Login;
}

/// <summary>
/// The browser side: submit one prompt on chatgpt.com and return the answer.
///
/// Everything that touches the page lives in this class, because it is the
/// part that changes without notice. When every prompt suddenly comes back
/// <see cref="Outcome.Unknown"/>, the selectors below are where to look first.
///
/// No attempt is made to disguise the browser. If chatgpt.com shows a bot
/// challenge or a login wall, that is reported as an outcome and the run
/// stops — it is a measurement, and the fix is operational (a headed browser,
/// a different network, a refreshed session), not code that pretends to be
/// a person.
/// </summary>
public sealed class ChatGptWebClient : IAsyncDisposable
{
    private const string Home = "https://chatgpt.com/";

    // Selectors, in one place.
    private const string Composer = "#prompt-textarea";
    private const string Send = "button[data-testid=\"send-button\"]";
    private const string Stop = "button[data-testid=\"stop-button\"]";
    private const string Assistant = "[data-message-author-role=\"assistant\"]";
    // ChatGPT renders widgets INSIDE the .markdown prose: a map
    // (div.not-prose[data-testid=businesses-map-widget]), place cards
    // (span.contents holding a rounded-md rating block like "4.3 • Housing
    // society • Open Website • Directions") and citation pills
    // ([data-testid=webpage-citation-pill]). Their captions came through
    // InnerText as noise. This script, run against the live message, lifts
    // citations and place cards out as structured data, removes them from the
    // DOM, and returns the remaining prose. It runs after RawText is captured
    // and the page is discarded per prompt, so mutating it is safe.
    private const string ExtractScript = """
        el => {
          const md = el.querySelector('.markdown') || el;
          const clean = t => (t || '').replace(/\s+/g, ' ').trim();
          const citations = [];
          md.querySelectorAll('[data-testid="webpage-citation-pill"]').forEach(p => {
            const a = p.querySelector('a');
            citations.push({ text: clean(p.innerText), href: a ? a.href : null });
            p.remove();
          });
          const places = [];
          md.querySelectorAll('span.contents').forEach(sp => {
            const block = sp.querySelector('div.rounded-md');
            if (!block || !/•/.test(block.innerText || '')) return;
            const prev = sp.previousElementSibling;
            const name = prev && prev.tagName === 'P' ? clean(prev.innerText) : null;
            const m = (block.innerText || '').match(/(\d(?:\.\d)?)/);
            places.push({ name, rating: m ? parseFloat(m[1]) : null, text: clean(block.innerText) });
            sp.remove();
          });
          md.querySelectorAll('[data-testid*="widget"], .not-prose').forEach(n => n.remove());
          return { answer: md.innerText, citations, places };
        }
        """;
    private static readonly string[] ChallengeText = { "Verify you are human", "Just a moment", "Checking your browser" };
    private static readonly string[] LoginText = { "Log in", "Sign up" };

    private readonly string _statePath;
    private readonly int _answerTimeoutSeconds;
    private readonly string? _channel;
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    public ChatGptWebClient(string statePath, int answerTimeoutSeconds, string? channel)
    {
        _statePath = statePath;
        _answerTimeoutSeconds = answerTimeoutSeconds;
        _channel = string.IsNullOrWhiteSpace(channel) ? null : channel;
    }

    /// <summary>Open a browser using the saved login. Fails clearly if there is none.</summary>
    public async Task OpenAsync(bool headless)
    {
        if (!File.Exists(_statePath))
            throw new InvalidOperationException(
                $"No saved session at {_statePath} — run `dotnet run -- login` first.");

        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new() { Headless = headless, Channel = _channel });
        _context = await _browser.NewContextAsync(new() { StorageStatePath = _statePath });
        _page = await _context.NewPageAsync();
    }

    /// <summary>
    /// Interactive sign-in: opens a visible browser, waits for the user to log
    /// in by hand, then saves the session for later runs.
    /// </summary>
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

        Console.WriteLine("A browser window is open. Sign in to ChatGPT there.");
        Console.WriteLine("When the message box is visible, press Enter here to save the session.");
        Console.ReadLine();

        await context.StorageStateAsync(new() { Path = statePath });
        Console.WriteLine($"Saved session to {Path.GetFullPath(statePath)}");
    }

    /// <summary>ChatGPT rotates cookies; writing them back keeps the next run signed in.</summary>
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

    /// <summary>Submit one prompt in a fresh conversation and wait for the answer to finish.</summary>
    public async Task<AskResult> AskAsync(string promptText)
    {
        var page = _page ?? throw new InvalidOperationException("Call OpenAsync first.");
        var sw = Stopwatch.StartNew();
        double Elapsed() => Math.Round(sw.Elapsed.TotalSeconds, 1);

        await page.GotoAsync(Home, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        // The composer renders after the shell; give it up to 15 s before deciding
        // what kind of page this is. A fixed 2 s misread a slow first load as "unknown".
        try { await page.Locator(Composer).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }); }
        catch (TimeoutException) { }

        var state = await ClassifyPageAsync(page);
        if (state != Outcome.Answered)          // Answered here means "this is the chat"
            return new AskResult(state, Elapsed());

        var composer = page.Locator(Composer);
        await composer.ClickAsync();
        await composer.FillAsync(promptText);
        await page.Locator(Send).ClickAsync();

        // Streaming is over when the Stop button goes away. Wait for it to
        // appear first so a slow request is not misread as an instant finish.
        try
        {
            await page.Locator(Stop).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        }
        catch (TimeoutException) { /* very short answers can finish before we look */ }

        try
        {
            await page.Locator(Stop).WaitForAsync(new()
            {
                State = WaitForSelectorState.Hidden,
                Timeout = _answerTimeoutSeconds * 1000f,
            });
        }
        catch (TimeoutException)
        {
            return new AskResult(Outcome.Timeout, Elapsed());
        }

        await page.WaitForTimeoutAsync(1500);   // let the final render settle
        var messages = page.Locator(Assistant);
        if (await messages.CountAsync() == 0)
            return new AskResult(Outcome.NoAnswer, Elapsed());

        var last = messages.Last;
        var raw = (await last.InnerTextAsync()).Trim();
        var extracted = await last.EvaluateAsync<System.Text.Json.JsonElement>(ExtractScript);
        var answer = CleanAnswer(extracted.GetProperty("answer").GetString() ?? raw);
        var citations = extracted.GetProperty("citations");
        var places = extracted.GetProperty("places");
        return new AskResult(Outcome.Answered, Elapsed(), Answer: answer, RawText: raw,
            CitationsJson: citations.GetArrayLength() > 0 ? citations.GetRawText() : null,
            PlacesJson: places.GetArrayLength() > 0 ? places.GetRawText() : null);
    }

    // Labels of controls that sit inside the assistant message container and
    // therefore come back with InnerText. Stripped only from the very start or
    // end of the text, never from the middle, so real content is never touched.
    private static readonly string[] UiLabels = { "Give feedback", "Copy", "Good response", "Bad response", "Read aloud", "Share", "Edit" };

    private static string CleanAnswer(string raw)
    {
        var text = raw.Trim();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var label in UiLabels)
            {
                if (text.StartsWith(label, StringComparison.Ordinal))
                { text = text[label.Length..].TrimStart(); changed = true; }
                if (text.EndsWith(label, StringComparison.Ordinal))
                { text = text[..^label.Length].TrimEnd(); changed = true; }
            }
        }
        return text;
    }

    /// <summary>
    /// Is this the chat, a bot challenge, a login wall, or something else?
    /// Returns <see cref="Outcome.Answered"/> as the "yes, it's the chat" value.
    /// </summary>
    private static async Task<Outcome> ClassifyPageAsync(IPage page)
    {
        string body = "";
        try { body = await page.InnerTextAsync("body", new() { Timeout = 3000 }); }
        catch (TimeoutException) { }

        if (ChallengeText.Any(body.Contains)) return Outcome.Challenge;
        if (await page.Locator(Composer).CountAsync() > 0) return Outcome.Answered;
        if (LoginText.Any(body.Contains)) return Outcome.Login;
        return Outcome.Unknown;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _pw?.Dispose();
    }
}
