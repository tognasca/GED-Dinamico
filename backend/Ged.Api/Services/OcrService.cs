using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace Ged.Api.Services;

public interface IOcrProvider
{
    Task<string> ReadAsync(string fileName, string contentType, Stream file, CancellationToken ct);
}

public sealed class TesseractOcrProvider(IConfiguration configuration, IHttpClientFactory clients, ILogger<TesseractOcrProvider> logger) : IOcrProvider
{
    public async Task<string> ReadAsync(string fileName, string contentType, Stream file, CancellationToken ct)
    {
        if (contentType == "text/plain")
        {
            using var reader = new StreamReader(file, Encoding.UTF8, true, leaveOpen: true);
            return await reader.ReadToEndAsync(ct);
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
                        return string.Join("\n", pagesWithText);
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

                await Execute(configuration["Ocr:PdfToPpm"] ?? "pdftoppm", ["-f", "1", "-l", "30", "-r", "200", "-png", input, Path.Combine(temp, "page")], ct);
                pages.AddRange(Directory.GetFiles(temp, "page-*.png").OrderBy(x => x, StringComparer.Ordinal));
                if (pages.Count == 0) throw new InvalidOperationException("PDF has no readable pages");
            }
            else pages.Add(input);
            var result = new StringBuilder();
            foreach (var page in pages)
            {
                result.AppendLine(await Execute(configuration["Ocr:Tesseract"] ?? "tesseract", [page, "stdout", "-l", configuration["Ocr:Languages"] ?? "por+eng"], ct));
            }
            return result.ToString();
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

    private async Task<string> ReadWithDoclingAsync(string path, CancellationToken ct, string? serviceUrl = null)
    {
        using var client = clients.CreateClient("Docling");
        using var form = new MultipartFormDataContent();
        await using var source = File.OpenRead(path);
        var upload = new StreamContent(source);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(upload, "files", "document.pdf");
        form.Add(new StringContent("text"), "to_formats");
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
            return text;

        throw new InvalidDataException("Docling did not return a complete text result.");
    }

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
