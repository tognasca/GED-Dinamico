using Ged.Api.Data;
using Ged.Api.Domain;
using Ged.Api.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<GedDbContext>(x => x.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton<IStorageProvider, LocalStorageProvider>();
builder.Services.AddSingleton<IOcrProvider, TesseractOcrProvider>();
builder.Services.AddSingleton<DocumentExtraction>();
if (builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("FolderImport:Enabled"))
    builder.Services.AddHostedService<FolderImportWorker>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("GedFrontend", policy =>
        policy
            .WithOrigins(
                "http://localhost:8087",
                "http://127.0.0.1:8087"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
    );
});
var app = builder.Build();

app.UseCors("GedFrontend");

app.UseSwagger();
app.UseSwaggerUI();

// DEVELOPMENT ONLY: tenant identification via X-Tenant-Id is NOT authentication.
// Do not expose to the Internet until proper JWT auth + tenant claims are implemented.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
    {
        if (!Guid.TryParse(ctx.Request.Headers["X-Tenant-Id"], out var tenantId) || tenantId == Guid.Empty)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "Provide a development X-Tenant-Id UUID header" });
            return;
        }
        ctx.Items["TenantId"] = tenantId;
    }
    await next();
});

var api = app.MapGroup("/api");
static Guid Tenant(HttpContext ctx) => (Guid)ctx.Items["TenantId"]!;
static object ViewDocument(GedDocument d) => new
{
    d.Id, d.FileName, d.ContentType, d.FileSize, d.OcrText, d.OcrStatus, d.Status,
    d.DocumentTypeId, d.DocumentTypeVersion, d.BatchId, d.PhysicalBoxId, d.CreatedAt,
    Values = JsonSerializer.Deserialize<Dictionary<string, FieldResult>>(d.ValuesJson, JsonOptions.Default),
    Suggestions = JsonSerializer.Deserialize<object>(d.SuggestionsJson, JsonOptions.Default)
};

api.MapGet("/health", () => Results.Ok(new { status = "ok", environment = app.Environment.EnvironmentName }));

api.MapGet("/types", async (HttpContext ctx, GedDbContext db) =>
{
    var tenant = Tenant(ctx);
    return Results.Ok((await db.DocumentTypes.AsNoTracking().Where(x => x.TenantId == tenant).OrderBy(x => x.Name).ToListAsync())
        .Select(t => new { t.Id, t.Name, t.Version, t.Active, fields = t.Fields, keywords = t.Keywords }));
});

api.MapPost("/types", async (HttpContext ctx, GedDbContext db, CreateTypeRequest request) =>
{
    var tenant = Tenant(ctx);
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100 || request.Fields is null || request.Fields.Count == 0 || request.Fields.Count > 100)
        return Results.BadRequest(new { error = "Name and 1-100 fields are required" });
    var invalidKeys = request.Fields.Where(f => string.IsNullOrWhiteSpace(f.Key) || !Regex.IsMatch(f.Key, "^[a-zA-Z][a-zA-Z0-9_]{0,63}$")).Select(f => f.Key).ToArray();
    var duplicateKeys = request.Fields.GroupBy(f => f.Key?.Trim() ?? "", StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
    if (invalidKeys.Length > 0 || duplicateKeys.Length > 0)
        return Results.BadRequest(new { error = "Invalid or duplicate field keys", invalidKeys, duplicateKeys });
    if (await db.DocumentTypes.AnyAsync(x => x.TenantId == tenant && x.Name == request.Name.Trim())) return Results.Conflict(new { error = "Document type already exists" });
    var type = new DocumentType { TenantId = tenant, Name = request.Name.Trim(), FieldsJson = JsonSerializer.Serialize(request.Fields, JsonOptions.Default), KeywordsJson = JsonSerializer.Serialize(request.Keywords ?? [], JsonOptions.Default) };
    db.DocumentTypes.Add(type);
    await db.SaveChangesAsync();
    return Results.Created($"/api/types/{type.Id}", new { type.Id, type.Name });
});

api.MapGet("/documents", async (HttpContext ctx, GedDbContext db, Guid? batchId, string? q) =>
{
    var tenant = Tenant(ctx);
    var docs = db.Documents.AsNoTracking().Where(x => x.TenantId == tenant);
    if (batchId.HasValue) docs = docs.Where(x => x.BatchId == batchId.Value);
    if (!string.IsNullOrWhiteSpace(q)) docs = docs.Where(x => x.FileName.Contains(q) || x.OcrText.Contains(q));
    return Results.Ok((await docs.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync()).Select(ViewDocument));
});

api.MapGet("/documents/{id:guid}", async (Guid id, HttpContext ctx, GedDbContext db) =>
{
    var tenant = Tenant(ctx);
    var d = await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant);
    return d is null ? Results.NotFound() : Results.Ok(ViewDocument(d));
});

api.MapPost("/documents/upload", async (HttpContext ctx, GedDbContext db, IStorageProvider storage, IOcrProvider ocr, DocumentExtraction extractor, IFormFile file, Guid? batchId, CancellationToken ct) =>
{
    var tenant = Tenant(ctx);
    const long limit = 25 * 1024 * 1024;
    if (file.Length == 0 || file.Length > limit) return Results.BadRequest(new { error = "File must be 1 byte to 25 MB" });
    var accepted = new[] { "application/pdf", "image/png", "image/jpeg", "image/tiff", "image/webp", "text/plain" };
    if (!accepted.Contains(file.ContentType)) return Results.BadRequest(new { error = "Unsupported file type" });
    if (batchId.HasValue && !await db.Batches.AnyAsync(x => x.Id == batchId && x.TenantId == tenant && x.Status == "Open"))
        return Results.BadRequest(new { error = "Batch not found or closed" });
    await using var upload = file.OpenReadStream();
    var (key, hash, size) = await storage.PutAsync(tenant, upload, ct);
    var doc = new GedDocument { TenantId = tenant, FileName = Path.GetFileName(file.FileName), ContentType = file.ContentType, FileSize = size, StorageKey = key, Sha256 = hash, BatchId = batchId };
    db.Documents.Add(doc);
    await db.SaveChangesAsync(ct);
    try
    {
        await using var source = await storage.OpenReadAsync(tenant, key, ct);
        doc.OcrText = await ocr.ReadAsync(doc.FileName, doc.ContentType, source, ct);
        doc.OcrStatus = "Completed";
        doc.SuggestionsJson = JsonSerializer.Serialize(extractor.Suggest(doc.OcrText), JsonOptions.Default);
        doc.Status = "AwaitingTemplate";
        var types = await db.DocumentTypes.AsNoTracking().Where(x => x.TenantId == tenant && x.Active).ToListAsync(ct);
        var type = extractor.MatchType(doc.OcrText, types);
        if (type != null) Apply(type, doc, extractor);
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        doc.OcrStatus = "Failed"; doc.Status = "NeedsReview";
        doc.SuggestionsJson = JsonSerializer.Serialize(new { error = "OCR failed. Check service logs and Tesseract/Poppler installation." }, JsonOptions.Default);
        app.Logger.LogError(e, "OCR failure for document {DocumentId}", doc.Id);
    }
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/documents/{doc.Id}", ViewDocument(doc));
}).DisableAntiforgery(); // Local MVP only; use token-based antiforgery with production authentication.

api.MapPost("/documents/{id:guid}/apply-template", async (Guid id, ApplyTemplateRequest request, HttpContext ctx, GedDbContext db, DocumentExtraction extraction) =>
{
    var tenant = Tenant(ctx);
    var doc = await db.Documents.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant);
    var type = await db.DocumentTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.DocumentTypeId && x.TenantId == tenant && x.Active);
    if (doc is null || type is null) return Results.NotFound();
    if (doc.OcrStatus != "Completed") return Results.BadRequest(new { error = "OCR is not complete" });
    Apply(type, doc, extraction);
    await db.SaveChangesAsync();
    return Results.Ok(ViewDocument(doc));
});

api.MapPost("/documents/{id:guid}/confirm", async (Guid id, ConfirmRequest request, HttpContext ctx, GedDbContext db) =>
{
    var tenant = Tenant(ctx);
    var doc = await db.Documents.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant);
    if (doc is null) return Results.NotFound();
    if (!doc.DocumentTypeId.HasValue) return Results.BadRequest(new { error = "Apply a document type first" });
    var type = await db.DocumentTypes.FirstOrDefaultAsync(x => x.Id == doc.DocumentTypeId && x.TenantId == tenant);
    if (type is null) return Results.NotFound();
    var allowed = type.Fields.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (request.Values.Keys.Any(k => !allowed.Contains(k))) return Results.BadRequest(new { error = "Unknown field" });
    var values = JsonSerializer.Deserialize<Dictionary<string, FieldResult>>(doc.ValuesJson, JsonOptions.Default) ?? [];
    foreach (var item in request.Values) values[item.Key] = new FieldResult(item.Value ?? "", 1, "human-reviewed");
    if (type.Fields.Any(f => f.Required && (!values.TryGetValue(f.Key, out var v) || string.IsNullOrWhiteSpace(v.Value))))
        return Results.BadRequest(new { error = "Required fields missing" });
    doc.ValuesJson = JsonSerializer.Serialize(values, JsonOptions.Default);
    doc.Status = "Indexed";
    await db.SaveChangesAsync();
    return Results.Ok(ViewDocument(doc));
});

api.MapGet("/documents/{id:guid}/file", async (Guid id, HttpContext ctx, GedDbContext db, IStorageProvider storage, CancellationToken ct) =>
{
    var tenant = Tenant(ctx);
    var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant, ct);
    if (doc is null) return Results.NotFound();
    var stream = await storage.OpenReadAsync(tenant, doc.StorageKey, ct);
    return Results.File(stream, doc.ContentType, enableRangeProcessing: true);
});

api.MapGet("/batches", async (HttpContext ctx, GedDbContext db) =>
{
    var tenant = Tenant(ctx);
    return Results.Ok(await db.Batches.Where(x => x.TenantId == tenant).OrderByDescending(x => x.CreatedAt).ToListAsync());
});
api.MapPost("/batches", async (HttpContext ctx, GedDbContext db, NameRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest();
    var batch = new ProcessingBatch { TenantId = Tenant(ctx), Name = request.Name.Trim() };
    db.Batches.Add(batch); await db.SaveChangesAsync(); return Results.Created($"/api/batches/{batch.Id}", batch);
});
api.MapPost("/batches/{id:guid}/close", async (Guid id, HttpContext ctx, GedDbContext db) =>
{
    var tenant = Tenant(ctx);
    var batch = await db.Batches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant);
    if (batch == null) return Results.NotFound();
    if (await db.Documents.AnyAsync(x => x.BatchId == id && x.TenantId == tenant && x.Status != "Indexed")) return Results.Conflict(new { error = "Review pending documents before closing" });
    batch.Status = "Completed"; batch.CompletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); return Results.Ok(batch);
});

api.MapGet("/locations", async (HttpContext ctx, GedDbContext db) =>
{
    var tenant = Tenant(ctx);
    return Results.Ok(await db.Locations.Where(x => x.TenantId == tenant).OrderBy(x => x.Name).ToListAsync());
});
api.MapPost("/locations", async (HttpContext ctx, GedDbContext db, LocationRequest request) =>
{
    var tenant = Tenant(ctx);
    if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest();
    if (request.ParentId.HasValue && !await db.Locations.AnyAsync(x => x.Id == request.ParentId && x.TenantId == tenant)) return Results.BadRequest(new { error = "Parent not found" });
    var location = new PhysicalLocation { TenantId = tenant, Name = request.Name, Kind = request.Kind ?? "Location", ParentId = request.ParentId };
    db.Locations.Add(location); await db.SaveChangesAsync(); return Results.Created($"/api/locations/{location.Id}", location);
});
api.MapGet("/boxes", async (HttpContext ctx, GedDbContext db) =>
{
    var tenant = Tenant(ctx);
    return Results.Ok(await db.Boxes.Where(x => x.TenantId == tenant).OrderBy(x => x.Code).ToListAsync());
});
api.MapPost("/boxes", async (HttpContext ctx, GedDbContext db, BoxRequest request) =>
{
    var tenant = Tenant(ctx);
    if (string.IsNullOrWhiteSpace(request.Code) || !await db.Locations.AnyAsync(x => x.Id == request.LocationId && x.TenantId == tenant)) return Results.BadRequest();
    var box = new PhysicalBox { TenantId = tenant, Code = request.Code, LocationId = request.LocationId };
    db.Boxes.Add(box); await db.SaveChangesAsync(); return Results.Created($"/api/boxes/{box.Id}", box);
});
api.MapPost("/documents/{id:guid}/box", async (Guid id, AssignBoxRequest request, HttpContext ctx, GedDbContext db) =>
{
    var tenant = Tenant(ctx);
    var doc = await db.Documents.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant);
    if (doc == null || !await db.Boxes.AnyAsync(x => x.Id == request.BoxId && x.TenantId == tenant)) return Results.NotFound();
    doc.PhysicalBoxId = request.BoxId; await db.SaveChangesAsync(); return Results.Ok(ViewDocument(doc));
});
api.MapGet("/boxes/{id:guid}/route", async (Guid id, HttpContext ctx, GedDbContext db) =>
{
    var tenant = Tenant(ctx);
    var box = await db.Boxes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant);
    if (box == null) return Results.NotFound();
    var locations = await db.Locations.AsNoTracking().Where(x => x.TenantId == tenant).ToDictionaryAsync(x => x.Id);
    var route = new List<object>();
    var visited = new HashSet<Guid>();
    Guid? current = box.LocationId;
    while (current.HasValue && locations.TryGetValue(current.Value, out var node) && visited.Add(node.Id))
    {
        route.Insert(0, new { node.Id, node.Name, node.Kind }); current = node.ParentId;
    }
    return Results.Ok(new { box.Id, box.Code, route });
});

static void Apply(DocumentType type, GedDocument doc, DocumentExtraction extractor)
{
    var values = extractor.Extract(doc.OcrText, type.Fields);
    doc.DocumentTypeId = type.Id;
    doc.DocumentTypeVersion = type.Version;
    doc.ValuesJson = JsonSerializer.Serialize(values, JsonOptions.Default);
    // Heuristic extracted values are NEVER auto-approved in the starter; explicit user review required.
    doc.Status = "NeedsReview";
}

// Starter schema creation. Replace with versioned EF migrations before production use.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GedDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (app.Environment.IsDevelopment())
        await InsuranceTemplateSeeder.SeedAsync(db);
}
app.Run();

public sealed record CreateTypeRequest(string Name, List<FieldDefinition> Fields, List<string>? Keywords);
public sealed record ApplyTemplateRequest(Guid DocumentTypeId);
public sealed record ConfirmRequest(Dictionary<string, string?> Values);
public sealed record NameRequest(string Name);
public sealed record LocationRequest(string Name, string? Kind, Guid? ParentId);
public sealed record BoxRequest(string Code, Guid LocationId);
public sealed record AssignBoxRequest(Guid BoxId);
