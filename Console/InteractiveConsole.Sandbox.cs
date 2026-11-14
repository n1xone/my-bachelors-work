using System.Text.Json;
using System.Xml.Linq;
using Camunda.Api;

namespace Bakalarka.ConsoleApp;

internal sealed partial class InteractiveConsole
{
    private async Task RefreshSandboxCatalogAsync(CancellationToken ct)
    {
        if (sourceApi is null) return;
        ui.Line("  Načítám nejnovější procesy z hlavní Camundy do místních kopií…");
        try
        {
            var result = await SandboxWorkspace.SynchronizeAsync(sourceApi, Directory.GetCurrentDirectory(), ct);
            ui.Success($"Katalog: {result.Imported}/{result.Available} procesů uloženo. Existující úpravy jsou zachovány.");
            foreach (var error in result.Errors) ui.Line("  Import se nezdařil: " + error);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            ui.Line("  Hlavní Camunda není dostupná. Pracujte s uloženými kopiemi; obnovu lze opakovat v menu.");
            ui.Line("  " + ex.Message);
        }
    }

    private async Task SandboxModelsAsync(CancellationToken ct)
    {
        var path = await ui.Select(BpmnEditorHost.DiscoverModelFiles().ToArray(), SandboxWorkspace.Label, ct);
        if (path is null) return;
        path = BpmnEditorHost.ResolveEditableModelPath(path, Directory.GetCurrentDirectory());
        switch (await ui.Menu(["Otevřít ve vizualizátoru", "Nasadit uloženou kopii do sandboxu a spustit test", "Publikovat tuto kopii na hlavní Camundu"], ct))
        {
            case 1: await EditSandboxModelAsync(path, ct); break;
            case 2: await TestSandboxModelAsync(path, ct); break;
            case 3: await PromoteSandboxModelAsync(ct, path); break;
        }
    }

    private async Task OpenAnyBpmnInEditorAsync(CancellationToken ct)
    {
        switch (await ui.Menu(["Uložené procesy — editace / test / publikace", "Import BPMN souboru", "Obnovit katalog z hlavní Camundy"], ct))
        {
            case 1: await SandboxModelsAsync(ct); break;
            case 2: await EditSandboxModelAsync(Path.GetFullPath(await ui.Ask("Cesta k souboru .bpmn", ct)), ct); break;
            case 3: await RefreshSandboxCatalogAsync(ct); break;
        }
    }

    private async Task EditSandboxModelAsync(string sourcePath, CancellationToken ct)
    {
        var editorRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "BpmnEditor", "dist"));
        ui.Heading("Editor BPMN");
        ui.Line($"  Otevírám: {BpmnEditorHost.RelativePathForDisplay(sourcePath, Directory.GetCurrentDirectory())}");
        ui.Line("  Uložení nasadí novou verzi do připojené Camundy a aktualizuje její aktivní instance. Data doplňte v terminálu.");
        var selectedModelPath = await BpmnEditorHost.RunAsync(sourcePath, editorRoot, ct, ApplySavedModelAsync, sandboxMode ? CamundaConnections.SandboxAddress : CamundaConnections.MainAddress);
        if (selectedModelPath is null)
            return;


    }

    private async Task TestSandboxModelAsync(string sandboxPath, CancellationToken ct)
    {
        ui.Heading("Sandbox • kontrola uložené verze");
        if (!File.Exists(sandboxPath)) throw new FileNotFoundException("Sandbox draft was not found.", sandboxPath);
        var processKeys = ExecutableProcessKeys(sandboxPath);
        if (processKeys.Count == 0)
        {
            ui.Line("  Model je uložen v sandboxu, ale neobsahuje spustitelný BPMN proces; do Camundy se nenasadil.");
            return;
        }

        var tenantId = SandboxWorkspace.ReadManifest(sandboxPath)?.Source.TenantId;
        await LoadDefinitionsAsync(ct);
        var previousVersions = definitions.Values
            .Where(definition => definition.TenantId == tenantId && processKeys.Contains(definition.Key, StringComparer.Ordinal))
            .GroupBy(definition => definition.Key)
            .ToDictionary(group => group.Key, group => group.Max(definition => definition.Version), StringComparer.Ordinal);

        await DeployAsync(sandboxPath, ct);
        await LoadDefinitionsAsync(ct);
        var latestDefinitions = definitions.Values
            .Where(definition => definition.TenantId == tenantId && processKeys.Contains(definition.Key, StringComparer.Ordinal))
            .GroupBy(definition => definition.Key)
            .Select(group => group.OrderByDescending(definition => definition.Version).First())
            .OrderBy(definition => definition.Key)
            .ToArray();

        foreach (var definition in latestDefinitions)
        {
            if (!previousVersions.TryGetValue(definition.Key, out var previousVersion))
                ui.Success($"Aktuální sandbox verze: {definition.Key} v{definition.Version} (první nasazení).");
            else if (definition.Version > previousVersion)
                ui.Success($"Nová sandbox verze: {definition.Key} v{definition.Version} (předchozí v{previousVersion}).");
            else
                ui.Success($"Sandbox je aktuální: {definition.Key} v{definition.Version} (BPMN beze změny, nová verze nevznikla).");
        }

        if (latestDefinitions.Length == 0)
            throw new InvalidOperationException("Camunda did not publish an executable process definition from the saved BPMN.");

        var testDefinition = await ui.Select(latestDefinitions,
            definition => $"{definition.Name ?? definition.Key} | {definition.Key} | v{definition.Version} | LATEST SANDBOX",
            ct);
        if (testDefinition is null)
            return;

        if (await ui.Menu(["Spustit tuto sandbox verzi a otestovat", "Zatím jen uložit v sandboxu"], ct) != 1)
            return;

        await StartDefinitionAsync(testDefinition, ct);
    }

    private static HashSet<string> ExecutableProcessKeys(string path)
    {
        XNamespace bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
        var document = XDocument.Load(path);
        return document.Descendants(bpmn + "process")
            .Where(process => string.Equals((string?)process.Attribute("isExecutable"), "true", StringComparison.OrdinalIgnoreCase))
            .Select(process => (string?)process.Attribute("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task PromoteSandboxModelAsync(CancellationToken ct, string? path = null)
    {
        path ??= await ui.Select(BpmnEditorHost.DiscoverModelFiles().ToArray(), SandboxWorkspace.Label, ct);
        if (path is null)
            return;
        path = BpmnEditorHost.ResolveEditableModelPath(path, Directory.GetCurrentDirectory());

        var target = CamundaConnections.MainAddress;
        CamundaConnections.EnsureSeparate(target);
        ui.Field("Hlavní Camunda", target);
        ui.Field("Soubor", SandboxWorkspace.Label(path));
        ui.Line("  Publikuje se celý importovaný balíček včetně přiložených modelů, DMN a formulářů.");
        ui.Line("  Publikace vytvoří definici; existující instance zůstávají na své verzi.");
        if (await ui.Menu(["Potvrdit nasazení na hlavní Camundu", "Zpět bez nasazení"], ct) != 1)
            return;
        using var http = CamundaConnections.Create(target);
        await DeployAsync(path, ct, new CamundaClient(http));
    }

}
