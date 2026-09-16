namespace Novaritz.WebCapture;

/// <summary>
/// One LLM's web front-end driven through a browser. ChatGPT and Gemini each
/// implement this; the run loop in Program.cs does not know which it has.
/// </summary>
public interface IWebLlmClient : IAsyncDisposable
{
    /// <summary>Open a browser using the saved login. Fails clearly if there is none.</summary>
    Task OpenAsync(bool headless);

    /// <summary>Submit one prompt in a fresh conversation and wait for the answer to finish.</summary>
    Task<AskResult> AskAsync(string promptText);

    Task ScreenshotAsync(string path);

    /// <summary>Write rotated cookies back so the next run is still signed in.</summary>
    Task SaveSessionAsync();
}

/// <summary>Picks the client for an LLMType and the session file it uses.</summary>
public static class WebClients
{
    public static readonly string[] Supported = { "chatgpt", "gemini" };

    /// <summary>
    /// chatgpt keeps the historical file name (storage_state.json); every other
    /// LLM gets its own file beside it (storage_state.gemini.json), so two
    /// logins never overwrite each other.
    /// </summary>
    public static string StatePathFor(string llmType, string configuredStatePath)
    {
        if (llmType == "chatgpt") return configuredStatePath;
        var dir = Path.GetDirectoryName(configuredStatePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(configuredStatePath);
        var ext = Path.GetExtension(configuredStatePath);
        return Path.Combine(dir, $"{name}.{llmType}{ext}");
    }

    public static IWebLlmClient Create(string llmType, string statePath, int answerTimeoutSeconds, string? channel) =>
        llmType switch
        {
            "chatgpt" => new ChatGptWebClient(statePath, answerTimeoutSeconds, channel),
            "gemini" => new GeminiWebClient(statePath, answerTimeoutSeconds, channel),
            _ => throw new ArgumentException($"No web client for LLMType '{llmType}'. Supported: {string.Join(", ", Supported)}."),
        };

    public static Task LoginInteractiveAsync(string llmType, string statePath, string? channel) =>
        llmType switch
        {
            "chatgpt" => ChatGptWebClient.LoginInteractiveAsync(statePath, channel),
            "gemini" => GeminiWebClient.LoginInteractiveAsync(statePath, channel),
            _ => throw new ArgumentException($"No web client for LLMType '{llmType}'. Supported: {string.Join(", ", Supported)}."),
        };
}
