using System.Text.Json;
using Camunda.Api;
using Bakalarka.ConsoleApp;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

var chooseEnvironment = args.Length == 0;
if (chooseEnvironment) args = ["menu"];

var sandboxMode = args[0] == "sandbox";
if (sandboxMode)
{
    args = args.Skip(1).ToArray();
    if (args.Length == 0) args = ["menu"];
}

if (args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("""
    Camunda 7 — univerzální C# API / konzolový klient
    Použití: dotnet run -- <příkaz> [argumenty]

    menu                              (výchozí interaktivní ovládání)
    watch [processKey|-] [intervalMs]  (samostatné sledování do Ctrl+C)
    deploy <soubor.bpmn> [název]
    definitions [offset] [limit]
    xml <definitionId>
    start <processKey> [variables.json|-] [businessKey]
    instances [processKey|-] [offset] [limit]
    instance <instanceId>
    activities <instanceId>
    variables <instanceId>
    set-variables <instanceId> <variables.json>
    tasks <instanceId> [offset] [limit]
    complete <taskId> [variables.json]
    external-tasks <instanceId> [offset] [limit]
    fetch <topic> <workerId> <instanceId> [maxTasks]
    external-complete <taskId> <workerId> [variables.json]
    external-fail <taskId> <workerId> <retries> <retryTimeoutMs> <message>
    message <instanceId> <messageName> [variables.json]
    subscriptions <instanceId> [offset] [limit]
    incidents <instanceId> [offset] [limit]
    suspend <instanceId>
    resume <instanceId>
    cancel <instanceId> <reason>
    history <instanceId>
    activity-history <instanceId> [offset] [limit]
    demo [cesta-ke-kořeni-projektu]
    visualize <model.bpmn>              (local browser-based BPMN editor)
    sandbox import <definitionId>       (copy source deployment and deploy only to sandbox)
    sandbox <command> [arguments]        (force Camunda requests to localhost:18080)

    Výchozí CAMUNDA_URL=http://localhost:8080/engine-rest/
    Volitelné: CAMUNDA_USERNAME, CAMUNDA_PASSWORD (HTTP Basic).
    Proměnné: {"approved":{"value":true,"type":"Boolean"}}
    Seznamy jsou stránkované (výchozí limit 100). Výstup je JSON.
    Demo nasadí oba modely a spustí testovací instance; dokončí je nebo zruší.
    """);

    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (!BpmnEditorHost.TryHandleEditorCtrlC())
        cancellation.Cancel();
};
var ct = cancellation.Token;
var httpClients = new List<HttpClient>();
var terminal = new Terminal(Console.In, Console.Out);

try
{
    if (chooseEnvironment)
    {
        var selection = await terminal.Menu(
        [
            "Sandbox Camunda — isolated tests (127.0.0.1:18080)",
            "Main Camunda — primary server (localhost:8080)"
        ], ct, "Exit");

        if (selection == 0)
            return 0;
        sandboxMode = selection == 1;
    }

    var url = sandboxMode ? CamundaConnections.SandboxAddress : CamundaConnections.MainAddress;
    HttpClient CreateHttpClient(string address)
    {
        var client = CamundaConnections.Create(address);
        httpClients.Add(client);
        return client;
    }
    var sourceApi = new CamundaClient(CreateHttpClient(CamundaConnections.MainAddress));
    if (sandboxMode) CamundaConnections.EnsureSeparate(CamundaConnections.MainAddress);

    var http = CreateHttpClient(url);
    ICamundaClient api = new CamundaClient(http);
    string Arg(int index) => index < args.Length ? args[index] : throw new ArgumentException($"Chybí argument {index}; viz help.");
    int Number(int index, int fallback) => index < args.Length ? int.Parse(args[index]) : fallback;
    async Task<Dictionary<string, Variable>?> Vars(int index)
    {
        if (index >= args.Length || args[index] == "-")
            return null;
        await using var file = File.OpenRead(args[index]);
        var result = await JsonSerializer.DeserializeAsync<Dictionary<string, Variable>>(file, json, ct)
            ?? throw new ArgumentException("Soubor proměnných musí obsahovat JSON objekt.");

        foreach (var (name, variable) in result)
            if (string.IsNullOrWhiteSpace(name) || variable is null || string.IsNullOrWhiteSpace(variable.Type) || variable.Value.ValueKind == JsonValueKind.Undefined)
                throw new ArgumentException($"Neplatná proměnná '{name}': očekává se value a type.");

        return result;
    }

    void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, json));

    switch (args[0])
    {
        case "menu":
            while (true)
            {
                await new InteractiveConsole(api, terminal, sandboxMode, sourceApi)
                    .RunAsync(http.BaseAddress!.ToString(), ct, chooseEnvironment);

                if (!chooseEnvironment)
                    break;

                var selection = await terminal.Menu(
                [
                    "Sandbox Camunda — isolated tests (127.0.0.1:18080)",
                    "Main Camunda — primary server (localhost:8080)"
                ], ct, "Exit");

                if (selection == 0)
                    break;

                sandboxMode = selection == 1;
                if (sandboxMode) CamundaConnections.EnsureSeparate(CamundaConnections.MainAddress);
                var nextUrl = sandboxMode ? CamundaConnections.SandboxAddress : CamundaConnections.MainAddress;
                http = CreateHttpClient(nextUrl);
                api = new CamundaClient(http);
            }
            break;

        case "import" when sandboxMode:
            var sourceDefinitions = await Pagination.ReadAllAsync((offset, limit) => sourceApi.GetDefinitionsAsync(offset, limit, ct));
            var sourceDefinition = sourceDefinitions.SingleOrDefault(d => d.Id == Arg(1))
                ?? throw new ArgumentException("Source definition ID was not found.");
            var importedPath = await SandboxWorkspace.ImportAsync(sourceApi, sourceDefinition, Directory.GetCurrentDirectory(), ct);
            var importedDeployment = await SandboxWorkspace.DeployAsync(api, importedPath, ct);
            Print(new { path = importedPath, deployment = importedDeployment });
            break;

        case "watch":
            await new ProcessWatcher(api, Console.Out).RunAsync(args.Length > 1 && args[1] != "-" ? args[1] : null, Number(2, 1000), ct);
            break;

        case "deploy":
            await using (var file = File.OpenRead(Arg(1)))
                Print(await api.DeployAsync(args.Length > 2 ? args[2] : Path.GetFileNameWithoutExtension(args[1]), file, Path.GetFileName(args[1]), ct));
            break;

        case "definitions":
            Print(await api.GetDefinitionsAsync(Number(1, 0), Number(2, 100), ct));
            break;

        case "xml":
            Console.WriteLine((await api.GetBpmnAsync(Arg(1), ct)).Bpmn20Xml);
            break;

        case "start":
            Print(await api.StartAsync(Arg(1), await Vars(2), args.Length > 3 ? args[3] : null, ct));
            break;

        case "instances":
            Print(await api.GetInstancesAsync(args.Length > 1 && args[1] != "-" ? args[1] : null, Number(2, 0), Number(3, 100), ct));
            break;

        case "instance":
            Print(await api.GetInstanceAsync(Arg(1), ct));
            break;

        case "activities":
            Print(await api.GetActivitiesAsync(Arg(1), ct));
            break;

        case "variables":
            Print(await api.GetVariablesAsync(Arg(1), ct));
            break;

        case "set-variables":
            await api.SetVariablesAsync(Arg(1), await Vars(2) ?? throw new ArgumentException("Chybí soubor proměnných."), ct);
            Print(new
            {
                updated = true
            });
            break;

        case "tasks":
            Print(await api.GetTasksAsync(Arg(1), Number(2, 0), Number(3, 100), ct));
            break;

        case "complete":
            await api.CompleteTaskAsync(Arg(1), await Vars(2), ct);
            Print(new
            {
                completed = true
            });
            break;

        case "external-tasks":
            Print(await api.GetExternalTasksAsync(Arg(1), Number(2, 0), Number(3, 100), ct));
            break;

        case "fetch":
            Print(await api.FetchAndLockAsync(Arg(2), [new TopicSubscription(Arg(1), ProcessInstanceId: Arg(3))], Number(4, 10), ct));
            break;

        case "external-complete":
            await api.CompleteExternalTaskAsync(Arg(1), Arg(2), await Vars(3), ct);
            Print(new
            {
                completed = true
            });
            break;

        case "external-fail":
            await api.FailExternalTaskAsync(Arg(1), Arg(2), Arg(5), int.Parse(Arg(3)), long.Parse(Arg(4)), ct);
            Print(new
            {
                reported = true
            });
            break;

        case "message":
            await api.CorrelateMessageAsync(Arg(2), Arg(1), await Vars(3), ct);
            Print(new
            {
                correlated = true
            });
            break;

        case "subscriptions":
            Print(await api.GetSubscriptionsAsync(Arg(1), Number(2, 0), Number(3, 100), ct));
            break;

        case "incidents":
            Print(await api.GetIncidentsAsync(Arg(1), Number(2, 0), Number(3, 100), ct));
            break;

        case "suspend":
            await api.SetSuspendedAsync(Arg(1), true, ct);
            Print(new
            {
                suspended = true
            });
            break;

        case "resume":
            await api.SetSuspendedAsync(Arg(1), false, ct);
            Print(new
            {
                suspended = false
            });
            break;

        case "cancel":
            await api.CancelAsync(Arg(1), Arg(2), ct);
            Print(new
            {
                canceled = true
            });
            break;

        case "history":
            Print(await api.GetHistoryAsync(Arg(1), ct));
            break;

        case "activity-history":
            Print(await api.GetActivityHistoryAsync(Arg(1), Number(2, 0), Number(3, 100), ct));
            break;

        case "demo":
            await ScenarioDemo.RunAsync(api, args.Length > 1 ? args[1] : Directory.GetCurrentDirectory(), ct);
            break;

        case "visualize":
            var modelPath = Path.GetFullPath(Arg(1));
            if (!File.Exists(modelPath))
                throw new FileNotFoundException("BPMN model file was not found.", modelPath);
            var editorDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "BpmnEditor", "dist"));
            var liveEditor = new InteractiveConsole(api, terminal, sandboxMode);
            await BpmnEditorHost.RunAsync(modelPath, editorDirectory, ct, liveEditor.ApplySavedModelAsync, url);
            break;

        default:
            throw new ArgumentException($"Neznámý příkaz '{args[0]}'; viz help.");
    }

    return 0;
}
catch (OperationCanceledException) when (ct.IsCancellationRequested) { Console.Error.WriteLine("Zrušeno uživatelem."); return 130; }
catch (OperationCanceledException) { Console.Error.WriteLine("Camunda neodpověděla včas. U zápisu ověřte stav před opakováním."); return 1; }
catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or JsonException or FormatException or OverflowException or InvalidOperationException or PlatformNotSupportedException)
{
    Console.Error.WriteLine(ex.Message);

    if (ex.InnerException is not null)
        Console.Error.WriteLine(ex.GetBaseException().Message);

    return 1;
}
finally
{
    terminal.EndScreen();
    foreach (var client in httpClients)
        client.Dispose();
}
