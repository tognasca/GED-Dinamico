using System.Text.Json;
using Ged.Api.Data;
using Ged.Api.Domain;
using Ged.Api.Services;
using Microsoft.EntityFrameworkCore;

internal static class LayerChecks
{
    public static async Task<int> RunAsync()
    {
        CheckEvidence();
        var connection = Environment.GetEnvironmentVariable("GED_TEST_DATABASE");
        if (!string.IsNullOrWhiteSpace(connection)) await CheckSearchAsync(connection);
        Console.WriteLine("Validação da camada concluída.");
        return 0;
    }

    private static void CheckEvidence()
    {
        using var json = JsonDocument.Parse("""
            {"texts":[{"text":"Número da apólice: 202600123",
              "prov":[{"page_no":1,"charspan":[0,28],
              "bbox":{"l":10,"t":20,"r":200,"b":40,"coord_origin":"TOPLEFT"}}]}],
             "tables":[{"prov":[{"page_no":2}],"data":{"grid":[
               [{"text":"Placa"},{"text":"Início da vigência"}],
               [{"text":"TST1A22"},{"text":"10/11/2026"}]
             ]}}]}
            """);
        var normalizer = new EvidenceExtraction(new DocumentExtraction());
        var doc = new GedDocument();
        var fields = new FieldDefinition[]
        {
            new("numero_apolice", "Número da apólice", "text", true),
            new("placa", "Placa", "text", false),
            new("inicio_vigencia", "Início da vigência", "date", true)
        };
        var evidence = TesseractOcrProvider.ReadDoclingEvidence(json.RootElement);
        normalizer.Store(doc, new("Número da apólice: 202600123", "docling", evidence));
        var values = normalizer.Extract(doc, fields);
        Require(values["numero_apolice"].Evidence is { Page: 1, Bounds.Left: 10 }, "Proveniência do texto");
        Require(values["placa"] is { Value: "TST1A22", Evidence.Page: 2 }, "Recuperação de tabela");
        Require(values["inicio_vigencia"] is { Value: "10/11/2026", Evidence.Page: 2 }, "Recuperação da data");

        normalizer.Store(doc, OcrResult.FromPages(["Placa: TST1A22", "Placa: TRT9B71"], "tesseract"));
        Require(normalizer.Extract(doc, fields)["placa"] is { Value: "", Source: "ambiguous-value" }, "Valores conflitantes");

        normalizer.Store(doc, OcrResult.FromPages(["Placa:", "TST1A22"], "tesseract"));
        Require(normalizer.Extract(doc, fields)["placa"].Value == "", "Não cruzar páginas ao preencher campos");
        Console.WriteLine("4 cenários de evidência aprovados.");
    }

    private static async Task CheckSearchAsync(string connection)
    {
        await using var db = new GedDbContext(new DbContextOptionsBuilder<GedDbContext>().UseNpgsql(connection).Options);
        Require(await db.Database.EnsureCreatedAsync(), "Use um banco descartável e vazio para o teste.");
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "Documents" DROP COLUMN IF EXISTS "OcrProvider", DROP COLUMN IF EXISTS "OcrEvidenceJson";
            """);
        await DocumentSearch.InstallAsync(db);
        await DocumentSearch.InstallAsync(db);
        var tenant = Guid.NewGuid();
        var batch = Guid.NewGuid();
        var type = Guid.NewGuid();
        var topics = new[] { "ambulância", "caminhonete", "residência", "odontologia", "apartamento",
            "comércio", "agricultura", "transporte", "oficina", "embarcação" };
        var documents = topics.Select(topic => new GedDocument
        {
            TenantId = tenant, BatchId = batch, DocumentTypeId = type, FileName = $"{topic}.pdf",
            OcrText = $"Apólice de seguro de {topic}", Status = "Indexed",
            ValuesJson = """{"numero_apolice":{"value":"202600123"}}"""
        }).ToArray();
        db.Documents.AddRange(documents);
        db.Documents.Add(new GedDocument { TenantId = Guid.NewGuid(), FileName = "ambulancia.pdf", OcrText = "ambulancia" });
        await db.SaveChangesAsync();

        int topFive = 0;
        foreach (var document in documents)
        {
            var query = Path.GetFileNameWithoutExtension(document.FileName);
            query = query[..^1] + "z";
            var results = await DocumentSearch.FindAsync(db, tenant, null, null, null, null,
                query, 0, 5, CancellationToken.None);
            Require(results.All(item => item.TenantId == tenant), "Busca isolada por empresa");
            if (results.Any(item => item.Id == document.Id)) topFive++;
        }
        Console.WriteLine($"Busca sintética top-5: {topFive}/{documents.Length}.");
        Require(topFive * 100 >= documents.Length * 90, "Top-5 abaixo de 90%");

        var filtered = await DocumentSearch.FindAsync(db, tenant, batch, type, "numero_apolice", "202600",
            "ambulância", 0, 5, CancellationToken.None);
        Require(filtered.FirstOrDefault()?.Id == documents[0].Id, "Ordenação e filtros combinados");
        var rejected = await DocumentSearch.FindAsync(db, tenant, batch, type, "numero_apolice", "202699",
            "ambulância", 0, 5, CancellationToken.None);
        Require(rejected.Count == 0, "Filtro por valor confirmado");
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }
}
