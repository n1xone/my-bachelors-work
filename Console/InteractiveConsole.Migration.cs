using Camunda.Api;
using System.Xml.Linq;

namespace Bakalarka.ConsoleApp;

internal sealed partial class InteractiveConsole
{
    internal async Task ApplySavedModelAsync(string path, CancellationToken ct)
    {
        await DeployAsync(path, ct);
        await LoadDefinitionsAsync(ct);
        var tenant = SandboxWorkspace.ReadManifest(path)?.Source.TenantId;
        foreach (var key in ExecutableProcessKeys(path))
        {
            var target = definitions.Values.Where(d => d.Key == key && d.TenantId == tenant).OrderByDescending(d => d.Version).First();
            var instances = await Pagination.ReadAllAsync((offset, limit) => api.GetInstancesAsync(key, offset, limit, ct));
            foreach (var instance in instances.Where(i => definitions.TryGetValue(i.DefinitionId, out var source) && source.TenantId == tenant))
            {
                await LiveUpdateRecovery.RecoverAsync(api, instance, ct);
                if (instance.DefinitionId != target.Id) await UpdateVersionAsync(await api.GetInstanceAsync(instance.Id, ct), ct, target);
            }
        }
    }

    private async Task<System.Text.Json.JsonElement> CompleteMigrationPlanAsync(System.Text.Json.JsonElement plan, ProcessInstance process, string xml, CancellationToken ct)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(plan.GetRawText())!.AsObject();
        var instructions = node["instructions"]!.AsArray();
        var mapped = instructions.SelectMany(i => i!["sourceActivityIds"]!.AsArray().Select(a => a!.GetValue<string>())).ToHashSet();
        var tree = await api.GetActivitiesAsync(process.Id, ct);
        var document = XDocument.Parse(xml);
        XNamespace b = "http://www.omg.org/spec/BPMN/20100524/MODEL";
        async Task Visit(ActivityInstance activity)
        {
            if (activity.ActivityType != "processDefinition" && !mapped.Contains(activity.ActivityId))
            {
                var candidates = document.Descendants(b + activity.ActivityType).Where(e => e.Attribute("id") is not null).ToArray();
                var selected = await ui.Select(candidates, e => $"{(string?)e.Attribute("name")} | {(string?)e.Attribute("id")}", ct)
                    ?? throw new InvalidOperationException("Aktivní krok nemá odpovídající cíl migrace.");
                instructions.Add(System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new { sourceActivityIds = new[] { activity.ActivityId }, targetActivityIds = new[] { (string)selected.Attribute("id")! }, updateEventTrigger = false })));
                mapped.Add(activity.ActivityId);
            }
            foreach (var child in activity.ChildActivityInstances) await Visit(child);
        }
        await Visit(tree);
        return System.Text.Json.JsonSerializer.SerializeToElement(node);
    }

    private async Task UpdateVersionAsync(ProcessInstance process, CancellationToken ct, ProcessDefinition? selectedTarget = null)
    {
        process = await api.GetInstanceAsync(process.Id, ct);
        await LiveUpdateRecovery.RecoverAsync(api, process, ct);
        process = await api.GetInstanceAsync(process.Id, ct);
        foreach (var definition in await Pagination.ReadAllAsync((offset, limit) => api.GetDefinitionsAsync(offset, limit, ct)))
            definitions[definition.Id] = definition;
        var source = definitions[process.DefinitionId];
        var target = selectedTarget ?? await ui.Select(definitions.Values.Where(d => d.Key == source.Key && d.TenantId == source.TenantId && d.Version > source.Version && !d.Suspended).OrderByDescending(d => d.Version).ToArray(), d => $"{d.Key} v{d.Version}", ct);
        if (target is null) return;
        process = await api.GetInstanceAsync(process.Id, ct);
        if (process.DefinitionId != source.Id) throw new InvalidOperationException("Instance changed version; retry the update.");
        var oldXml = (await api.GetBpmnAsync(source.Id, ct)).Bpmn20Xml;
        var newXml = (await api.GetBpmnAsync(target.Id, ct)).Bpmn20Xml;
        var plan = await api.GenerateMigrationAsync(source.Id, target.Id, ct);
        plan = await CompleteMigrationPlanAsync(plan, process, newXml, ct);
        var report = await api.ValidateMigrationAsync(plan, ct);
        if (report.GetProperty("instructionReports").EnumerateArray().Any(r => r.GetProperty("failures").GetArrayLength() > 0)
            || report.GetProperty("variableReports").EnumerateObject().Any())
            throw new InvalidOperationException("Migration plan rejected: " + report.GetRawText());
        ui.Field("Instance", process.Id);
        ui.Field("Verze", $"v{source.Version} → v{target.Version}");
        ui.Line("Budoucí kroky použijí novou verzi. Dokončené kroky se neopakují. Zkontrolujte plán migrace.");
        if (selectedTarget is null && await ui.Ask("Pro přípravu zadejte ANO", ct) != "ANO") return;
        var pausedByUs = false;
        var completed = false;
        try
        {
            var current = await api.GetVariablesAsync(process.Id, ct);
            XNamespace c = "http://camunda.org/schema/1.0/bpmn";
            Dictionary<string, XElement> Fields(string xml) => XDocument.Parse(xml).Descendants().Single(e => e.Name.LocalName == "process" && (string?)e.Attribute("id") == source.Key).Descendants(c + "formField").GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
            var before = Fields(oldXml);
            var after = Fields(newXml);
            var changes = new Dictionary<string, Variable>();
            var history = await Pagination.ReadAllAsync((offset, limit) => api.GetActivityHistoryAsync(process.Id, offset, limit, ct));
            var visited = history.Select(h => h.ActivityId).ToHashSet();
            var removed = before.Keys.Except(after.Keys).Where(current.ContainsKey).ToArray();
            var fieldsToReview = after.Where(pair =>
            {
                var owner = pair.Value.Ancestors().FirstOrDefault(e => e.Attribute("id") is not null);
                return owner is not null && (visited.Contains((string)owner.Attribute("id")!) || owner.Name.LocalName == "startEvent")
                    && (!current.ContainsKey(pair.Key) || !before.TryGetValue(pair.Key, out var old) || !XNode.DeepEquals(old, pair.Value));
            }).ToArray();
            if ((fieldsToReview.Length > 0 || removed.Length > 0) && !process.Suspended)
            {
                await api.SetSuspendedAsync(process.Id, true, ct);
                pausedByUs = true;
            }
            foreach (var (name, field) in fieldsToReview)
            {
                var type = (string?)field.Attribute("type") ?? "string";
                var changed = !current.ContainsKey(name) || !before.TryGetValue(name, out var previous) || !XNode.DeepEquals(previous, field);
                if (!changed) continue;
                var required = field.Descendants(c + "constraint").Any(e => (string?)e.Attribute("name") == "required" && (string?)e.Attribute("config") == "true");
                do
                {
                    var values = await editor.EditAsync([new(name, (string?)field.Attribute("label") ?? name, type == "enum" ? "String" : type, (string?)field.Attribute("defaultValue"))], current, ct);
                    foreach (var pair in values) changes[pair.Key] = pair.Value;
                    var value = changes.GetValueOrDefault(name);
                    var error = MigrationFieldValidation.Error(field, value, required);
                    if (error is null) break;
                    ui.Line(error);
                } while (true);
            }

            if (selectedTarget is null && await ui.Ask("Provést migraci a uložit hodnoty? ANO", ct) != "ANO") throw new BackException();
            new LiveUpdateRecovery(source.Id, target.Id, removed, pausedByUs).Save(process.Id);
            await api.MigrateAsync(plan, process.Id, changes, ct);
            if (removed.Length > 0) await api.DeleteVariablesAsync(process.Id, removed, ct);
            completed = true;
            ui.Success($"Instance {process.Id} aktualizována na v{target.Version}; pokračuje ze stejného místa.");
        }
        catch
        {
            // On a rejected migration, the old definition is still consistent and can resume.
            var actual = await api.GetInstanceAsync(process.Id, CancellationToken.None);
            if (pausedByUs && actual.DefinitionId == source.Id)
            {
                await api.SetSuspendedAsync(process.Id, false, CancellationToken.None);
                pausedByUs = false;
                LiveUpdateRecovery.Clear(process.Id);
            }
            if (actual.DefinitionId == source.Id) LiveUpdateRecovery.Clear(process.Id);
            ui.Line(actual.DefinitionId == source.Id ? "Aktualizace neprovedena; původní instance zachována."
                : "Migrace proběhla; odpověď nebo dokončení aktualizace selhalo. Opakujte Save pro dokončení. Stav instance: " + (actual.Suspended ? "pozastavena" : "aktivní"));
            throw;
        }
        // Resume only after the complete update succeeded.
        if (completed && pausedByUs) await api.SetSuspendedAsync(process.Id, false, CancellationToken.None);
        if (completed) LiveUpdateRecovery.Clear(process.Id);
    }
}
