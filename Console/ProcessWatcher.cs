using System.Net;
using System.Text.Json;
using Camunda.Api;

namespace Bakalarka.ConsoleApp;

/// <summary>Periodicke cteni stavu procesu. Pozorovani nejsou auditnim zaznamem enginu.</summary>
internal sealed class ProcessWatcher(ICamundaClient api, TextWriter output)
{
    private sealed record Snapshot(ProcessInstance Instance, Dictionary<string, string> Items);

    private readonly Dictionary<string, Snapshot> previous = new();

    private bool connected;

    public async Task RunAsync(
        string? key,
        int intervalMs,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalMs, 200);
        await output.WriteLineAsync($"Sledování Camundy: {(key ?? "všechny procesy")}. Interval {intervalMs} ms. Ukončení: Ctrl+C.");
        await output.WriteLineAsync("Pracujte v Camunda Tasklist/Cockpit nebo v jiném klientu. Zobrazuji pozorované změny; hodnoty proměnných nevypisuji.");
        string? lastError = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await PollAsync(key, ct);

                if (lastError is not null)
                    Log("SPOJENÍ OBNOVENO");
                lastError = null;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError)
            {
                var error = ex.GetBaseException().Message;

                if (error != lastError)
                    Log("ČEKÁM NA CAMUNDU: " + error);
                lastError = error;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (lastError != "timeout")
                    Log("ČEKÁM NA CAMUNDU: vypršel čas požadavku.");
                lastError = "timeout";
            }

            await Task.Delay(intervalMs, ct);
        }
    }

    private void Log(string text) => output.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {text}");

    private async Task PollAsync(string? key, CancellationToken ct)
    {
        var instances = await Pagination.ReadAllAsync((offset, limit) => api.GetInstancesAsync(key, offset, limit, ct));

        if (!connected)
        {
            Log($"PŘIPOJENO. Aktivních instancí: {instances.Count}. Čekám na vaše akce…");
            connected = true;
        }

        var present = instances.Select(p => p.Id).ToHashSet();

        foreach (var instance in instances.DistinctBy(p => p.Id))
        {
            Snapshot current;

            try
            {
                current = await ReadAsync(instance, ct);
            }
            catch (CamundaException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Proces muze skoncit mezi nactenim seznamu a detailu. Znovu jej overime pri dalsim dotazu.

                if (!previous.ContainsKey(instance.Id))
                {
                    previous[instance.Id] = new(instance, new());
                    Log($"PROCES ZJIŠTĚN {instance.Id} | {instance.DefinitionId}");
                }
                continue;
            }

            if (!previous.TryGetValue(instance.Id, out var old))
                Log($"PROCES ZJIŠTĚN {instance.Id} | {instance.DefinitionId} | businessKey={instance.BusinessKey ?? "—"}");
            else if (old.Instance.Suspended != instance.Suspended)
                Log($"{instance.Id} | {(instance.Suspended ? "POZASTAVEN" : "OBNOVEN")}");

            foreach (var (item, value) in current.Items)
            {
                if (old is null || !old.Items.TryGetValue(item, out var before))
                    Log($"{instance.Id} | NOVÉ {Describe(item, value)}");
                else if (before != value)
                    Log($"{instance.Id} | ZMĚNA {Describe(item, value)}");
            }

            if (old is not null)
                foreach (var (item, value) in old.Items)

                    if (!current.Items.ContainsKey(item))
                        Log($"{instance.Id} | JIŽ NENÍ AKTIVNÍ {Describe(item, value)}");
            previous[instance.Id] = current;
        }

        foreach (var id in previous.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            // Strankovani s offsetem a soubezne zmeny mohou vynechat stale bezici instanci.

            try
            {
                await api.GetInstanceAsync(id, ct);
                continue;
            }
            catch (CamundaException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }

            try
            {
                var history = await api.GetHistoryAsync(id, ct);

                if (history.EndTime is null)
                    continue;
                Log($"PROCES UKONČEN {id} | {history.State} | {history.DeleteReason ?? "—"}");
            }
            catch (CamundaException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Log($"PROCES NENÍ V RUNTIME {id} | historie nedostupná, výsledek nelze určit");
            }
            previous.Remove(id);
        }
    }

    private static string Describe(string item, string value) => item.StartsWith("PROMĚNNÁ ")
                ? item + " (hodnota skryta)" : item + " | " + value;

    private async Task<Snapshot> ReadAsync(ProcessInstance instance, CancellationToken ct)
    {
        var items = new Dictionary<string, string>();

        foreach (var task in await Pagination.ReadAllAsync((offset, limit) => api.GetTasksAsync(instance.Id, offset, limit, ct)))
            items["ÚLOHA " + task.Id] = $"{task.Name ?? task.TaskDefinitionKey} | řešitel: {task.Assignee ?? "nepřiřazeno"}";

        foreach (var task in await Pagination.ReadAllAsync((offset, limit) => api.GetExternalTasksAsync(instance.Id, offset, limit, ct)))
            items["EXTERNÍ ÚLOHA " + task.Id] = $"{task.TopicName} | worker: {task.WorkerId ?? "—"} | retries: {task.Retries?.ToString() ?? "—"}";

        foreach (var (name, variable) in await api.GetVariablesAsync(instance.Id, ct))
            items["PROMĚNNÁ " + name] = JsonSerializer.Serialize(variable);

        foreach (var subscription in await Pagination.ReadAllAsync((offset, limit) => api.GetSubscriptionsAsync(instance.Id, offset, limit, ct)))
            items["ČEKÁNÍ " + subscription.Id] = $"{subscription.EventType}: {subscription.EventName} | {subscription.ActivityId}";

        foreach (var incident in await Pagination.ReadAllAsync((offset, limit) => api.GetIncidentsAsync(instance.Id, offset, limit, ct)))
            items["INCIDENT " + incident.Id] = $"{incident.IncidentType} | {incident.IncidentMessage}";
        AddActivities(await api.GetActivitiesAsync(instance.Id, ct), items);

        return new(instance, items);
    }

    private static void AddActivities(ActivityInstance node, Dictionary<string, string> items)
    {
        foreach (var child in node.ChildActivityInstances)
        {
            items["AKTIVITA " + child.Id] = $"{child.ActivityName ?? child.ActivityId} ({child.ActivityType})";
            AddActivities(child, items);
        }

        foreach (var transition in node.ChildTransitionInstances)
            items["PŘECHOD " + transition.Id] = $"{transition.ActivityName ?? transition.ActivityId} ({transition.ActivityType})";
    }
}
