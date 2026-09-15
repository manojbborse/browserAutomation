namespace Novaritz.WebCapture;

/// <summary>Settings bound from the "Capture" section of appsettings.json.</summary>
public sealed class CaptureOptions
{
    /// <summary>
    /// Max prompts per start, across brands (a brand's set is ~14). A brand cut
    /// off by this limit stays PENDING and is resumed by the next start.
    /// </summary>
    public int BatchLimit { get; set; } = 30;

    /// <summary>Seconds between prompts. Under 20 invites rate limiting.</summary>
    public double PauseSeconds { get; set; } = 30;

    /// <summary>How long to wait for one answer to finish streaming.</summary>
    public int AnswerTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Playwright storage state — the saved ChatGPT login. Shared with the
    /// Python prototype by default (same file format). Treat it as a password.
    /// </summary>
    public string StatePath { get; set; } = "state/storage_state.json";

    /// <summary>Where run logs and failure screenshots go.</summary>
    public string ResultsPath { get; set; } = "results";

    /// <summary>
    /// Which browser Playwright drives: "chrome" or "msedge" for the copy already
    /// installed on the machine, or empty for Playwright's own bundled Chromium.
    /// Defaults to the installed Chrome: the bundled build failed to start on the
    /// first Windows PC this ran on (side-by-side manifest error), and a real
    /// branded browser is also less likely to be challenged.
    /// </summary>
    public string BrowserChannel { get; set; } = "chrome";

    /// <summary>
    /// Written to LLM_web_responses.LLMType on every row this tool saves, and
    /// used to decide which prompts are still pending for it. This tool drives
    /// chatgpt.com, so "chatgpt"; a Claude or Gemini capture would use its own.
    /// </summary>
    public string LlmType { get; set; } = "chatgpt";
}
