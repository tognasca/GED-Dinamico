using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ged.Api.Services;

internal static class PolicyNormalization
{
    private const string NumberPattern = @"[0-9][0-9./-]{3,39}";
    private const string PlatePattern = @"[A-Z]{3}-?[0-9][A-Z0-9][0-9]{2}";
    private const string DatePattern = @"[0-9]{1,2}/[0-9](?:[ \t]?[0-9])?/[0-9]{4}";
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly Rule[] Rules =
    [
        new("numero_apolice", ["numero da apolice", "nº da apolice", "apolice"], NumberPattern),
        new("placa", ["placa do veiculo", "placa"], PlatePattern),
        new("inicio_vigencia", ["inicio da vigencia", "vigencia inicial"], DatePattern)
    ];

    public static Dictionary<string, List<string>> Read(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var lines = Fold(text).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            foreach (var rule in Rules)
            foreach (var match in rule.Inline.Matches(line).Cast<Match>())
                Add(rule, match.Groups[1].Value);

            if (index + 2 < lines.Length && Cells(line) is { } header &&
                Cells(lines[index + 1]) is { } divider && divider.All(cell => cell.Trim(':').Length >= 3 && cell.Trim(':').All(c => c == '-')) &&
                Cells(lines[index + 2]) is { } values && header.Length == divider.Length && header.Length == values.Length)
            {
                for (var column = 0; column < header.Length; column++)
                    if (ByLabel(header[column]) is { } rule && rule.Whole.IsMatch(values[column]))
                        Add(rule, values[column]);
            }

            // A text export can emit all headers first, followed by their values.
            // Consume a whole block only when every position has the expected format.
            var labels = lines.Skip(index).Take(Rules.Length + 1).Select(ByLabel).TakeWhile(rule => rule != null).ToArray();
            if (labels.Length == 0 || labels.Distinct().Count() != labels.Length || index + 2 * labels.Length > lines.Length ||
                (index > 0 && ByLabel(lines[index - 1]) != null))
                continue;
            var row = lines.Skip(index + labels.Length).Take(labels.Length).ToArray();
            if (labels.Select((rule, column) => rule!.Whole.IsMatch(row[column])).All(valid => valid))
                for (var column = 0; column < labels.Length; column++)
                    Add(labels[column]!, row[column]);
        }
        return result;

        void Add(Rule rule, string value)
        {
            if (!result.TryGetValue(rule.Key, out var candidates))
                result[rule.Key] = candidates = [];
            candidates.Add(rule.Key == "inicio_vigencia" ? value.Replace(" ", "").Replace("\t", "") : value.ToUpperInvariant());
        }
    }

    private static string[]? Cells(string line) =>
        line.Length > 2 && line.StartsWith('|') && line.EndsWith('|')
            ? line[1..^1].Split('|', StringSplitOptions.TrimEntries) : null;

    private static Rule? ByLabel(string line) =>
        Rules.FirstOrDefault(rule => rule.Labels.Contains(line.TrimEnd(':').TrimEnd(), StringComparer.OrdinalIgnoreCase));

    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        return builder.ToString();
    }

    private sealed class Rule(string key, string[] labels, string pattern)
    {
        public string Key { get; } = key;
        public string[] Labels { get; } = labels;
        public Regex Inline { get; } = new($@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', labels.Select(Regex.Escape))})[ \t]*[:=]?[ \t]*({pattern})(?![\p{{L}}\p{{N}}])", Options, Timeout);
        public Regex Whole { get; } = new($@"\A(?:{pattern})\z", Options, Timeout);
    }
}
