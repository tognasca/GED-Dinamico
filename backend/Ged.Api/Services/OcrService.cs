using System.Diagnostics;
using System.Text;

namespace Ged.Api.Services;

public interface IOcrProvider
{
    Task<string> ReadAsync(string fileName, string contentType, Stream file, CancellationToken ct);
}

public sealed class TesseractOcrProvider(IConfiguration configuration) : IOcrProvider
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
