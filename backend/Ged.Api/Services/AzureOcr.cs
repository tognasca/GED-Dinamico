using System.Net.Http.Headers;
using System.Text.Json;

namespace Ged.Api.Services;

internal static class AzureOcr
{
    public static async Task<OcrResult> ReadAsync(HttpClient client, Uri endpoint, string key, string path, CancellationToken ct)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidOperationException("Azure OCR requires an HTTPS endpoint.");

        var analyze = new Uri(endpoint, "/documentintelligence/documentModels/prebuilt-read:analyze?api-version=2024-11-30&pages=1-30");
        using var request = new HttpRequestMessage(HttpMethod.Post, analyze);
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Content = new StreamContent(File.OpenRead(path));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        using var started = await client.SendAsync(request, ct);
        started.EnsureSuccessStatusCode();
        if (!started.Headers.TryGetValues("Operation-Location", out var locations) ||
            !Uri.TryCreate(locations.FirstOrDefault(), UriKind.Absolute, out var operation) ||
            operation.Scheme != endpoint.Scheme || operation.Authority != endpoint.Authority ||
            !string.IsNullOrEmpty(operation.UserInfo))
            throw new InvalidDataException("Azure OCR returned an invalid operation URL.");

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            using var poll = new HttpRequestMessage(HttpMethod.Get, operation);
            poll.Headers.Add("Ocp-Apim-Subscription-Key", key);
            using var response = await client.SendAsync(poll, ct);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            var root = json.RootElement;
            switch (root.GetProperty("status").GetString())
            {
                case "notStarted":
                case "running":
                    continue;
                case "succeeded":
                    return Normalize(root.GetProperty("analyzeResult"));
                default:
                    throw new InvalidDataException("Azure OCR failed.");
            }
        }
    }

    private static OcrResult Normalize(JsonElement result)
    {
        var pages = new List<string>();
        var evidence = new List<OcrEvidence>();
        foreach (var page in result.GetProperty("pages").EnumerateArray())
        {
            var number = page.GetProperty("pageNumber").GetInt32();
            if (!page.TryGetProperty("lines", out var lines)) continue;
            var text = new List<string>();
            foreach (var line in lines.EnumerateArray())
            {
                if (line.GetProperty("content").GetString() is not { Length: > 0 } content) continue;
                text.Add(content);
                evidence.Add(new(content, number, Bounds(line)));
            }
            if (page.TryGetProperty("words", out var words))
            {
                foreach (var word in words.EnumerateArray())
                {
                    if (word.GetProperty("content").GetString() is not { Length: > 0 } content) continue;
                    var confidence = word.TryGetProperty("confidence", out var score) ? score.GetDouble() : (double?)null;
                    evidence.Add(new(content, number, Bounds(word), confidence));
                }
            }
            pages.Add(string.Join('\n', text));
        }
        return new(string.Join("\n\f\n", pages), "azure-read", evidence);
    }

    private static OcrBounds? Bounds(JsonElement item)
    {
        if (!item.TryGetProperty("polygon", out var polygon) || polygon.GetArrayLength() < 8) return null;
        var points = polygon.EnumerateArray().Select(point => point.GetDouble()).ToArray();
        var x = points.Where((_, index) => index % 2 == 0);
        var y = points.Where((_, index) => index % 2 == 1);
        return new(x.Min(), y.Min(), x.Max(), y.Max(), "TOPLEFT");
    }
}
