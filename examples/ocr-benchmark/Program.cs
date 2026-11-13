using System.Net.Http.Headers;
using System.Text.Json;
using Ged.Api.Domain;
using Ged.Api.Services;

var fixtures = args.Length > 0 ? args[0] : "fixtures";
using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
var endpoint = (Environment.GetEnvironmentVariable("DOCLING_URL") ?? "http://localhost:5001")
    .TrimEnd('/') + "/v1/convert/file";
var expected = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
    await File.ReadAllTextAsync(Path.Combine(fixtures, "expected.json")))!;
var fields = new FieldDefinition[]
{
    new("numero_apolice", "Número da apólice", "text", true),
    new("placa", "Placa", "text", true),
    new("inicio_vigencia", "Início da vigência", "date", true)
};
var extractor = new DocumentExtraction();
int baselineHits = 0, structuredHits = 0, wrongPolicyNumbers = 0;
foreach (var (file, truth) in expected)
{
    using var form = new MultipartFormDataContent();
    var pdf = new StreamContent(File.OpenRead(Path.Combine(fixtures, file)));
    pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
    form.Add(pdf, "files", file);
    foreach (var format in new[] { "text", "json" })
        form.Add(new StringContent(format), "to_formats");
    form.Add(new StringContent("true"), "do_ocr");
    form.Add(new StringContent("tesseract"), "ocr_engine");
    foreach (var language in new[] { "por", "eng" })
        form.Add(new StringContent(language), "ocr_lang");
    using var response = await client.PostAsync(endpoint, form);
    response.EnsureSuccessStatusCode();
    using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    if (result.RootElement.GetProperty("status").GetString() != "success")
        throw new InvalidDataException($"Conversão incompleta: {file}");

    var document = result.RootElement.GetProperty("document");
    var text = document.GetProperty("text_content").GetString() ?? "";
    var baseline = extractor.Extract(text, fields);
    var structured = extractor.Extract(text, fields);
    var json = document.GetProperty("json_content");
    if (json.TryGetProperty("tables", out var tables))
    {
        foreach (var table in tables.EnumerateArray())
        {
            var grid = table.GetProperty("data").GetProperty("grid");
            var rows = grid.EnumerateArray().Select(row => row.EnumerateArray()
                .Select(cell => cell.GetProperty("text").GetString()?.Trim() ?? "").ToArray()).ToArray();
            foreach (var field in fields)
            {
                var candidates = new HashSet<string>();
                for (var row = 0; row < rows.Length; row++)
                for (var col = 0; col < rows[row].Length; col++)
                {
                    if (!string.Equals(rows[row][col].TrimEnd(':'), field.Label, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (col + 1 < rows[row].Length) candidates.Add(rows[row][col + 1]);
                    if (row + 1 < rows.Length && col < rows[row + 1].Length) candidates.Add(rows[row + 1][col]);
                }
                candidates.RemoveWhere(value => !MatchesFormat(field.Key, value));
                if (candidates.Count == 1 && string.IsNullOrWhiteSpace(structured[field.Key].Value))
                    structured[field.Key] = new FieldResult(candidates.Single(), 0.75, "table");
            }
        }
    }
    foreach (var field in fields)
    {
        var key = field.Key;
        var before = baseline[key].Value;
        var after = structured[key].Value;
        if (before == truth[key]) baselineHits++;
        if (after == truth[key]) structuredHits++;
        else if (key == "numero_apolice" && !string.IsNullOrWhiteSpace(after)) wrongPolicyNumbers++;
        Console.WriteLine($"{file} {key}: esperado={truth[key]} texto={before} estruturado={after}");
    }
}
Console.WriteLine($"Texto: {baselineHits}/36. Estruturado: {structuredHits}/36. Apólices incorretas: {wrongPolicyNumbers}.");
return structuredHits >= 33 && structuredHits > baselineHits && wrongPolicyNumbers == 0 ? 0 : 1;

static bool MatchesFormat(string key, string value) => key switch
{
    "numero_apolice" => value.Length >= 6 && value.All(char.IsAsciiDigit),
    "placa" => System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Z]{3}[0-9][A-Z0-9][0-9]{2}$"),
    "inicio_vigencia" => DateOnly.TryParseExact(value, "dd/MM/yyyy",
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _),
    _ => false
};
