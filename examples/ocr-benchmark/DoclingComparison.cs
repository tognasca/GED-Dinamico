using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ged.Api.Domain;
using Ged.Api.Services;

internal static class DoclingComparison
{
    private static readonly FieldDefinition[] Fields =
    [
        new("numero_apolice", "Número da apólice", "text", true),
        new("placa", "Placa", "text", true),
        new("inicio_vigencia", "Início da vigência", "date", true)
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Uso: --compare-docling <expected.json> <atual.json> <candidato.json>");
            return 2;
        }

        try
        {
            var expected = await Read<Dictionary<string, Dictionary<string, string>>>(args[0]);
            var baseline = await Read<Capture>(args[1]);
            var candidate = await Read<Capture>(args[2]);
            if (expected.Count == 0 || baseline.Version == candidate.Version ||
                !baseline.Languages.SequenceEqual(candidate.Languages) ||
                !expected.Keys.ToHashSet().SetEquals(baseline.Documents.Keys) ||
                !expected.Keys.ToHashSet().SetEquals(candidate.Documents.Keys) ||
                expected.Keys.Any(file => string.IsNullOrEmpty(baseline.Documents[file].Sha256) ||
                    baseline.Documents[file].Sha256 != candidate.Documents[file].Sha256) ||
                expected.Values.Any(truth => !truth.Keys.ToHashSet().SetEquals(Fields.Select(f => f.Key))))
                throw new InvalidDataException("Use versões diferentes, com os mesmos idiomas, PDFs e campos esperados.");

            var before = Score(expected, baseline, useEvidence: false);
            var releaseGate = expected.Count >= 30;
            var after = Score(expected, candidate, useEvidence: releaseGate);
            var total = expected.Count * Fields.Length;
            Console.WriteLine($"Docling {baseline.Version}: {before.Hits}/{total}. p95: {before.P95} ms.");
            Console.WriteLine($"Docling {candidate.Version}: {after.Hits}/{total}. p95: {after.P95} ms.");
            Console.WriteLine($"Apólices incorretas: {after.WrongPolicyNumbers}. Campos preenchidos indevidamente: {after.FalseFills}.");
            var strataPass = before.HitsByGroup.All(entry => after.HitsByGroup[entry.Key] >= entry.Value);
            var noRegressions = before.CorrectFields.IsSubsetOf(after.CorrectFields);
            Console.WriteLine($"Docs digitais e escaneados sem queda agregada: {strataPass}. Campos antes corretos preservados: {noRegressions}.");
            return after.Hits * 100 >= total * 95 && strataPass && noRegressions &&
                after.WrongPolicyNumbers == 0 && after.FalseFills == 0 && (!releaseGate || after.P95 < 5000) ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }

    private static async Task<T> Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException($"JSON inválido: {path}");

    private static ScoreResult Score(Dictionary<string, Dictionary<string, string>> expected, Capture capture, bool useEvidence)
    {
        var extractor = new DocumentExtraction();
        var normalizer = new EvidenceExtraction(extractor);
        var correct = new HashSet<(string File, string Field)>();
        var groups = new Dictionary<string, int>();
        int wrongPolicyNumbers = 0, falseFills = 0;
        foreach (var (file, truth) in expected)
        {
            var sample = capture.Documents[file];
            if (string.IsNullOrWhiteSpace(sample.Text) || sample.Milliseconds < 0)
                throw new InvalidDataException($"Captura inválida: {file}");
            // The synthetic fixture generator uses these prefixes to distinguish scans.
            var group = file.StartsWith("scan-", StringComparison.Ordinal) ||
                Regex.IsMatch(file, @"^apolice-(0[7-9]|1[0-2])\.pdf$") ? "escaneado" : "digital";
            groups.TryAdd(group, 0);
            var extracted = extractor.Extract(sample.Text, Fields);
            if (useEvidence)
            {
                if (sample.Structure.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"Captura candidata sem estrutura: {file}");
                var doc = new GedDocument();
                normalizer.Store(doc, new OcrResult(sample.Text, "docling",
                    TesseractOcrProvider.ReadDoclingEvidence(sample.Structure)));
                extracted = normalizer.Extract(doc, Fields);
            }
            foreach (var field in Fields)
            {
                var actual = Normalize(extracted[field.Key].Value, field.DataType);
                var target = Normalize(truth[field.Key], field.DataType);
                if (actual == target)
                {
                    correct.Add((file, field.Key));
                    groups[group]++;
                }
                else if (actual.Length > 0)
                {
                    if (field.Key == "numero_apolice") wrongPolicyNumbers++;
                    if (target.Length == 0) falseFills++;
                }
            }
        }

        var times = capture.Documents.Values.Select(d => d.Milliseconds).Order().ToArray();
        return new(correct.Count, wrongPolicyNumbers, falseFills, times[(int)Math.Ceiling(times.Length * 0.95) - 1], correct, groups);
    }

    private static string Normalize(string value, string dataType)
    {
        var text = Regex.Replace(value.Trim(), @"\s+", " ").Normalize();
        if (dataType == "date" && DateOnly.TryParseExact(text, ["d/M/yyyy", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return text;
    }

    private sealed record Capture(string Version, string[] Languages, Dictionary<string, Sample> Documents);
    private sealed record Sample(string Text, long Milliseconds, string Sha256, JsonElement Structure);
    private sealed record ScoreResult(int Hits, int WrongPolicyNumbers, int FalseFills, long P95,
        HashSet<(string File, string Field)> CorrectFields, Dictionary<string, int> HitsByGroup);
}
