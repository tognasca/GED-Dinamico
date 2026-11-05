using Ged.Api.Domain;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;

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

    // Common labels found on Brazilian insurance policies. The key is the normalized field key.
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["numero_apolice"] = ["Número da apólice", "Nº da apólice", "Apólice"],
        ["seguradora"] = ["Seguradora", "Companhia seguradora"],
        ["segurado"] = ["Segurado", "Nome do segurado"],
        ["cpf_cnpj"] = ["CPF/CNPJ", "CPF", "CNPJ"],
        ["placa"] = ["Placa", "Placa do veículo"],
        ["renavam"] = ["RENAVAM"],
        ["chassi"] = ["Chassi"],
        ["modelo_veiculo"] = ["Modelo do veículo", "Veículo", "Marca/modelo"],
        ["ano_modelo"] = ["Ano/modelo", "Ano modelo"],
        ["inicio_vigencia"] = ["Início da vigência", "Vigência inicial"],
        ["fim_vigencia"] = ["Fim da vigência", "Vigência final"],
        ["premio_total"] = ["Prêmio total", "Prêmio"],
        ["franquia"] = ["Franquia"],
        ["numero_sinistro"] = ["Número do sinistro", "Nº do sinistro"],
        ["numero_proposta"] = ["Número da proposta", "Nº da proposta"]
    };

    public Dictionary<string, FieldResult> Extract(string text, IEnumerable<FieldDefinition> fields)
    {
        var definitions = fields.ToList();
        var labels = definitions.SelectMany(f => new[] { f.Label, f.Anchor ?? "" })
            .Concat(Aliases.Values.SelectMany(x => x))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var result = new Dictionary<string, FieldResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in definitions)
        {
            string? value = null;
            if (!string.IsNullOrWhiteSpace(f.Regex)) value = Match(text, f.Regex);
            if (string.IsNullOrWhiteSpace(value))
            {
                var candidates = new List<string>();
                if (Aliases.TryGetValue(f.Key, out var aliases)) candidates.AddRange(aliases);
                if (!string.IsNullOrWhiteSpace(f.Anchor)) candidates.Add(f.Anchor);
                if (!string.IsNullOrWhiteSpace(f.Label)) candidates.Add(f.Label);
                foreach (var label in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    value = FindLabeledValue(lines, label, labels);
                    if (!string.IsNullOrWhiteSpace(value)) break;
                }
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                var predefined = Defaults.FirstOrDefault(x => x.Key.Equals(f.Key, StringComparison.OrdinalIgnoreCase));
                if (predefined.Pattern != null) value = Match(text, predefined.Pattern);
            }
            result[f.Key] = new FieldResult(value?.Trim() ?? "", string.IsNullOrWhiteSpace(value) ? 0 : 0.75,
                string.IsNullOrWhiteSpace(value) ? "not-found" : "regex-or-anchor");
        }
        return result;
    }

    private static string? FindLabeledValue(string[] lines, string label, string[] knownLabels)
    {
        // Never use \\s here: it consumes line breaks and shifts values to the next field.
        var pattern = @"^\s*" + Regex.Escape(label) + @"\s*[:=–-]\s*(.*)$";
        try
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var match = Regex.Match(lines[i], pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                if (!match.Success) continue;
                var candidate = match.Groups[1].Value.Trim();
                if (candidate.Length == 0 && i + 1 < lines.Length)
                    candidate = lines[i + 1].Trim();
                if (candidate.Length == 0 || IsFieldLabel(candidate, knownLabels)) continue;
                return candidate.Length <= 200 ? candidate : candidate[..200];
            }
        }
        catch (RegexMatchTimeoutException) { }
        return null;
    }

    private static bool IsFieldLabel(string value, string[] knownLabels)
    {
        var trimmed = value.Trim().TrimEnd(':', '=', ' ', '\t');
        return knownLabels.Any(label => string.Equals(trimmed, label, StringComparison.OrdinalIgnoreCase));
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
