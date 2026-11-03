using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Camunda.Api;

namespace Bakalarka.ConsoleApp;

internal sealed record InputField(
    string Name,
    string Label,
    string Type,
    string? Default = null,
    string? Hint = null);

/// <summary>Typovany vstup z terminalu. Prednost maji metadata BPMN formulare; napovedy prikladu jsou oddelene.</summary>
internal sealed class VariableEditor(Terminal ui)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Dictionary<string, Variable>> EditAsync(
        IReadOnlyList<InputField> fields,
        IReadOnlyDictionary<string, Variable>? current,
        CancellationToken ct)
    {
        var result = new Dictionary<string, Variable>();
        ui.Line("  Enter — ponechat aktuální hodnotu / přeskočit; :back — zrušit zadávání. Hodnoty se uloží po vyplnění formuláře.");

        foreach (var field in fields)
        {
            var existing = current?.GetValueOrDefault(field.Name);

            if (field.Hint is not null)
                ui.Line("  " + field.Hint);
            var suggestion = field.Type.Equals("File", StringComparison.OrdinalIgnoreCase)
                ? field.Default
                : existing is { Value.ValueKind: not JsonValueKind.Null }
                    ? Display(existing)
                    : field.Default;
            var value = await ReadAsync(field, suggestion, ct);

            if (value is not null)
                result[field.Name] = value;
        }

        while (true)
        {
            var name = await ui.Ask("Další proměnná (název) / Enter — hotovo", ct);

            if (name.Length == 0)
                break;
            var types = new[] { "String", "Boolean", "Integer", "Long", "Double", "Date", "Null", "Json", "Object", "File" };
            var choice = await ui.Menu(types, ct, "Nepřidávat");

            if (choice == 0)
                continue;
            var type = types[choice - 1];

            if (type == "Null")
            {
                result[name] = Variable.Null();
                continue;
            }

            var variable = await ReadAsync(new(name, name, type), null, ct);

            if (variable is not null)
                result[name] = variable;
        }

        return result;
    }

    private async Task<Variable?> ReadAsync(
        InputField field,
        string? suggestion,
        CancellationToken ct)
    {
        while (true)
        {
            var shown = IsSensitive(field.Name) && suggestion is not null ? "••••" : Terminal.Short(suggestion);
            var raw = await ui.Ask($"{field.Label} [{field.Name}, {field.Type}] ({shown})", ct);

            if (raw.Length == 0)
            {
                if (suggestion is null)
                    return null;
                raw = suggestion;
            }

            try
            {
                return Parse(raw, field.Type);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or JsonException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                ui.Line("  Neplatný formát. " + ex.Message);
            }
        }
    }

    internal static Variable Parse(string text, string type) => type.ToLowerInvariant() switch
    {
        "boolean" => Variable.Boolean(text.ToLowerInvariant() switch
        {
            "true" or "ano" or "yes" or "1" => true,

            "false" or "ne" or "no" or "0" => false,

            _ => throw new FormatException("Zadejte ano/ne nebo true/false.")
        }),

        "integer" => Variable.Integer(int.Parse(text, CultureInfo.InvariantCulture)),

        "long" => Variable.Long(long.Parse(text, CultureInfo.InvariantCulture)),

        "double" => Variable.Double(double.Parse(text, CultureInfo.InvariantCulture)),

        "date" => new(JsonSerializer.SerializeToElement(FormatDate(text)), "Date"),

        "null" => Variable.Null(),

        "file" => Variable.File(System.IO.File.ReadAllBytes(text), Path.GetFileName(text), Path.GetExtension(text).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",

            ".txt" => "text/plain",

            _ => "application/octet-stream"
        }),

        "json" => ParseJson(text),

        "object" => JsonSerializer.Deserialize<Variable>(text, Json)
                    is { Type: "Object", Value.ValueKind: not JsonValueKind.Undefined } value ? value
                    : throw new FormatException("Object: zadejte objekt s value, type: Object a valueInfo."),

        _ => Variable.String(text)
    };

    private static string FormatDate(string value)
    {
        // Datum v protokolu Camundy pouziva posun bez dvojtecky, napr. +0000.
        var czechDate = CultureInfo.GetCultureInfo("cs-CZ");

        if (!DateTimeOffset.TryParseExact(value, ["d.M.yyyy", "dd.MM.yyyy"], czechDate,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            if (value.Length >= 5 && (value[^5] is '+' or '-') && value[^3] != ':')
                value = value.Insert(value.Length - 2, ":");
            date = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        }

        var formatted = date.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

        return formatted.Remove(formatted.Length - 3, 1);
    }

    private static Variable ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text);

        return new(JsonSerializer.SerializeToElement(document.RootElement.GetRawText()), "Json");
    }

    public static bool IsSensitive(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase);

    public static string Display(Variable value)
    {
        if (value.Type == "File")
        {
            var filename = value.ValueInfo is not null &&
                value.ValueInfo.TryGetValue("filename", out var metadata) &&
                metadata.ValueKind == JsonValueKind.String
                    ? metadata.GetString()
                    : null;

            return "Soubor: " + (filename ?? "—");
        }

        if (value.Type == "Object")
            return JsonSerializer.Serialize(value, Json);

        return value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString()!
            : value.Value.GetRawText();
    }

    public static IReadOnlyList<InputField> ReadBpmnFields(string xml, string? activityId)
    {
        XNamespace bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
        XNamespace camunda = "http://camunda.org/schema/1.0/bpmn";
        var document = XDocument.Parse(xml);
        var activity = activityId is null ? document.Descendants(bpmn + "startEvent").FirstOrDefault()
            : document.Descendants().FirstOrDefault(e => (string?)e.Attribute("id") == activityId);

        return activity?.Descendants(camunda + "formField").Select(f => new InputField(
            (string)f.Attribute("id")!, (string?)f.Attribute("label") ?? (string)f.Attribute("id")!,
            (string?)f.Attribute("type") is "enum" ? "String" : (string?)f.Attribute("type") ?? "String",
            (string?)f.Attribute("defaultValue"),
            string.Join(", ", f.Elements(camunda + "value").Select(v => $"{v.Attribute("id")?.Value}: {v.Attribute("name")?.Value}"))))
            .ToArray() ?? [];
    }
}
