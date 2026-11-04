using System.Security.Cryptography;
using System.Text.Json;
using Ged.Api.Data;
using Ged.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ged.Api.Services;

/// <summary>
/// Polls a mounted folder and processes documents without an open browser.
/// This is a development-only importer; it uses the fixed demo tenant.
/// </summary>
public sealed class FolderImportWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<FolderImportWorker> logger) : BackgroundService
{
    private static readonly Guid DemoTenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg", [".tif"] = "image/tiff", [".tiff"] = "image/tiff",
        [".webp"] = "image/webp", [".txt"] = "text/plain"
    };
    private const long MaxBytes = 25 * 1024 * 1024;
    private readonly string _root = Path.GetFullPath(config["FolderImport:Root"] ?? "/app/import");
    private readonly Dictionary<string, (long Length, DateTime Modified)> _observed = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var incoming = Path.Combine(_root, "Entrada");
        var processed = Path.Combine(_root, "Processados");
        var errors = Path.Combine(_root, "Erros");
        Directory.CreateDirectory(incoming);
        Directory.CreateDirectory(processed);
        Directory.CreateDirectory(errors);
        logger.LogInformation("GED folder import active: {Path}", incoming);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var files = Directory.EnumerateFiles(incoming).Take(1000).ToArray();
                foreach (var file in files)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Exists) continue;
                        var state = (info.Length, info.LastWriteTimeUtc);
                        // Wait for the file to stop changing across two polls.
                        if (!_observed.TryGetValue(file, out var previous) || previous != state)
                        {
                            _observed[file] = state;
                            continue;
                        }
                        if (DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromSeconds(5)) continue;
                        _observed.Remove(file);
                        await ProcessAsync(file, processed, errors, stoppingToken);
                    }
                    catch (IOException ex) { logger.LogWarning(ex, "File busy: {File}", file); }
                    catch (UnauthorizedAccessException ex) { logger.LogWarning(ex, "File access denied: {File}", file); }
                }
                foreach (var key in _observed.Keys.Except(files, StringComparer.OrdinalIgnoreCase).ToArray()) _observed.Remove(key);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Folder import scan failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessAsync(string file, string processed, string errors, CancellationToken ct)
    {
        var extension = Path.GetExtension(file);
        if (!Mime.TryGetValue(extension, out var mime) || new FileInfo(file).Length is <= 0 or > MaxBytes)
        {
            Move(file, errors);
            logger.LogWarning("Rejected unsupported or oversized document: {File}", file);
            return;
        }

        await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
        source.Position = 0;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GedDbContext>();
        var exists = await db.Documents.AsNoTracking().AnyAsync(x => x.TenantId == DemoTenant && x.Sha256 == hash, ct);
        if (exists)
        {
            await source.DisposeAsync();
            Move(file, processed);
            logger.LogInformation("Duplicate document skipped: {File}", file);
            return;
        }

        var storage = scope.ServiceProvider.GetRequiredService<IStorageProvider>();
        var ocr = scope.ServiceProvider.GetRequiredService<IOcrProvider>();
        var extractor = scope.ServiceProvider.GetRequiredService<DocumentExtraction>();
        var (key, savedHash, size) = await storage.PutAsync(DemoTenant, source, ct);
        var document = new GedDocument
        {
            TenantId = DemoTenant, FileName = Path.GetFileName(file),
            ContentType = mime, FileSize = size, StorageKey = key, Sha256 = savedHash
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);
        try
        {
            await using var saved = await storage.OpenReadAsync(DemoTenant, key, ct);
            document.OcrText = await ocr.ReadAsync(document.FileName, mime, saved, ct);
            document.OcrStatus = "Completed";
            document.SuggestionsJson = JsonSerializer.Serialize(extractor.Suggest(document.OcrText), JsonOptions.Default);
            document.Status = "AwaitingTemplate";
            var templates = await db.DocumentTypes.AsNoTracking()
                .Where(x => x.TenantId == DemoTenant && x.Active).ToListAsync(ct);
            var matched = extractor.MatchType(document.OcrText, templates);
            if (matched != null)
            {
                document.DocumentTypeId = matched.Id;
                document.DocumentTypeVersion = matched.Version;
                document.ValuesJson = JsonSerializer.Serialize(extractor.Extract(document.OcrText, matched.Fields), JsonOptions.Default);
                document.Status = "NeedsReview";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            document.OcrStatus = "Failed";
            document.Status = "NeedsReview";
            document.SuggestionsJson = JsonSerializer.Serialize(new { error = "OCR failed; check logs" }, JsonOptions.Default);
            logger.LogError(ex, "OCR failed for {File}", file);
        }
        await db.SaveChangesAsync(ct);
        await source.DisposeAsync();
        Move(file, document.OcrStatus == "Completed" ? processed : errors);
        logger.LogInformation("Imported {File} as document {Id}, status {Status}", file, document.Id, document.Status);
    }

    private static void Move(string file, string destination)
    {
        Directory.CreateDirectory(destination);
        var name = Path.GetFileName(file);
        var target = Path.Combine(destination, name);
        if (File.Exists(target))
            target = Path.Combine(destination, $"{Path.GetFileNameWithoutExtension(name)}_{Guid.NewGuid():N}{Path.GetExtension(name)}");
        File.Move(file, target);
    }
}
