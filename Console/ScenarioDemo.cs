using System.Net;
using Camunda.Api;

namespace Bakalarka.ConsoleApp;

/// <summary>Vstupy konkretniho modelu patri sem, nikoli do znovupouzitelne knihovny API.</summary>
internal static class ScenarioDemo
{
    public static async Task RunAsync(ICamundaClient api, string root, CancellationToken ct)
    {
        foreach (var path in new[] { "models/registration-console.bpmn", "models/order-approval.bpmn" })
        {
            await using var file = File.OpenRead(Path.Combine(root, path));
            var deployment = await api.DeployAsync("api-demo-" + Path.GetFileNameWithoutExtension(path), file, Path.GetFileName(path), ct);
            Console.WriteLine($"DEPLOY {path}: {deployment.Id}");
        }
        await Registration(api, ct);
        await Order(api, approved: true, ct);
        await Order(api, approved: false, ct);
        await Incident(api, ct);
        Console.WriteLine("PASS: registrace, schválení, zamítnutí, incident a zrušení; stejné C# API.");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("  OK: " + message);
    }

    private static async Task WithInstance(ICamundaClient api, string key, Func<string, Task> scenario, CancellationToken ct)
    {
        var instance = await api.StartAsync(key, businessKey: "api-demo-" + Guid.NewGuid().ToString("N"), ct: ct);
        Console.WriteLine($"START {key}: {instance.Id}");
        try { await scenario(instance.Id); }
        finally
        {
            // Uklidit pouze instanci tohoto dema, i po neuspechu testu nebo Ctrl+C.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await api.GetInstanceAsync(instance.Id, cleanup.Token);
                await api.CancelAsync(instance.Id, "API demo cleanup", cleanup.Token);
            }
            catch (CamundaException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
            catch (Exception ex) { Console.Error.WriteLine($"Cleanup {instance.Id}: {ex.Message}"); }
        }
    }

    private static async Task CompleteUser(ICamundaClient api, string id, string activity, Dictionary<string, Variable>? vars, CancellationToken ct)
    {
        var task = (await api.GetTasksAsync(id, ct: ct)).Single(t => t.TaskDefinitionKey == activity);
        await api.CompleteTaskAsync(task.Id, vars, ct);
        Console.WriteLine($"  USER TASK {activity}");
    }

    private static async Task CompleteExternal(ICamundaClient api, string id, string topic, Dictionary<string, Variable>? vars, CancellationToken ct)
    {
        var worker = "api-demo-" + id;
        var tasks = await api.FetchAndLockAsync(worker, [new(topic, ProcessInstanceId: id)], ct: ct);
        Check(tasks.Count == 1, $"external topic {topic}");
        await api.CompleteExternalTaskAsync(tasks[0].Id, worker, vars, ct);
    }

    private static async Task Completed(ICamundaClient api, string id, string endActivity, CancellationToken ct)
    {
        var history = await api.GetHistoryAsync(id, ct);
        Check(history.State == "COMPLETED" && history.EndTime is not null, "history: COMPLETED");
        var activities = await api.GetActivityHistoryAsync(id, ct: ct);
        Check(activities.Any(a => a.ActivityId == endActivity && a.EndTime is not null), "end event: " + endActivity);
        Check((await api.GetIncidentsAsync(id, ct: ct)).Count == 0, "bez incidentů");
    }

    private static Task Registration(ICamundaClient api, CancellationToken ct) => WithInstance(api, "console_registration", async id =>
    {
        await CompleteUser(api, id, "registration_form", new()
        {
            ["userName"] = Variable.String("Test Student"),
            ["department"] = Variable.String("IT"),
            ["approved"] = Variable.Boolean(true)
        }, ct);
        await CompleteExternal(api, id, "store_registration", null, ct);
        Check((await api.GetSubscriptionsAsync(id, ct: ct)).Any(s => s.EventName == "registration_confirmed"), "čekání na potvrzení z konzole");
        await api.CorrelateMessageAsync("registration_confirmed", id, ct: ct);
        await Completed(api, id, "registration_completed", ct);
    }, ct);

    private static Task Order(ICamundaClient api, bool approved, CancellationToken ct) => WithInstance(api, "order_approval", async id =>
    {
        var instance = await api.GetInstanceAsync(id, ct);
        Check((await api.GetBpmnAsync(instance.DefinitionId, ct)).Bpmn20Xml.Contains("order_approval"), "čtení BPMN XML");
        await api.SetVariablesAsync(id, new Dictionary<string, Variable> { ["amount"] = Variable.Integer(250) }, ct);
        Check((await api.GetVariablesAsync(id, ct))["amount"].Value.GetInt32() == 250, "zápis a čtení proměnných");
        await api.SetSuspendedAsync(id, true, ct);
        Check((await api.GetInstanceAsync(id, ct)).Suspended, "pozastavení");
        await api.SetSuspendedAsync(id, false, ct);
        Check(!(await api.GetInstanceAsync(id, ct)).Suspended, "obnovení");
        await CompleteUser(api, id, "review_order", new() { ["approved"] = Variable.Boolean(approved) }, ct);
        if (approved)
        {
            await CompleteExternal(api, id, "prepare_order", new() { ["trackingNumber"] = Variable.String("DEMO-001") }, ct);
            Check((await api.GetSubscriptionsAsync(id, ct: ct)).Any(s => s.EventName == "order_delivered"), "čekání na order_delivered");
            await api.CorrelateMessageAsync("order_delivered", id, ct: ct);
        }
        await Completed(api, id, approved ? "order_completed" : "order_rejected", ct);
    }, ct);

    private static Task Incident(ICamundaClient api, CancellationToken ct) => WithInstance(api, "order_approval", async id =>
    {
        await CompleteUser(api, id, "review_order", new() { ["approved"] = Variable.Boolean(true) }, ct);
        var worker = "api-demo-" + id;
        var task = (await api.FetchAndLockAsync(worker, [new("prepare_order", ProcessInstanceId: id)], ct: ct)).Single();
        await api.FailExternalTaskAsync(task.Id, worker, "Simulovaná chyba pro test API", 0, 0, ct);
        Check((await api.GetIncidentsAsync(id, ct: ct)).Any(i => i.IncidentType == "failedExternalTask"), "incident po vyčerpání pokusů");
        await api.CancelAsync(id, "API demo: cancellation test", ct);
        Check((await api.GetHistoryAsync(id, ct)).State == "EXTERNALLY_TERMINATED", "zrušení odlišeno od úspěšného dokončení");
    }, ct);
}
