using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Camunda.Api;
using Bakalarka.ConsoleApp;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

if (args.Length == 0) args = ["menu"];

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

    Výchozí CAMUNDA_URL=http://localhost:8080/engine-rest/
    Volitelné: CAMUNDA_USERNAME, CAMUNDA_PASSWORD (HTTP Basic).
    Proměnné: {"approved":{"value":true,"type":"Boolean"}}
    Seznamy jsou stránkované (výchozí limit 100). Výstup je JSON.
    Demo nasadí oba modely a spustí testovací instance; dokončí je nebo zruší.
    """);

    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var ct = cancellation.Token;

try
{
    var url = Environment.GetEnvironmentVariable("CAMUNDA_URL") ?? "http://localhost:8080/engine-rest/";
    using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
    var username = Environment.GetEnvironmentVariable("CAMUNDA_USERNAME");

    if (!string.IsNullOrEmpty(username))
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + Environment.GetEnvironmentVariable("CAMUNDA_PASSWORD"))));
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
            await new InteractiveConsole(api, new Terminal(Console.In, Console.Out)).RunAsync(http.BaseAddress!.ToString(), ct);
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

        default:
            throw new ArgumentException($"Neznámý příkaz '{args[0]}'; viz help.");
    }

    return 0;
}
catch (OperationCanceledException) when (ct.IsCancellationRequested) { Console.Error.WriteLine("Zrušeno uživatelem."); return 130; }
catch (OperationCanceledException) { Console.Error.WriteLine("Camunda neodpověděla včas. U zápisu ověřte stav před opakováním."); return 1; }
catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or JsonException or FormatException or OverflowException or InvalidOperationException)
{
    Console.Error.WriteLine(ex.Message);

    if (ex.InnerException is not null)
        Console.Error.WriteLine(ex.GetBaseException().Message);

    return 1;
}
