using Ged.Api.Domain;
using System.Text.RegularExpressions;

namespace Ged.Api.Services;

public sealed class DocumentExtraction
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly (string Key, string Label, string DataType, string Pattern)[] Defaults =
    [
        ("numero", "Número", "text", @"(?:n[úu]mero|n[ºo.]|cheque)\s*[:#-]?\s*([\d.-]{4,})"),
        ("valor", "Valor", "currency", @"(?:valor|R\$)\s*[:=]?\s*(?:R\$\s*)?([\d.]+,\d{2})"),
        ("data", "Data", "date", @"(?:data|emiss[aã]o)\s*[:=]?\s*(\d{2}[/.-]\d{2}[/.-]\d{2,4})"),
        ("favorecido", "Favorecido", "text", @"(?:favorecido|pague\s+por\s+este\s+cheque\s+a)\s*[:=]?\s*([^\r\n]{3,100})"),
        ("cpf", "CPF", "text", @"CPF\s*[:=]?\s*(\d{3}\.?\d{3}\.?\d{3}-?\d{2})"),
        ("cnpj", "CNPJ", "text", @"CNPJ\s*[:=]?\s*(\d{2}\.?\d{3}\.?\d{3}/?\d{4}-?\d{2})"),
        ("agencia", "Agência", "text", @"ag[eê]ncia\s*[:=]?\s*([\d-]+)"),
        ("conta", "Conta", "text", @"conta\s*[:=]?\s*([\d-]+)")
    ];

    public List<object> Suggest(string text) => Defaults.Select(x =>
    {
        var found = Match(text, x.Pattern);
        return new { key = x.Key, label = x.Label, dataType = x.DataType, value = found, suggested = found != null };
    }).Where(x => x.suggested).Cast<object>().ToList();

    public Dictionary<string, FieldResult> Extract(string text, IEnumerable<FieldDefinition> fields)
    {
        var result = new Dictionary<string, FieldResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields)
        {
            string? value = null;
            if (!string.IsNullOrWhiteSpace(f.Regex)) value = Match(text, f.Regex);
            if (value == null && !string.IsNullOrWhiteSpace(f.Anchor))
                value = Match(text, $@"{Regex.Escape(f.Anchor)}\s*[:=]?\s*([^\r\n]{{1,120}})");
            if (value == null)
            {
                var predefined = Defaults.FirstOrDefault(x => x.Key.Equals(f.Key, StringComparison.OrdinalIgnoreCase));
                if (predefined.Pattern != null) value = Match(text, predefined.Pattern);
            }
            result[f.Key] = new FieldResult(value?.Trim() ?? "", value is null ? 0 : 0.75, value is null ? "not-found" : "regex-or-anchor");
        }
        return result;
    }

    public DocumentType? MatchType(string text, IEnumerable<DocumentType> types)
    {
        var matches = types.Where(t => t.Active).Select(t => new
        {
            Type = t,
            Score = t.Keywords.Count(k => k.Length >= 3 && text.Contains(k, StringComparison.OrdinalIgnoreCase))
        }).OrderByDescending(x => x.Score).ToList();
        // At least one explicit keyword. In case of a tie, avoid ambiguous auto-classification.
        return matches.Count == 0 || matches[0].Score == 0 || (matches.Count > 1 && matches[0].Score == matches[1].Score)
            ? null : matches[0].Type;
    }

    private static string? Match(string text, string pattern)
    {
        try
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            if (!match.Success) return null;
            return (match.Groups.Count > 1 ? match.Groups[1] : match.Groups[0]).Value.Trim();
        }
        catch (ArgumentException) { return null; }
        catch (RegexMatchTimeoutException) { return null; }
    }
}
