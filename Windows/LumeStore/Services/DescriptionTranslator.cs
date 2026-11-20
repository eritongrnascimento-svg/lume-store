using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace LumeStore.Services;

internal sealed class DescriptionTranslator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly string? key = Environment.GetEnvironmentVariable("LUME_TRANSLATOR_KEY");
    private readonly string? region = Environment.GetEnvironmentVariable("LUME_TRANSLATOR_REGION");
    private readonly SemaphoreSlim requests = new(2);
    private readonly ConcurrentDictionary<string, string> cache = new(StringComparer.Ordinal);

    public async Task<string> Translate(string text, string locale, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(text) || text.Length > 10000 ||
            locale.Equals("pt", StringComparison.OrdinalIgnoreCase) || locale.StartsWith("pt-", StringComparison.OrdinalIgnoreCase))
            return text;

        if (cache.TryGetValue(text, out var translated)) return translated;
        await requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(text, out translated)) return translated;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=pt-br");
            request.Headers.Add("Ocp-Apim-Subscription-Key", key);
            if (!string.IsNullOrWhiteSpace(region)) request.Headers.Add("Ocp-Apim-Subscription-Region", region);
            request.Content = JsonContent.Create(new[] { new { Text = text } });
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return text;
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var result = document.RootElement[0];
            var detected = result.GetProperty("detectedLanguage").GetProperty("language").GetString();
            translated = detected is "pt" or "pt-br" ? text : result.GetProperty("translations")[0].GetProperty("text").GetString();
            return cache[text] = string.IsNullOrWhiteSpace(translated) ? text : translated;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return text; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or FormatException)
        {
            return text;
        }
        finally { requests.Release(); }
    }
}
