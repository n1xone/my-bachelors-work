using Camunda.Api;

namespace Bakalarka.ConsoleApp;

internal sealed partial class InteractiveConsole
{
    private async Task UserTaskAsync(ProcessInstance process, UserTask task, CancellationToken ct)
    {
        ui.Heading(task.Name ?? task.TaskDefinitionKey);
        ui.Field("Řešitel", task.Assignee ?? "nepřiřazena");
        ui.Field("ID úlohy", task.Id);
        switch (await ui.Menu(["Vyplnit údaje a dokončit", "Převzít úlohu (claim)", "Uvolnit úlohu (unclaim)"], ct))
        {
            case 1:
                var current = await api.GetTaskFormVariablesAsync(task.Id, ct);
                var fields = await FieldsAsync(process.DefinitionId, task.TaskDefinitionKey, ct);
                if (fields.Count == 0) ui.Line("  Úloha nemá definovaná pole. Podle potřeby přidejte proměnné podle názvu.");
                var variables = await editor.EditAsync(fields, current, ct);
                await api.SubmitTaskFormAsync(task.Id, variables, ct);
                ui.Success("Údaje předány; úloha dokončena (delegovaná úloha byla vrácena vlastníkovi).");
                break;
            case 2:
                var user = await ui.Ask("ID uživatele / Enter — demo", ct);
                await api.ClaimTaskAsync(task.Id, user.Length == 0 ? "demo" : user, ct);
                ui.Success("Úloha převzata.");
                break;
            case 3:
                await api.UnclaimTaskAsync(task.Id, ct);
                ui.Success("Úloha uvolněna.");
                break;
        }
    }

    private async Task ExternalTaskAsync(ProcessInstance process, ExternalTask task, CancellationToken ct)
    {
        ui.Heading("Externí úloha • " + task.TopicName);
        ui.Field("ID", task.Id);
        ui.Field("Chyba", task.ErrorMessage);
        ui.Line("  Dokončením předáváte výsledek externí práce do Camundy.");
        ui.Line("  Konzole neodesílá e-maily ani nezapisuje do podnikové databáze.");
        switch (await ui.Menu(["Předat výsledek a dokončit", "Nahlásit chybu při zpracování", "Nastavit opakované pokusy"], ct))
        {
            case 1:
                var variables = await editor.EditAsync(ExampleInputs.For(Key(process.DefinitionId), task.TopicName), null, ct);
                // Zamknout vybrany ukol az po dokonceni zadavani udaju.
                await api.LockExternalTaskAsync(task.Id, workerId, 30000, ct);
                await api.CompleteExternalTaskAsync(task.Id, workerId, variables, ct);
                ui.Success("Externí úloha dokončena.");
                break;
            case 2:
                var message = await ui.Ask("Popis chyby", ct);
                var retries = await NonNegativeAsync("Zbývající pokusy (0 vytvoří incident)", ct);
                await api.LockExternalTaskAsync(task.Id, workerId, 30000, ct);
                await api.FailExternalTaskAsync(task.Id, workerId, message, retries, 1000, ct);
                ui.Success("Chyba zaznamenána.");
                break;
            case 3:
                await api.SetExternalTaskRetriesAsync(task.Id, await NonNegativeAsync("Počet pokusů", ct), ct);
                ui.Success("Počet pokusů změněn.");
                break;
        }
    }
}
