using System.Text.Json;
using Camunda.Api;

namespace Bakalarka.ConsoleApp;

/// <summary>Navigace a akce aplikace. Pristup k enginu probiha pres ICamundaClient.</summary>
internal sealed partial class InteractiveConsole(ICamundaClient api, Terminal ui, bool sandboxMode = false, ICamundaClient? sourceApi = null)
{
    private CancellationToken shutdown;

    private readonly VariableEditor editor = new(ui);

    private readonly string workerId = "console-" + Guid.NewGuid().ToString("N");

    private readonly Dictionary<string, ProcessDefinition> definitions = new();

    private readonly Dictionary<string, string> models = new();

    public async Task RunAsync(string address, CancellationToken ct, bool returnToEnvironmentChooser = false)
    {
        shutdown = ct;
        ui.Heading(sandboxMode
            ? "CAMUNDA SANDBOX • Správa procesů"
            : "CAMUNDA • Správa procesů");
        ui.Field("Server", address);
        ui.Line("  Akce vybírejte číslem. 0 — zpět, :back — zrušit zadávání, Ctrl+C — ukončit.");

        try
        {
            if (sandboxMode) await RefreshSandboxCatalogAsync(ct);
            while (true)
            {
                ui.Heading("Hlavní nabídka");
                var choices = new List<string>
                {
                    "Modely procesů — výběr a spuštění",
                    "Aktivní procesy — úlohy a ovládání",
                    "Historie procesů",
                    "Nahrát BPMN ze souboru"
                };
                if (sandboxMode)
                {
                    choices.Add("Sandbox koncepty — editor / import souboru / obnovit z Camundy");
                    choices.Add("Publikovat sandbox model na hlavní Camundu");
                }
                choices.Add("Návod k použití");
                choices.Add("Odstranit nasazení modelu");

                var choice = await ui.Menu(choices.ToArray(), ct,
                    returnToEnvironmentChooser ? "Zpět k výběru Camundy" : "Ukončit");

                if (choice == 0)
                    return;
                await GuardAsync(async () =>
                {
                    switch (choice)
                    {
                        case 1:
                            await DefinitionsAsync(ct);
                            break;

                        case 2:
                            await InstancesAsync(null, ct);
                            break;

                        case 3:
                            await HistoryListAsync(ct);
                            break;

                        case 4:
                            await DeployAsync(await ui.Ask("Cesta k souboru .bpmn", ct), ct);
                            break;

                        case 5 when sandboxMode:
                            await OpenAnyBpmnInEditorAsync(ct);
                            break;

                        case 6 when sandboxMode:
                            await PromoteSandboxModelAsync(ct);
                            break;

                        case 5:
                        case 7 when sandboxMode:
                            ui.Line("  1. Otevřete modely: seznam se načítá z Camundy, včetně Invoice a ReviewInvoice.");
                            ui.Line("  2. Vyberte verzi, spusťte proces a vyplňte vstupní pole.");
                            ui.Line("  3. V přehledu procesu otevřete úlohu. Převezměte ji nebo dokončete s údaji.");
                            ui.Line("  4. Externí úloha se dokončuje ručně; zprávu odešlete v nabídce událostí.");
                            ui.Line("  5. Pokud proces čeká na podproces, otevřete jej z přehledu nadřazeného procesu.");
                            ui.Line("  6. V sandboxu otevřete BPMN ve vizualizátoru; uloženou verzi lze otestovat a pak poslat na hlavní Camundu.");
                            ui.Line("  Po provedení akce se stav znovu načte. Enter v přehledu — obnovit.");
                            ui.Line("  Průběžné sledování v jiném terminálu: dotnet run -- watch");
                            ui.Line("  V sandbox editoru vyberte proces hlavní Camundy, například prace_1.");
                            ui.Line("  Externí úloha: předáváte výsledek práce, konzole neprovádí její obchodní logiku.");
                            await ui.Pause(ct);
                            break;

                        case 6:
                        case 8 when sandboxMode:
                            await DeleteDeploymentAsync(ct);
                            break;
                    }
                });
            }
        }
        catch (EndOfInputException) { ui.Line("  Vstup byl ukončen. Na shledanou."); }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
            ui.Error(new TimeoutException("Server neodpověděl včas."));
        }
        catch (BackException) { ui.Line("  Zadávání bylo zrušeno."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or ArgumentException or InvalidOperationException or FormatException or System.Xml.XmlException or UnauthorizedAccessException or OverflowException)
        {
            ui.Error(ex);
        }
    }

    private async Task LoadDefinitionsAsync(CancellationToken ct)
    {
        var available = await Pagination.ReadAllAsync((offset, limit) => api.GetDefinitionsAsync(offset, limit, ct));
        definitions.Clear();

        foreach (var definition in available)
            definitions[definition.Id] = definition;
    }

    private string DefinitionName(string id) => definitions.TryGetValue(id, out var d) ? $"{d.Name ?? d.Key} · v{d.Version}" : id;

    private string Key(string id) => definitions.TryGetValue(id, out var d) ? d.Key : id.Split(':')[0];

    private async Task<string> XmlAsync(string definitionId, CancellationToken ct)
    {
        if (!models.TryGetValue(definitionId, out var xml))
            models[definitionId] = xml = (await api.GetBpmnAsync(definitionId, ct)).Bpmn20Xml;

        return xml;
    }

    private async Task<IReadOnlyList<InputField>> FieldsAsync(
        string definitionId,
        string? activity,
        CancellationToken ct)
    {
        var fields = VariableEditor.ReadBpmnFields(await XmlAsync(definitionId, ct), activity);

        return fields.Count > 0 ? fields : ExampleInputs.For(Key(definitionId), activity);
    }

    private async Task DefinitionsAsync(CancellationToken ct)
    {
        if (sandboxMode)
        {
            var choice = await ui.Menu(["Uložené procesy Camundy — editace / test / publikace", "Verze již nasazené v sandboxu"], ct);
            if (choice == 0) return;
            if (choice == 1) { await SandboxModelsAsync(ct); return; }
        }
        await LoadDefinitionsAsync(ct);
        ui.Heading("Modely • všechny verze dostupné přes toto připojení");
        var latestVersions = definitions.Values
            .GroupBy(definition => (definition.Key, definition.TenantId))
            .ToDictionary(group => group.Key, group => group.Max(definition => definition.Version));
        var model = await ui.Select(definitions.Values.OrderBy(d => d.Key).ThenByDescending(d => d.Version).ToArray(),
            d => $"{d.Name ?? d.Key} | {d.Key} | v{d.Version}{(latestVersions[(d.Key, d.TenantId)] == d.Version ? " | LATEST" : "")}{(d.TenantId is null ? "" : " | tenant=" + d.TenantId)}{(d.Suspended ? " | POZASTAVENO" : "")}", ct);

        if (model is null)
            return;
        ui.Heading(model.Name ?? model.Key);
        ui.Field("ID definice", model.Id);

        switch (await ui.Menu(["Spustit tuto verzi", "Otevřít aktivní instance tohoto procesu", "Uložit BPMN XML do souboru", "Upravit živý proces ve vizualizátoru"], ct))
        {
            case 1:
                await StartDefinitionAsync(model, ct);
                break;

            case 2:
                await InstancesAsync(model.Key, ct);
                break;

            case 4:
                var imported = await SandboxWorkspace.ImportAsync(api, model, Directory.GetCurrentDirectory(), ct);
                await EditSandboxModelAsync(imported, ct);
                break;

            case 3:
                var path = await ui.Ask("Nový soubor pro BPMN", ct);
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                await using (var writer = new StreamWriter(file))
                    await writer.WriteAsync((await XmlAsync(model.Id, ct)).AsMemory(), ct);
                ui.Success("Model uložen: " + path);
                break;
        }
    }

    private async Task StartDefinitionAsync(ProcessDefinition model, CancellationToken ct)
    {
        if (model.Suspended)
        {
            ui.Line("  Definice je v enginu pozastavena.");
            return;
        }

        var businessKey = await ui.Ask("Označení instance / Enter — bez označení", ct);
        var variables = await editor.EditAsync(await FieldsAsync(model.Id, null, ct), null, ct);
        var instance = await api.StartByDefinitionAsync(model.Id, variables,
            businessKey.Length == 0 ? null : businessKey, ct);
        ui.Success($"{(sandboxMode ? "Sandbox" : "Proces")} v{model.Version} spuštěn: {instance.Id}");
        await InstanceAsync(instance.Id, ct);
    }

    private async Task InstancesAsync(string? key, CancellationToken ct)
    {
        await LoadDefinitionsAsync(ct);
        ui.Heading("Aktivní procesy");
        var instances = await Pagination.ReadAllAsync((offset, limit) => api.GetInstancesAsync(key, offset, limit, ct));
        var instance = await ui.Select(instances, p => $"{DefinitionName(p.DefinitionId)} | {p.BusinessKey ?? "bez označení"} | {p.Id[..Math.Min(8, p.Id.Length)]}{(p.Suspended ? " | POZASTAVENO" : "")}", ct);

        if (instance is not null)
            await InstanceAsync(instance.Id, ct);
    }

    private async Task<Deployment> DeployAsync(string path, CancellationToken ct, ICamundaClient? targetApi = null)
    {
        var deployed = await SandboxWorkspace.DeployAsync(targetApi ?? api, path, ct);
        ui.Success("Nahráno: " + Path.GetFileName(path) + " | " + deployed.Id);
        return deployed;
    }

    private async Task DeleteDeploymentAsync(CancellationToken ct)
    {
        await LoadDefinitionsAsync(ct);
        var deployments = definitions.Values
            .GroupBy(definition => definition.DeploymentId)
            .Select(group => new
            {
                Id = group.Key,
                Definitions = group.OrderBy(definition => definition.Key).ThenByDescending(definition => definition.Version).ToArray()
            })
            .OrderBy(group => group.Definitions[0].Name ?? group.Definitions[0].Key)
            .ToArray();
        var selected = await ui.Select(deployments,
            deployment => $"{deployment.Definitions[0].Name ?? deployment.Definitions[0].Key} | {string.Join(", ", deployment.Definitions.Select(definition => $"{definition.Key} v{definition.Version}"))} | {deployment.Id}", ct);

        if (selected is null)
            return;

        ui.Line("  Odstraní všechny definice v tomto nasazení. Instance ani historie se nekaskádují.");
        var confirmation = await ui.Ask("Pro potvrzení napište ODSTRANIT", ct);

        if (confirmation != "ODSTRANIT")
        {
            ui.Line("  Nasazení nebylo změněno.");
            return;
        }

        await api.DeleteDeploymentAsync(selected.Id, ct);
        models.Clear();
        await LoadDefinitionsAsync(ct);
        ui.Success("Nasazení odstraněno.");
    }

    private async Task HistoryListAsync(CancellationToken ct)
    {
        await LoadDefinitionsAsync(ct);
        for (var offset = 0; ;)
        {
            ui.Heading($"Historie • záznamy {offset + 1}–{offset + 20}");
            var page = await api.GetProcessHistoryAsync(offset, 20, ct);
            for (var i = 0; i < page.Count; i++)
                ui.Line($"  {i + 1,2}. {Terminal.Clean(DefinitionName(page[i].ProcessDefinitionId))} | {Terminal.State(page[i].State)} | {page[i].Id}");
            var choice = await ui.Ask("Číslo — otevřít; n — další strana; p — předchozí strana; 0 — nabídka", ct);

            if (choice == "0")
                return;

            if (choice == "n")
            {
                if (page.Count == 20)
                    offset += 20;
                continue;
            }

            if (choice == "p")
            {
                offset = Math.Max(0, offset - 20);
                continue;
            }

            if (int.TryParse(choice, out var index) && index > 0 && index <= page.Count)
                await ShowHistoryAsync(page[index - 1].Id, ct);
        }
    }

    private async Task ShowHistoryAsync(string id, CancellationToken ct)
    {
        var history = await api.GetHistoryAsync(id, ct);
        ui.Heading("Historie instance");
        ui.Field("Proces", DefinitionName(history.ProcessDefinitionId));
        ui.Field("ID", id);
        ui.Field("Stav", Terminal.State(history.State));
        ui.Field("Začátek", history.StartTime);
        ui.Field("Konec", history.EndTime);
        ui.Field("Důvod zrušení", history.DeleteReason);

        foreach (var activity in await Pagination.ReadAllAsync((offset, limit) => api.GetActivityHistoryAsync(id, offset, limit, ct)))
            ui.Line($"  {(activity.Canceled ? "ZRUŠENA " : activity.EndTime is null ? "AKTIVNÍ " : "HOTOVO  ")} | {Terminal.Short(activity.ActivityName ?? activity.ActivityId),-42} | {activity.ActivityType}");
        await ui.Pause(ct);
    }
}
