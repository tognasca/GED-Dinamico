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
        ["cpf_cnpj"] = ["CPF/CNPJ", "CPF/CNP)", "CPF/CNP]", "CPF", "CNPJ"],
        ["coberturas"] = ["Coberturas"],
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
        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        var labels = definitions.SelectMany(f => new[] { f.Label, f.Anchor ?? "" })
            .Concat(Aliases.Values.SelectMany(x => x))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new Dictionary<string, FieldResult>(StringComparer.OrdinalIgnoreCase);
        var columnValues = FindSeparatedColumnValues(lines, labels);

        foreach (var field in definitions)
        {
            var candidates = new List<string>();
            if (Aliases.TryGetValue(field.Key, out var aliases)) candidates.AddRange(aliases);
            if (!string.IsNullOrWhiteSpace(field.Anchor)) candidates.Add(field.Anchor);
            candidates.Add(field.Label);
            string? value = null;
            string source = "not-found";

            foreach (var label in candidates.Where(x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (columnValues.TryGetValue(NormalizeLabel(label), out var mapped))
                    value = mapped;
                else
                    value = FindLabeledValue(lines, label, labels);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    source = "label";
                    break;
                }
            }

            // Custom regexes are a fallback only: legacy patterns can match 'Segurado'
            // inside 'Seguradora' and cross line boundaries into the next label.
            if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(field.Regex))
            {
                var match = Match(text, field.Regex);
                if (!string.IsNullOrWhiteSpace(match) && !IsFieldLabel(match, labels))
                {
                    value = match;
                    source = "regex";
                }
            }
            result[field.Key] = new FieldResult(value?.Trim() ?? "",
                string.IsNullOrWhiteSpace(value) ? 0 : 0.75, source);
        }
        return result;
    }

    private static string NormalizeLabel(string label) =>
        label.Trim().TrimEnd(':', '=', ' ', '\t').ToUpperInvariant();

    private static bool TryLabel(string line, string label, out string remainder)
    {
        remainder = "";
        if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = line[label.Length..];
        if (tail.Length == 0) return true;
        // A word boundary is essential: "Segurado" must never match "Seguradora".
        if (char.IsLetterOrDigit(tail[0])) return false;
        tail = tail.TrimStart();
        if (tail.Length > 0 && (tail[0] == ':' || tail[0] == '=' || tail[0] == '–' || tail[0] == '-'))
            tail = tail[1..].TrimStart();
        else if (tail.Length > 0) return false;
        remainder = tail.Trim();
        return true;
    }

    private static string? FindLabeledValue(string[] lines, string label, string[] knownLabels)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryLabel(lines[i], label, out var value)) continue;
            if (value.Length > 0 && !IsFieldLabel(value, knownLabels)) return value;
            // A value may be on the next non-empty line, but only if it is NOT another label.
            if (i + 1 < lines.Length && (i == 0 || !IsFieldLabel(lines[i - 1], knownLabels))
                && !IsFieldLabel(lines[i + 1], knownLabels)
                && !LooksLikeOtherLabel(lines[i + 1]))
                return lines[i + 1];
        }
        return null;
    }

    private static bool LooksLikeOtherLabel(string line) =>
        Regex.IsMatch(line, @"^[\p{L}][\p{L}\p{N} /().º-]{1,65}:\s*",
            RegexOptions.CultureInvariant, RegexTimeout);

    private static bool IsFieldLabel(string value, string[] labels) =>
        labels.Any(label => TryLabel(value, label, out var rest) && rest.Length == 0);

    // Some PDF text extractors output the entire label column before the value column.
    // Map only a contiguous sequence of empty labels to an equally sized sequence of
    // values, rather than assigning each label the following label as its value.
    private static Dictionary<string, string> FindSeparatedColumnValues(string[] lines, string[] labels)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var start = 0; start < lines.Length; start++)
        {
            if (!IsFieldLabel(lines[start], labels)) continue;
            var end = start;
            while (end < lines.Length && IsFieldLabel(lines[end], labels)) end++;
            var count = end - start;
            if (count < 2) { start = end - 1; continue; }
            var values = new List<string>();
            var cursor = end;
            // Test PDFs sometimes place the disclaimer between the label column and values.
            while (cursor < lines.Length && (lines[cursor].StartsWith("Este arquivo é uma simulação", StringComparison.OrdinalIgnoreCase)
                || lines[cursor].StartsWith("cobertura ou apólice emitida", StringComparison.OrdinalIgnoreCase)
                || lines[cursor].StartsWith("cobertura ou apólice", StringComparison.OrdinalIgnoreCase))) cursor++;
            while (cursor < lines.Length && values.Count < count
                && !IsFieldLabel(lines[cursor], labels))
            {
                if (LooksLikeOtherLabel(lines[cursor])) break;
                values.Add(lines[cursor]);
                cursor++;
            }
            if (values.Count == count)
                for (var j = 0; j < count; j++)
                    result[NormalizeLabel(lines[start + j])] = values[j];
            start = end - 1;
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
