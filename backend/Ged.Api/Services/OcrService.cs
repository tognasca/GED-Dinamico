using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace Ged.Api.Services;

public interface IOcrProvider
{
    Task<OcrResult> ReadAsync(string fileName, string contentType, Stream file, CancellationToken ct);
}

public sealed class TesseractOcrProvider(IConfiguration configuration, IHttpClientFactory clients, ILogger<TesseractOcrProvider> logger) : IOcrProvider
{
    public async Task<OcrResult> ReadAsync(string fileName, string contentType, Stream file, CancellationToken ct)
    {
        if (contentType == "text/plain")
        {
            using var reader = new StreamReader(file, Encoding.UTF8, true, leaveOpen: true);
            return new(await reader.ReadToEndAsync(ct), "text", []);
        }
        var isPdf = contentType == "application/pdf";
        if (!isPdf && contentType is not ("image/png" or "image/jpeg" or "image/tiff" or "image/webp"))
            throw new NotSupportedException("Format not supported for OCR");
        var temp = Path.Combine(Path.GetTempPath(), $"gedocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var input = Path.Combine(temp, isPdf ? "input.pdf" : "input.img");
            await using (var output = File.Create(input)) await file.CopyToAsync(output, ct);
            var pages = new List<string>();
            if (isPdf)
            {
                try
                {
                    var embedded = await Execute(configuration["Ocr:PdfToText"] ?? "pdftotext",
                        ["-f", "1", "-l", "30", "-layout", "-enc", "UTF-8", input, "-"], ct);
                    var pagesWithText = embedded.Split('\f').ToList();
                    if (pagesWithText.Count > 0 && pagesWithText[^1].Length == 0)
                        pagesWithText.RemoveAt(pagesWithText.Count - 1);
                    if (pagesWithText.Count > 0 && pagesWithText.All(page => page.Count(char.IsLetterOrDigit) >= 30))
                        return OcrResult.FromPages(pagesWithText, "poppler");
                }
                catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning("PDF text extraction failed ({ErrorType}); continuing with OCR", error.GetType().Name);
                }

                if (!string.IsNullOrWhiteSpace(configuration["Ocr:DoclingUrl"]))
                {
                    try
                    {
                        return await ReadWithDoclingAsync(input, ct);
                    }
                    catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        logger.LogWarning("Docling failed ({ErrorType}); continuing with Tesseract", error.GetType().Name);
                    }
                }

                if (!string.IsNullOrWhiteSpace(configuration["Ocr:PaddleUrl"]))
                {
                    try
                    {
                        return await ReadWithDoclingAsync(input, ct, configuration["Ocr:PaddleUrl"]);
                    }
                    catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        logger.LogWarning("Paddle OCR failed ({ErrorType}); continuing with Tesseract", error.GetType().Name);
                    }
                }

            }

            try
            {
                if (isPdf)
                {
                    await Execute(configuration["Ocr:PdfToPpm"] ?? "pdftoppm",
                        ["-f", "1", "-l", "30", "-r", "200", "-png", input, Path.Combine(temp, "page")], ct);
                    pages.AddRange(Directory.GetFiles(temp, "page-*.png").OrderBy(x => x, StringComparer.Ordinal));
                }
                else pages.Add(input);

                var result = new List<string>();
                foreach (var page in pages)
                    result.Add(await Execute(configuration["Ocr:Tesseract"] ?? "tesseract",
                        [page, "stdout", "-l", configuration["Ocr:Languages"] ?? "por+eng"], ct));
                if (result.Any(text => !string.IsNullOrWhiteSpace(text))) return OcrResult.FromPages(result, "tesseract");
                throw new InvalidDataException("No readable text found.");
            }
            catch (Exception error) when (isPdf && configuration.GetValue<bool>("Ocr:Azure:Enabled") &&
                (error is not OperationCanceledException || !ct.IsCancellationRequested))
            {
                var key = configuration["Ocr:Azure:Key"];
                if (string.IsNullOrWhiteSpace(key) ||
                    !Uri.TryCreate(configuration["Ocr:Azure:Endpoint"], UriKind.Absolute, out var endpoint))
                    throw new InvalidOperationException("Azure OCR is enabled but its endpoint or key is missing.");

                logger.LogWarning("Local OCR failed ({ErrorType}); continuing with Azure Read", error.GetType().Name);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                using var client = clients.CreateClient("AzureOcr");
                try { return await AzureOcr.ReadAsync(client, endpoint, key, input, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("Azure OCR did not finish within the configured time budget.");
                }
            }
        }
        finally { Directory.Delete(temp, true); }
    }

    public async Task<IReadOnlyList<OcrWord>> ReadPdfPageAsync(Stream file, int page, CancellationToken ct)
    {
        var directory = Directory.CreateTempSubdirectory("ged-page-");
        try
        {
            var input = Path.Combine(directory.FullName, "input.pdf");
            await using (var output = File.Create(input))
                await file.CopyToAsync(output, ct);
            var outputBase = Path.Combine(directory.FullName, "page");
            var number = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Execute(configuration["Ocr:PdfToPpm"] ?? "pdftoppm",
                ["-f", number, "-l", number, "-singlefile", "-scale-to", "2200", "-png", input, outputBase], ct);
            if (!File.Exists(outputBase + ".png")) return [];
            var tsv = await Execute(configuration["Ocr:Tesseract"] ?? "tesseract",
                [outputBase + ".png", "stdout", "-l", configuration["Ocr:Languages"] ?? "por+eng", "tsv"], ct);
            var words = new List<OcrWord>();
            int pageWidth = 0, pageHeight = 0;
            foreach (var line in tsv.Split('\n').Skip(1))
            {
                var columns = line.Split('\t', 12);
                if (columns.Length != 12 || !int.TryParse(columns[6], out var left) ||
                    !int.TryParse(columns[7], out var top) || !int.TryParse(columns[8], out var width) ||
                    !int.TryParse(columns[9], out var height)) continue;
                if (columns[0] == "1") (pageWidth, pageHeight) = (width, height);
                if (columns[0] != "5" || pageWidth <= 0 || pageHeight <= 0 ||
                    string.IsNullOrWhiteSpace(columns[11])) continue;
                words.Add(new OcrWord(columns[11].Trim(), (double)left / pageWidth, (double)top / pageHeight,
                    (double)width / pageWidth, (double)height / pageHeight));
            }
            return words;
        }
        finally { directory.Delete(recursive: true); }
    }

    private async Task<OcrResult> ReadWithDoclingAsync(string path, CancellationToken ct, string? serviceUrl = null)
    {
        using var client = clients.CreateClient("Docling");
        using var form = new MultipartFormDataContent();
        await using var source = File.OpenRead(path);
        var upload = new StreamContent(source);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(upload, "files", "document.pdf");
        form.Add(new StringContent("text"), "to_formats");
        if (serviceUrl is null) form.Add(new StringContent("json"), "to_formats");
        form.Add(new StringContent("true"), "do_ocr");
        form.Add(new StringContent("1"), "page_range");
        form.Add(new StringContent("30"), "page_range");
        var endpoint = (serviceUrl ?? configuration["Ocr:DoclingUrl"])!.TrimEnd('/') + "/v1/convert/file";
        using var response = await client.PostAsync(endpoint, form, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var result = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        if (result.RootElement.GetProperty("status").GetString() == "success" &&
            result.RootElement.GetProperty("document").GetProperty("text_content").GetString() is { } text &&
            !string.IsNullOrWhiteSpace(text))
        {
            var document = result.RootElement.GetProperty("document");
            var evidence = serviceUrl is null && document.TryGetProperty("json_content", out var structure)
                ? ReadDoclingEvidence(structure) : [];
            return new(text, serviceUrl is null ? "docling" : "paddle", evidence);
        }

        throw new InvalidDataException("Docling did not return a complete text result.");
    }

    public static List<OcrEvidence> ReadDoclingEvidence(JsonElement structure)
    {
        var evidence = new List<OcrEvidence>();
        if (structure.ValueKind != JsonValueKind.Object) return evidence;
        if (structure.TryGetProperty("texts", out var texts) && texts.ValueKind == JsonValueKind.Array)
        foreach (var item in texts.EnumerateArray())
        {
            if (!item.TryGetProperty("text", out var raw) || raw.GetString() is not { Length: > 0 } text ||
                !item.TryGetProperty("prov", out var provenance) || provenance.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var entry in provenance.EnumerateArray())
            {
                if (!entry.TryGetProperty("page_no", out var page) || !page.TryGetInt32(out var number) || number < 1)
                    continue;
                var fragment = text;
                if (entry.TryGetProperty("charspan", out var span) && span.ValueKind == JsonValueKind.Array && span.GetArrayLength() == 2)
                {
                    // Docling offsets count Unicode code points.
                    var characters = text.EnumerateRunes().ToArray();
                    if (span[0].TryGetInt32(out var start) && span[1].TryGetInt32(out var end) &&
                        start >= 0 && end > start && end <= characters.Length)
                        fragment = string.Concat(characters[start..end].Select(rune => rune.ToString()));
                }
                evidence.Add(new(fragment, number, ReadBounds(entry)));
            }
        }
        if (structure.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Array)
            foreach (var table in tables.EnumerateArray())
                ReadDoclingTable(table, evidence);
        return evidence;
    }

    private static OcrBounds? ReadBounds(JsonElement entry)
    {
        if (entry.TryGetProperty("bbox", out var box) && box.ValueKind == JsonValueKind.Object &&
            box.TryGetProperty("l", out var left) && box.TryGetProperty("t", out var top) &&
            box.TryGetProperty("r", out var right) && box.TryGetProperty("b", out var bottom) &&
            box.TryGetProperty("coord_origin", out var origin) && origin.GetString() is { } coordinates)
            return new(left.GetDouble(), top.GetDouble(), right.GetDouble(), bottom.GetDouble(), coordinates);
        return null;
    }

    private static void ReadDoclingTable(JsonElement table, List<OcrEvidence> evidence)
    {
        if (!table.TryGetProperty("prov", out var pages) || pages.ValueKind != JsonValueKind.Array ||
            pages.GetArrayLength() != 1 || !pages[0].TryGetProperty("page_no", out var page) ||
            !page.TryGetInt32(out var number) || number < 1 ||
            !table.TryGetProperty("data", out var data) || !data.TryGetProperty("grid", out var grid) ||
            grid.ValueKind != JsonValueKind.Array) return;

        var rows = grid.EnumerateArray().Select(row => row.EnumerateArray().ToArray()).ToArray();
        for (var row = 0; row < rows.Length; row++)
        for (var column = 0; column < rows[row].Length; column++)
        {
            var label = CellText(rows[row][column]).TrimEnd(':');
            if (label.Length == 0) continue;
            if (column + 1 < rows[row].Length) Add(rows[row][column + 1]);
            if (row + 1 < rows.Length && column < rows[row + 1].Length) Add(rows[row + 1][column]);

            void Add(JsonElement cell)
            {
                var value = CellText(cell);
                if (value.Length > 0)
                    evidence.Add(new(value, number, ReadBounds(cell), Label: label));
            }
        }
    }

    private static string CellText(JsonElement cell) =>
        cell.TryGetProperty("text", out var text) ? text.GetString()?.Trim() ?? "" : "";

    private static async Task<string> Execute(string command, IEnumerable<string> args, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new InvalidOperationException($"Failed to start {command}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"{command} failed: {error[..Math.Min(error.Length, 500)]}");
            return text;
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    }
}

public sealed record OcrWord(string Text, double Left, double Top, double Width, double Height);
