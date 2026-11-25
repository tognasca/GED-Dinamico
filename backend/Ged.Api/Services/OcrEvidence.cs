using System.Text.Json;
using System.Text.RegularExpressions;
using Ged.Api.Domain;

namespace Ged.Api.Services;

public sealed record OcrBounds(double Left, double Top, double Right, double Bottom, string Origin);
public sealed record OcrEvidence(string Text, int Page, OcrBounds? Bounds = null, double? Confidence = null, string? Label = null);
public sealed record OcrResult(string Text, string Provider, IReadOnlyList<OcrEvidence> Evidence)
{
    public static OcrResult FromPages(IEnumerable<string> pages, string provider)
    {
        var evidence = pages.Select((text, index) => new OcrEvidence(text, index + 1)).ToArray();
        return new(string.Join("\n\f\n", evidence.Select(x => x.Text)), provider, evidence);
    }
}

public sealed partial class EvidenceExtraction(DocumentExtraction extractor)
{
    public void Store(GedDocument document, OcrResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Text))
            throw new InvalidDataException("No readable text found.");
        document.OcrText = result.Text.Replace("\0", "");
        document.OcrProvider = result.Provider;
        document.OcrEvidenceJson = JsonSerializer.Serialize(result.Evidence, JsonOptions.Default);
    }

    public Dictionary<string, FieldResult> Extract(GedDocument document, IEnumerable<FieldDefinition> fields)
    {
        var evidence = JsonSerializer.Deserialize<List<OcrEvidence>>(document.OcrEvidenceJson, JsonOptions.Default) ?? [];
        var definitions = fields.ToArray();
        var pages = document.OcrText.Split('\f').Select(page => extractor.Extract(page, definitions)).ToArray();
        var results = new Dictionary<string, FieldResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definitions)
        {
            var candidates = pages.Select(page => page[field.Key]).ToList();
            // The right-hand and lower cells may both contain a format-valid value.
            // Keep the field empty if they disagree with each other or with the text.
            if (field.Key is "numero_apolice" or "placa" or "inicio_vigencia")
                candidates.AddRange(evidence.Where(item => string.Equals(item.Label, field.Label.TrimEnd(':'),
                    StringComparison.OrdinalIgnoreCase)).Select(item => new FieldResult(item.Text, 0.75, "table", item)));

            var nonempty = candidates.Where(item => !string.IsNullOrWhiteSpace(item.Value)).ToArray();
            var valid = nonempty.Where(item => Valid(field, item.Value)).ToArray();
            var distinct = valid.Select(item => item.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count();
            if (distinct > 1 || candidates.Any(item => item.Source == "ambiguous-layout"))
            {
                results[field.Key] = new("", 0, distinct > 1 ? "ambiguous-value" : "ambiguous-layout");
                continue;
            }

            var result = valid.FirstOrDefault(item => item.Evidence != null) ?? valid.FirstOrDefault();
            if (result == null)
            {
                results[field.Key] = new("", 0, nonempty.Length == 0 ? "not-found" : "invalid-format");
                continue;
            }

            var matches = evidence.Where(item => item.Label == null && ContainsValue(item.Text, result.Value)).ToArray();
            var match = result.Evidence;
            if (match == null && matches.Select(item => item.Page).Distinct().Take(2).Count() == 1)
                match = matches.OrderByDescending(item => item.Confidence.HasValue).ThenBy(item => item.Text.Length).First();
            if (evidence.Count > 0 && match == null)
            {
                results[field.Key] = new("", 0, matches.Length == 0 ? "unverified" : "ambiguous-value");
                continue;
            }

            results[field.Key] = result;
            if (match != null)
            {
                var excerpt = match.Text.Split('\n').FirstOrDefault(line => ContainsValue(line, result.Value)) ?? result.Value;
                results[field.Key] = result with { Evidence = match with { Text = excerpt.Trim().Replace("\0", "") } };
            }
        }
        return results;
    }

    private static bool ContainsValue(string text, string value)
    {
        var start = 0;
        while (start <= text.Length - value.Length)
        {
            var index = text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var end = index + value.Length;
            if ((index == 0 || !char.IsLetterOrDigit(text[index - 1])) &&
                (end == text.Length || !char.IsLetterOrDigit(text[end])))
                return true;
            start = index + 1;
        }
        return false;
    }

    private static bool Valid(FieldDefinition field, string value) => field.Key switch
    {
        "numero_apolice" => PolicyNumber().IsMatch(value),
        "placa" => Plate().IsMatch(value),
        _ when field.DataType == "date" => DateOnly.TryParseExact(value, ["d/M/yyyy", "yyyy-MM-dd"],
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _),
        _ => true
    };

    [GeneratedRegex(@"^[0-9][0-9./-]{3,39}$")]
    private static partial Regex PolicyNumber();

    [GeneratedRegex(@"^[A-Z]{3}-?[0-9][A-Z0-9][0-9]{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Plate();
}
