using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Camunda.Api;

namespace Bakalarka.ConsoleApp;
internal static class MigrationFieldValidation
{
    public static string? Error(XElement field, Variable? value, bool required)
    {
        XNamespace c = "http://camunda.org/schema/1.0/bpmn";
        if (value is null || value.Value.ValueKind == JsonValueKind.Null
            || value.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.Value.GetString()))
            return required ? "Povinná hodnota chybí." : null;
        var text = VariableEditor.Display(value);
        if ((string?)field.Attribute("type") == "enum" && !field.Elements(c + "value").Any(e => (string?)e.Attribute("id") == text))
            return "Vyberte jednu z povolených hodnot: " + string.Join(", ", field.Elements(c + "value").Select(e => (string?)e.Attribute("id")));
        foreach (var constraint in field.Descendants(c + "constraint"))
        {
            var name = (string?)constraint.Attribute("name");
            var config = (string?)constraint.Attribute("config");
            if (name is "minlength" or "maxlength" && int.TryParse(config, out var length)
                && (name == "minlength" ? text.Length < length : text.Length > length)) return $"Nesplněno {name}: {config}";
            if (name is "min" or "max" && decimal.TryParse(config, NumberStyles.Any, CultureInfo.InvariantCulture, out var bound)
                && decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
                && (name == "min" ? number < bound : number > bound)) return $"Nesplněno {name}: {config}";
        }
        return null;
    }
}
