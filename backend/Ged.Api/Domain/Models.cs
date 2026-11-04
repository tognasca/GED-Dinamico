using System.Text.Json;

namespace Ged.Api.Domain;

public class TenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DocumentType : TenantOwned
{
    public string Name { get; set; } = "";
    public string KeywordsJson { get; set; } = "[]";
    public string FieldsJson { get; set; } = "[]";
    public int Version { get; set; } = 1;
    public bool Active { get; set; } = true;
    public List<FieldDefinition> Fields => JsonSerializer.Deserialize<List<FieldDefinition>>(FieldsJson, JsonOptions.Default) ?? [];
    public List<string> Keywords => JsonSerializer.Deserialize<List<string>>(KeywordsJson, JsonOptions.Default) ?? [];
}

public sealed record FieldDefinition(string Key, string Label, string DataType, bool Required, string? Regex = null, string? Anchor = null);
public sealed record FieldResult(string Value, double Confidence, string Source);

public sealed class GedDocument : TenantOwned
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long FileSize { get; set; }
    public string OcrText { get; set; } = "";
    public string OcrStatus { get; set; } = "Pending";
    public string Status { get; set; } = "Uploaded";
    public Guid? DocumentTypeId { get; set; }
    public int? DocumentTypeVersion { get; set; }
    public Guid? BatchId { get; set; }
    public string ValuesJson { get; set; } = "{}";
    public string SuggestionsJson { get; set; } = "{}";
    public Guid? PhysicalBoxId { get; set; }
}

public sealed class ProcessingBatch : TenantOwned
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Open";
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class PhysicalLocation : TenantOwned
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Location";
    public Guid? ParentId { get; set; }
}

public sealed class PhysicalBox : TenantOwned
{
    public string Code { get; set; } = "";
    public Guid LocationId { get; set; }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}
