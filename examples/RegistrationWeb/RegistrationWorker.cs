using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

public sealed class RegistrationWorker(
    IHttpClientFactory clients,
    UserRepository users,
    RegistrationStore registrations,
    ILogger<RegistrationWorker> logger,
    ApprovalLinks approvalLinks,
    IConfiguration configuration,
    EmailSender emailSender,
    IDataProtectionProvider dataProtection) : BackgroundService
{
    // Kazda instance aplikace potrebuje vlastni ID vlastnika zamku.
    private readonly Dictionary<string, DateTimeOffset> cleanupRetries = new();
    private readonly string workerId = $"registration-{Guid.NewGuid():N}";
    private readonly IDataProtector demoPasswordProtector = dataProtection.CreateProtector("RegistrationDemoPassword");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var http = clients.CreateClient("camunda");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExpireRegistrationsAsync(http, stoppingToken);
                await DeliverApprovalsAsync(http, stoppingToken);
                var tasks = await FetchTasksAsync(http, stoppingToken);

                foreach (var task in tasks)
                {
                    await ProcessTaskAsync(task, stoppingToken);
                    await CompleteTaskAsync(http, task, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Zachovat beh workeru pri vypadku enginu nebo vadnem ukolu.
                // Nedokonceny ukol lze znovu prevzit po vyprseni zamku.
                logger.LogError(exception, "External task processing failed");
            }

            // Zpozdit dalsi dotaz i po prazdnem vysledku nebo chybe, aby smycka nezatezovala server.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ExpireRegistrationsAsync(HttpClient http, CancellationToken ct)
    {
        await registrations.Gate.WaitAsync(ct);
        try
        {
            var records = registrations.Read();
            foreach (var item in records.Where(x => !x.Cancelled && x.ProcessId != null
                && x.Expires <= DateTimeOffset.UtcNow))
            {
                if (cleanupRetries.TryGetValue(item.Token, out var retryAt) && retryAt > DateTimeOffset.UtcNow) continue;
                try
                {
                    await RegistrationLifecycle.CancelAsync(http, item, approvalLinks,
                        "Registration confirmation expired", ct);
                    registrations.Save(records);
                    cleanupRetries.Remove(item.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    cleanupRetries[item.Token] = DateTimeOffset.UtcNow.AddMinutes(1);
                    logger.LogWarning(ex, "Registration cleanup failed for process {ProcessId}; retrying in one minute", item.ProcessId);
                }
            }
        }
        finally { registrations.Gate.Release(); }
    }

    private async Task<List<ExternalTaskInfo>> FetchTasksAsync(
        HttpClient http, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("external-task/fetchAndLock", new
        {
            workerId,
            // Zpracovat jeden ukol najednou, aby cekajici ukoly neztratily zamky.
            maxTasks = 1,
            topics = new[] { "check_user", "create_account", "db_save", "send_email" }
                .Select(topic => new { topicName = topic, lockDuration = 60000 })
        }, cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ExternalTaskInfo>>(
            cancellationToken: cancellationToken) ?? [];
    }

    private async Task ProcessTaskAsync(ExternalTaskInfo task, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new InvalidOperationException("Camunda returned a task without an ID.");

        switch (task.TopicName)
        {
            case "check_user":
                _ = RequiredVariable(task, "userEmail");
                break;

            case "create_account":
                // Validace probiha v dalsim kroku BPMN; data formulare zatim neparsovat.
                await registrations.Gate.WaitAsync(ct);
                try
                {
                    if (!string.IsNullOrWhiteSpace(task.ProcessInstanceId)
                        && !registrations.Read().Any(x => x.ProcessId == task.ProcessInstanceId && x.Cancelled))
                        approvalLinks.Create(task.ProcessInstanceId, task);
                }
                finally { registrations.Gate.Release(); }
                break;

            case "db_save":
                // Opakovane doruceni stejneho ukolu nesmi vytvorit duplicitniho uzivatele.
                await registrations.Gate.WaitAsync(ct);
                try
                {
                    var records = registrations.Read();
                    var registration = records.Find(x => x.ProcessId == task.ProcessInstanceId);
                    var user = ReadUser(task);
                    user.Password = registration?.ProtectedDemoPassword is { } protectedPassword
                        ? demoPasswordProtector.Unprotect(protectedPassword) : null;
                    users.Save(task.Id, user);
                    // Po ulozeni do users.txt neni v rade potreba dalsi kopie v citelnem textu.
                    if (registration?.ProtectedDemoPassword != null)
                    {
                        registration.ProtectedDemoPassword = null;
                        registrations.Save(records);
                    }
                }
                finally { registrations.Gate.Release(); }
                break;

            case "send_email":
                await emailSender.SendAsync(RequiredVariable(task, "userEmail"), "Registrace dokončena",
                    "Váš e-mail byl potvrzen a registrace byla zpracována. Děkujeme.", ct);
                break;

            default:
                throw new InvalidOperationException("Unexpected external task topic.");
        }
    }

    private async Task CompleteTaskAsync(
        HttpClient http, ExternalTaskInfo task, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            $"external-task/{Uri.EscapeDataString(task.Id!)}/complete",
            new { workerId, variables = task.TopicName == "check_user"
                ? new Dictionary<string, object> { ["userExists"] = new { value = users.ContainsEmail(RequiredVariable(task, "userEmail")), type = "Boolean" } }
                : new Dictionary<string, object>() }, cancellationToken);

        response.EnsureSuccessStatusCode();
        logger.LogDebug("Completed {Topic} task {TaskId}", task.TopicName, task.Id);

    }

    private async Task DeliverApprovalsAsync(HttpClient http, CancellationToken ct)
    {
        foreach (var item in approvalLinks.Pending())
        {
            await approvalLinks.Gate.WaitAsync(ct);
            try
            {
                if (!approvalLinks.TryGetRegistration(item.Token, out var current) || current.Confirmed) continue;
                var subscriptions = await http.GetFromJsonAsync<System.Text.Json.JsonElement>(
                    $"event-subscription?processInstanceId={item.ProcessInstanceId}&eventType=message&eventName=approve_user", ct);
                if (subscriptions.GetArrayLength() == 0) continue;
                var baseUrl = configuration["App:PublicUrl"] ?? throw new InvalidOperationException("Configure App:PublicUrl.");
                await emailSender.SendAsync(item.Email, "Potvrzení registrace",
                    $"Dobrý den,\n\nPotvrďte registraci otevřením odkazu a stisknutím tlačítka:\n{baseUrl.TrimEnd('/')}/approve?token={item.Token}\n\nRegistraci potvrďte do 48 hodin od jejího zahájení. Pokud jste registraci neprováděli, zprávu ignorujte.", ct);
                approvalLinks.Update(item.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Confirmation email delivery failed; will retry");
            }
            finally { approvalLinks.Gate.Release(); }
        }
    }

    private static User ReadUser(ExternalTaskInfo task)
    {
        var rawDate = RequiredVariable(task, "birthDate");
        // Datum Camundy muze obsahovat cas a posun. Zachovat zadane kalendarni datum
        // a neprevadet je do mistniho casoveho pasma serveru.
        var datePart = rawDate.Split('T')[0];
        if (!DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var birthDate))
            throw new InvalidOperationException("birthDate must use an ISO yyyy-MM-dd date.");

        return new User
        {
            Name = RequiredVariable(task, "userFName"),
            Surname = RequiredVariable(task, "userRName"),
            Email = RequiredVariable(task, "userEmail"),
            BirthDate = birthDate
        };
    }

    private static string RequiredVariable(ExternalTaskInfo task, string name)
    {
        if (task.Variables is null || !task.Variables.TryGetValue(name, out var variable)
            || variable is null || string.IsNullOrWhiteSpace(variable.Value?.ToString()))
            throw new InvalidOperationException($"Required process variable '{name}' is missing.");
        return variable.Value.ToString()!;
    }
}
