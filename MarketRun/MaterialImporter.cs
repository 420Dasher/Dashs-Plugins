using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MarketRun;

internal sealed class ImportRow
{
    public int Line { get; init; }
    public string Original { get; init; } = "";
    public string Name { get; init; } = "";
    public uint ItemId { get; init; }
    public int Quantity { get; init; }
    public int Quality { get; init; }
    public string Error { get; init; } = "";

    public bool Valid => ItemId != 0 && Error.Length == 0;
}

internal sealed class MaterialImporter
{
    private readonly Dictionary<string, uint> names =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<uint, string> displayNames = new();

    private static readonly Regex[] Patterns =
    {
        new(
            @"^(?<qty>\d+)\s*[xX×*]\s*(?<name>.+)$",
            RegexOptions.Compiled),

        new(
            @"^(?<name>.+?)\s+[xX×*]\s*(?<qty>\d+)$",
            RegexOptions.Compiled),

        new(
            @"^(?<qty>\d+)\s*[,;\t]\s*(?<name>.+)$",
            RegexOptions.Compiled),

        new(
            @"^(?<name>.+?)\s*[,;\t:]\s*(?<qty>\d+)$",
            RegexOptions.Compiled),

        new(
            @"^(?<qty>\d+)\s+(?<name>.+)$",
            RegexOptions.Compiled)
    };

    public MaterialImporter(IDataManager data)
    {
        var languages = new[]
        {
            data.Language,
            ClientLanguage.English,
            ClientLanguage.German,
            ClientLanguage.French,
            ClientLanguage.Japanese
        }.Distinct();

        foreach (var language in languages)
        {
            try
            {
                foreach (var item in data.GetExcelSheet<Item>(language))
                {
                    if (item.RowId == 0)
                        continue;

                    string name = Clean(item.Name.ToString());

                    if (name.Length == 0)
                        continue;

                    displayNames.TryAdd(item.RowId, name);

                    if (names.TryGetValue(name, out uint existing))
                    {
                        // Never silently select an ambiguous item name.
                        if (existing != item.RowId)
                            names[name] = 0;
                    }
                    else
                    {
                        names.Add(name, item.RowId);
                    }
                }
            }
            catch
            {
                // Continue if a language sheet is unavailable.
            }
        }
    }

    private static string Clean(string value)
    {
        value = value.Trim().Trim('"', '\'', '`');
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private bool Resolve(
        string raw,
        out uint itemId,
        out string displayName)
    {
        itemId = 0;
        displayName = Clean(raw);

        var idMatch = Regex.Match(
            displayName,
            @"^(?:#|item:)\s*(\d+)$",
            RegexOptions.IgnoreCase);

        if (idMatch.Success &&
            uint.TryParse(
                idMatch.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint id) &&
            displayNames.TryGetValue(id, out string? knownName))
        {
            itemId = id;
            displayName = knownName;
            return true;
        }

        if (names.TryGetValue(displayName, out uint resolved) &&
            resolved != 0)
        {
            itemId = resolved;
            displayName = displayNames.GetValueOrDefault(
                resolved, displayName);

            return true;
        }

        return false;
    }

    private ImportRow ResolveRow(
        string name,
        int quantity,
        int quality,
        int line,
        string original)
    {
        if (quantity < 1 || quantity > 999999)
        {
            return new ImportRow
            {
                Line = line,
                Original = original,
                Error = "Quantity must be between 1 and 999999."
            };
        }

        if (!Resolve(name, out uint id, out string display))
        {
            return new ImportRow
            {
                Line = line,
                Original = original,
                Error = "Unknown or ambiguous game item."
            };
        }

        return new ImportRow
        {
            Line = line,
            Original = original,
            Name = display,
            ItemId = id,
            Quantity = quantity,
            Quality = quality
        };
    }

    private ImportRow ParseLine(string original, int line)
    {
        string text = original.Trim();

        // Remove common copied-list bullets and numbering.
        text = Regex.Replace(
            text,
            @"^\s*(?:[-•]\s+|\d+\.\s+)",
            "");

        // Accept quantity-first and quantity-last formats,
        // including optional trailing x and separators.
        var patterns = new[]
        {
            @"^(?<qty>\d{1,7})\s*(?:[xX×*]\s*|\s+|[,;\t]\s*)(?<name>.+)$",
            @"^(?<name>.+?)\s*(?:[,;:\t]\s*|\s+[xX×*]\s*|\s+)(?<qty>\d{1,7})\s*[xX×*]?$"
        };

        ImportRow? firstError = null;

        foreach (string pattern in patterns)
        {
            var match = Regex.Match(
                text, pattern, RegexOptions.IgnoreCase);

            if (!match.Success)
                continue;

            if (!int.TryParse(
                match.Groups["qty"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int quantity))
                continue;

            string name = Clean(match.Groups["name"].Value);
            int quality = 0;

            // HQ/NQ may appear before or after the name.
            var prefix = Regex.Match(
                name,
                @"^\s*[\[(]\s*(HQ|NQ)\s*[\])]\s*",
                RegexOptions.IgnoreCase);

            if (prefix.Success)
            {
                quality = prefix.Groups[1].Value.Equals(
                    "HQ", StringComparison.OrdinalIgnoreCase)
                    ? 2 : 1;

                name = name.Substring(prefix.Length).Trim();
            }

            var suffix = Regex.Match(
                name,
                @"\s*[\[(]\s*(HQ|NQ)\s*[\])]\s*$",
                RegexOptions.IgnoreCase);

            if (suffix.Success)
            {
                quality = suffix.Groups[1].Value.Equals(
                    "HQ", StringComparison.OrdinalIgnoreCase)
                    ? 2 : 1;

                name = name.Substring(
                    0, suffix.Index).Trim();
            }

            var resolved = ResolveRow(
                name, quantity, quality, line, original);

            if (resolved.Valid)
                return resolved;

            firstError ??= resolved;
        }

        return firstError ?? new ImportRow
        {
            Line = line,
            Original = original,
            Error = "Unrecognized material-list format."
        };
    }

    private static int ReadInt(
        JsonElement element,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out int numeric))
                return numeric;

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out int text))
                return text;
        }

        return 0;
    }

    private List<ImportRow> ParseJson(string source)
    {
        var result = new List<ImportRow>();

        try
        {
            using var doc = JsonDocument.Parse(source);

            var array = doc.RootElement;

            if (array.ValueKind == JsonValueKind.Object &&
                array.TryGetProperty("items", out var nested))
                array = nested;

            if (array.ValueKind != JsonValueKind.Array)
                throw new FormatException(
                    "Expected an array or an object with an items array.");

            int line = 0;

            foreach (var entry in array.EnumerateArray())
            {
                line++;

                if (entry.ValueKind != JsonValueKind.Object)
                {
                    result.Add(new ImportRow
                    {
                        Line = line,
                        Original = entry.ToString(),
                        Error = "Expected an item object."
                    });
                    continue;
                }

                int id = ReadInt(entry, "itemId", "id");

                int quantity = ReadInt(
                    entry,
                    "quantity", "amount", "required", "total");

                string name = "";

                if (entry.TryGetProperty("name", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                    name = value.GetString() ?? "";

                if (id > 0)
                    name = "#" + id;

                int quality = 0;

                if (entry.TryGetProperty("hq", out var hq) &&
                    hq.ValueKind == JsonValueKind.True)
                    quality = 2;

                if (entry.TryGetProperty("isHq", out var isHq) &&
                    isHq.ValueKind == JsonValueKind.True)
                    quality = 2;

                result.Add(ResolveRow(
                    name,
                    quantity,
                    quality,
                    line,
                    entry.ToString()));
            }

            if (result.Count == 0)
            {
                result.Add(new ImportRow
                {
                    Original = source,
                    Error = "JSON contains no items."
                });
            }
        }
        catch (Exception ex)
        {
            result.Clear();

            result.Add(new ImportRow
            {
                Original = source,
                Error = "JSON import failed: " + ex.Message
            });
        }

        return result;
    }

    public List<ImportRow> Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new List<ImportRow>();

        string trimmed = source.Trim();

        if (trimmed.StartsWith("[") ||
            trimmed.StartsWith("{"))
            return ParseJson(trimmed);

        var result = new List<ImportRow>();

        string[] lines = source.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();

            if (line.Length == 0)
                continue;

            // Skip recognized column headers.
            if (Regex.IsMatch(
                line,
                @"^(?:item|item name|material|name)\s*[,;\t:]\s*(?:quantity|qty|amount|required)$",
                RegexOptions.IgnoreCase))
                continue;

            result.Add(ParseLine(line, i + 1));
        }

        return result;
    }
}