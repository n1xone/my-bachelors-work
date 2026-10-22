using System.Net;
using Camunda.Api;

namespace Bakalarka.ConsoleApp;

internal sealed partial class InteractiveConsole
{
    private async Task InstanceAsync(string id, CancellationToken ct)
    {
        while (true)
        {
            ProcessInstance process;
            try { process = await api.GetInstanceAsync(id, ct); }
            catch (CamundaException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                ui.Success("Instance již není aktivní. Ověřuji historii.");
                await ShowHistoryAsync(id, ct);
                return;
            }
            var tasks = await All(offset => api.GetTasksAsync(id, offset, 100, ct));
            var external = await All(offset => api.GetExternalTasksAsync(id, offset, 100, ct));
            var events = await All(offset => api.GetSubscriptionsAsync(id, offset, 100, ct));
            var incidents = await All(offset => api.GetIncidentsAsync(id, offset, 100, ct));
            var children = await All(offset => api.GetChildInstancesAsync(id, offset, 100, ct));
            ui.Heading(DefinitionName(process.DefinitionId));
            ui.Field("Instance", id);
            ui.Field("Označení", process.BusinessKey);
            ui.Field("Stav", process.Suspended ? "Pozastaven" : "Probíhá");
            ui.Line($"  Úlohy: {tasks.Count}   Externí: {external.Count}   Události: {events.Count}   Chyby: {incidents.Count}   Podprocesy: {children.Count}");
            foreach (var task in tasks) ui.Field("Úloha", $"{task.Name ?? task.TaskDefinitionKey} • {task.Assignee ?? "nepřiřazena"}");
            foreach (var task in external) ui.Field("Externí úloha", task.TopicName);
            foreach (var subscription in events) ui.Field("Čeká na událost", $"{subscription.EventType}: {subscription.EventName}");
            foreach (var child in children) ui.Field("Podproces", DefinitionName(child.DefinitionId));
            foreach (var incident in incidents) ui.Field("Chyba", $"{incident.IncidentType}: {incident.IncidentMessage}");
            ui.Line();
            ui.Line("  1 Úlohy    2 Externí úlohy    3 Zprávy / signály    4 Proměnné");
            ui.Line("  5 Pozastavit / obnovit    6 Historie    7 Podprocesy    8 Joby / chyby");
            ui.Line("  9 Aktivity / přijímací úloha    10 Zrušit proces    0 Zpět    Enter Obnovit");
            var command = await ui.Ask("Akce", ct);
            if (command == "0") return;
            await GuardAsync(async () =>
            {
                switch (command)
                {
                    case "": break;
                    case "1":
                        var task = await ui.Select(tasks, t => $"{t.Name ?? t.TaskDefinitionKey} | {t.Assignee ?? "nepřiřazena"}", ct);
                        if (task is not null) await UserTaskAsync(process, task, ct);
                        break;
                    case "2":
                        var job = await ui.Select(external, t => $"{t.TopicName} | počet pokusů: {t.Retries?.ToString() ?? "—"} | worker: {t.WorkerId ?? "—"}", ct);
                        if (job is not null) await ExternalTaskAsync(process, job, ct);
                        break;
                    case "3": await EventsAsync(id, events, ct); break;
                    case "4": await VariablesAsync(id, ct); break;
                    case "5": await api.SetSuspendedAsync(id, !process.Suspended, ct); ui.Success(process.Suspended ? "Proces obnoven." : "Proces pozastaven."); break;
                    case "6": await ShowHistoryAsync(id, ct); break;
                    case "7":
                        var child = await ui.Select(children, p => $"{DefinitionName(p.DefinitionId)} | {p.Id}", ct);
                        if (child is not null) await InstanceAsync(child.Id, ct);
                        break;
                    case "8": await JobsAsync(id, ct); break;
                    case "9": await ActivitiesAsync(id, ct); break;
                    case "10":
                        var reason = await ui.Ask("Důvod zrušení (:back — vrátit se bez zrušení)", ct);
                        if (reason.Length == 0) { ui.Line("  Uveďte důvod zrušení."); break; }
                        await api.CancelAsync(id, reason, ct);
                        ui.Success("Proces zrušen.");
                        break;
                    default: ui.Line("  Zadejte číslo akce ze seznamu."); break;
                }
            });
        }
    }

    private async Task VariablesAsync(string id, CancellationToken ct)
    {
        var variables = await api.GetVariablesAsync(id, ct);
        ui.Heading("Proměnné");
        foreach (var (name, value) in variables)
            ui.Field($"{name} ({value.Type})", VariableEditor.IsSensitive(name) ? "••••" : VariableEditor.Display(value));
        switch (await ui.Menu(["Změnit jednu proměnnou", "Přidat proměnné"], ct))
        {
            case 1:
                var selected = await ui.Select(variables.Keys.ToArray(), x => x, ct);
                if (selected is null) return;
                var changes = await editor.EditAsync([new(selected, selected, variables[selected].Type)], variables, ct);
                await api.SetVariablesAsync(id, changes, ct);
                ui.Success("Proměnné uloženy.");
                break;
            case 2:
                await api.SetVariablesAsync(id, await editor.EditAsync([], null, ct), ct);
                ui.Success("Proměnné uloženy.");
                break;
        }
    }

    private async Task EventsAsync(string id, IReadOnlyList<EventSubscription> events, CancellationToken ct)
    {
        ui.Heading("Události • předání přímo enginu");
        var subscription = await ui.Select(events.Where(e => e.EventType is "message" or "signal").ToArray(),
            e => $"{e.EventType}: {e.EventName} | {e.ActivityId}", ct);
        if (subscription is null) return;
        var variables = await editor.EditAsync([], null, ct);
        if (subscription.EventType == "message")
            await api.CorrelateMessageAsync(subscription.EventName!, id, variables, ct);
        else
            await api.SendSignalAsync(subscription.EventName!, subscription.ExecutionId, variables, ct);
        ui.Success("Událost předána. Žádné e-maily nebyly odeslány.");
    }

    private async Task JobsAsync(string id, CancellationToken ct)
    {
        ui.Heading("Joby • časovače a asynchronní akce");
        var job = await ui.Select(await All(offset => api.GetJobsAsync(id, offset, 100, ct)),
            j => $"{j.Id} | počet pokusů: {j.Retries} | termín: {j.DueDate ?? "nyní"} | {j.ExceptionMessage ?? "bez chyby"}", ct);
        if (job is null) return;
        switch (await ui.Menu(["Nastavit počet opakovaných pokusů", "Provést nyní (i časovač před jeho termínem)"], ct))
        {
            case 1: await api.SetJobRetriesAsync(job.Id, await NonNegativeAsync("Počet pokusů", ct), ct); ui.Success("Počet pokusů změněn."); break;
            case 2: await api.ExecuteJobAsync(job.Id, ct); ui.Success("Job proveden."); break;
        }
    }

    private async Task ActivitiesAsync(string id, CancellationToken ct)
    {
        var tree = await api.GetActivitiesAsync(id, ct);
        ui.Heading("Aktivní kroky");
        var receiveTasks = new Dictionary<string, string>();
        void Print(ActivityInstance node, int depth)
        {
            ui.Line(new string(' ', depth * 2 + 2) + Terminal.Clean($"{node.ActivityName ?? node.ActivityId} [{node.ActivityType}]"));
            if (node.ActivityType == "receiveTask") receiveTasks[node.ActivityId] = node.ActivityName ?? node.ActivityId;
            foreach (var child in node.ChildActivityInstances) Print(child, depth + 1);
            foreach (var transition in node.ChildTransitionInstances) ui.Line($"    → {Terminal.Clean(transition.ActivityId)} [asynchronní pokračování]");
        }
        Print(tree, 0);
        if (receiveTasks.Count == 0)
        {
            ui.Line("  Není aktivní žádná přijímací úloha. Uživatelské úlohy otevřete volbou 1 — Úlohy.");
            await ui.Pause(ct);
            return;
        }

        ui.Line("  Pokračování je možné pouze pro aktivní přijímací úlohu (receive task).");
        var executions = await All(offset => api.GetExecutionsAsync(id, offset, 100, ct));
        var targets = executions.Where(execution => execution.ActivityId is not null && receiveTasks.ContainsKey(execution.ActivityId)).ToArray();
        if (targets.Length == 0)
        {
            ui.Line("  Pro aktivní přijímací úlohu nebylo nalezeno odpovídající execution.");
            await ui.Pause(ct);
            return;
        }

        var target = await ui.Select(targets,
            execution => $"{receiveTasks[execution.ActivityId!]} | {execution.Id}", ct);
        if (target is null) return;
        await api.TriggerExecutionAsync(target.Id, await editor.EditAsync([], null, ct), ct);
        ui.Success("Execution pokračuje.");
    }

    private async Task<int> NonNegativeAsync(string prompt, CancellationToken ct)
    {
        while (true)
        {
            var input = await ui.Ask(prompt, ct);
            if (int.TryParse(input, out var number) && number >= 0) return number;
            ui.Line("  Zadejte celé nezáporné číslo.");
        }
    }
}
