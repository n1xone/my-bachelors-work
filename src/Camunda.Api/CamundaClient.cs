using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Camunda.Api;

/// <summary>HTTP klient pro Camunda 7 REST. Volajici nastavi HttpClient, adresu, prihlaseni a casovy limit.</summary>
public sealed partial class CamundaClient : ICamundaClient
{
    private readonly HttpClient http;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CamundaClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (http.BaseAddress is not { IsAbsoluteUri: true } address ||
            (address.Scheme != "http" && address.Scheme != "https") ||
            !address.AbsolutePath.EndsWith('/') || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("HttpClient.BaseAddress must be an HTTP(S) engine URL ending in '/', without query or fragment.", nameof(http));
        this.http = http;
    }

    private static string E(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return Uri.EscapeDataString(value);
    }

    private static string Page(int first, int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        return $"firstResult={first}&maxResults={max}";
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await CheckAsync(response, ct).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new JsonException("Camunda returned an unexpected null response.");
    }

    private async Task SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await CheckAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new CamundaException(response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<Deployment> DeployAsync(
        string name,
        Stream bpmn,
        string fileName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bpmn);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        // Bufferovani zachova stream volajiciho i po uvolneni multipart obsahu.
        using var buffer = new MemoryStream();
        await bpmn.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return await DeployResourcesAsync(name, new Dictionary<string, byte[]> { [Path.GetFileName(fileName)] = buffer.ToArray() }, ct: ct);
    }

    public async Task<IReadOnlyList<DeploymentResource>> GetDeploymentResourcesAsync(string deploymentId, CancellationToken ct = default) =>
        await SendAsync<DeploymentResource[]>(HttpMethod.Get, $"deployment/{E(deploymentId)}/resources", null, ct);

    public async Task<byte[]> GetDeploymentResourceAsync(string deploymentId, string resourceId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"deployment/{E(deploymentId)}/resources/{E(resourceId)}/data", ct);
        await CheckAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<Deployment> DeployResourcesAsync(string name, IReadOnlyDictionary<string, byte[]> resources,
        string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (resources.Count == 0) throw new ArgumentException("Deployment requires resources.");
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(name), "deployment-name");
        content.Add(new StringContent("true"), "enable-duplicate-filtering");
        if (tenantId is not null) content.Add(new StringContent(tenantId), "tenant-id");
        var index = 0;
        foreach (var (resourceName, bytes) in resources)
            content.Add(new ByteArrayContent(bytes), "resource" + index++, resourceName);
        using var response = await http.PostAsync("deployment/create", content, ct);
        await CheckAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<Deployment>(Json, ct)
            ?? throw new JsonException("Camunda returned a null deployment.");
    }

    public Task DeleteDeploymentAsync(string deploymentId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"deployment/{E(deploymentId)}?cascade=false", null, ct);

    public async Task<IReadOnlyList<ProcessDefinition>> GetDefinitionsAsync(
        int firstResult = 0,
        int maxResults = 100,
        CancellationToken ct = default) =>
        await SendAsync<ProcessDefinition[]>(HttpMethod.Get, $"process-definition?sortBy=key&sortOrder=asc&{Page(firstResult, maxResults)}", null, ct);

    public Task<BpmnXml> GetBpmnAsync(string definitionId, CancellationToken ct = default) =>
        SendAsync<BpmnXml>(HttpMethod.Get, $"process-definition/{E(definitionId)}/xml", null, ct);

    public Task<ProcessInstance> StartAsync(
        string key,
        IReadOnlyDictionary<string, Variable>? variables = null,
        string? businessKey = null,
        CancellationToken ct = default) =>
        SendAsync<ProcessInstance>(HttpMethod.Post, $"process-definition/key/{E(key)}/start", new
        {
            variables,
            businessKey
        }, ct);

    public async Task<IReadOnlyList<ProcessInstance>> GetInstancesAsync(
        string? definitionKey = null,
        int firstResult = 0,
        int maxResults = 100,
        CancellationToken ct = default) =>
        await SendAsync<ProcessInstance[]>(HttpMethod.Get, $"process-instance?{Page(firstResult, maxResults)}" + (definitionKey is null ? "" : $"&processDefinitionKey={E(definitionKey)}"), null, ct);

    public Task<ProcessInstance> GetInstanceAsync(string instanceId, CancellationToken ct = default) =>
        SendAsync<ProcessInstance>(HttpMethod.Get, $"process-instance/{E(instanceId)}", null, ct);

    public Task CancelAsync(
        string instanceId,
        string reason,
        CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"process-instance/{E(instanceId)}?deleteReason={E(reason)}", null, ct);

    public Task SetSuspendedAsync(
        string instanceId,
        bool suspended,
        CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"process-instance/{E(instanceId)}/suspended", new
        {
            suspended
        }, ct);

    public Task<Dictionary<string, Variable>> GetVariablesAsync(string instanceId, CancellationToken ct = default) =>
        SendAsync<Dictionary<string, Variable>>(HttpMethod.Get, $"process-instance/{E(instanceId)}/variables?deserializeValues=false", null, ct);

    public Task SetVariablesAsync(
        string instanceId,
        IReadOnlyDictionary<string, Variable> variables,
        CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"process-instance/{E(instanceId)}/variables", new
        {
            modifications = variables
        }, ct);

    public Task<ActivityInstance> GetActivitiesAsync(string instanceId, CancellationToken ct = default) =>
        SendAsync<ActivityInstance>(HttpMethod.Get, $"process-instance/{E(instanceId)}/activity-instances", null, ct);

    public async Task<IReadOnlyList<UserTask>> GetTasksAsync(
        string instanceId,
        int firstResult = 0,
        int maxResults = 100,
        CancellationToken ct = default) =>
        await SendAsync<UserTask[]>(HttpMethod.Get, $"task?processInstanceId={E(instanceId)}&{Page(firstResult, maxResults)}", null, ct);

    public Task CompleteTaskAsync(
        string taskId,
        IReadOnlyDictionary<string, Variable>? variables = null,
        CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"task/{E(taskId)}/complete", new
        {
            variables
        }, ct);

    public async Task<IReadOnlyList<ExternalTask>> GetExternalTasksAsync(
        string instanceId,
        int firstResult = 0,
        int maxResults = 100,
        CancellationToken ct = default) =>
        await SendAsync<ExternalTask[]>(HttpMethod.Get, $"external-task?processInstanceId={E(instanceId)}&{Page(firstResult, maxResults)}", null, ct);

    public async Task<IReadOnlyList<ExternalTask>> FetchAndLockAsync(
        string workerId,
        IReadOnlyList<TopicSubscription> topics,
        int maxTasks = 10,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(topics);

        if (topics.Count == 0)
            throw new ArgumentException("At least one topic is required.", nameof(topics));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTasks, 1);

        foreach (var topic in topics)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(topic.TopicName);
            ArgumentOutOfRangeException.ThrowIfLessThan(topic.LockDuration, 1);
        }

        return await SendAsync<ExternalTask[]>(HttpMethod.Post, "external-task/fetchAndLock", new
        {
            workerId,
            maxTasks,
            usePriority = false,
            topics = topics.Select(t => new { t.TopicName, t.LockDuration, t.ProcessInstanceId, deserializeValues = false })
        }, ct);
    }

    public Task CompleteExternalTaskAsync(
        string taskId,
        string workerId,
        IReadOnlyDictionary<string, Variable>? variables = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        return SendAsync(HttpMethod.Post, $"external-task/{E(taskId)}/complete", new
        {
            workerId,
            variables
        }, ct);
    }

    public Task FailExternalTaskAsync(
        string taskId,
        string workerId,
        string errorMessage,
        int retries,
        long retryTimeout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfNegative(retries);
        ArgumentOutOfRangeException.ThrowIfNegative(retryTimeout);

        return SendAsync(HttpMethod.Post, $"external-task/{E(taskId)}/failure", new
        {
            workerId,
            errorMessage,
            retries,
            retryTimeout
        }, ct);
    }

    public Task CorrelateMessageAsync(
        string messageName,
        string instanceId,
        IReadOnlyDictionary<string, Variable>? variables = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        return SendAsync(HttpMethod.Post, "message", new
        {
            messageName,
            processInstanceId = instanceId,
            processVariables = variables,
            resultEnabled = false
        }, ct);
    }

    public async Task<IReadOnlyList<EventSubscription>> GetSubscriptionsAsync(
        string instanceId,
        int firstResult = 0,
        int maxResults = 100,
        CancellationToken ct = default) =>
        await SendAsync<EventSubscription[]>(HttpMethod.Get, $"event-subscription?processInstanceId={E(instanceId)}&{Page(firstResult, maxResults)}", null, ct);

    public async Task<IReadOnlyList<Incident>> GetIncidentsAsync(
        string instanceId,
        int firstResult = 0,
        int maxResults = 100,
        CancellationToken ct = default) =>
        await SendAsync<Incident[]>(HttpMethod.Get, $"incident?processInstanceId={E(instanceId)}&{Page(firstResult, maxResults)}", null, ct);

    public Task<HistoricProcess> GetHistoryAsync(string instanceId, CancellationToken ct = default) =>
        SendAsync<HistoricProcess>(HttpMethod.Get, $"history/process-instance/{E(instanceId)}", null, ct);

    public async Task<IReadOnlyList<HistoricActivity>> GetActivityHistoryAsync(
        string instanceId,
        int firstResult = 0,
        int maxResults = 100,
        CancellationToken ct = default) =>
        await SendAsync<HistoricActivity[]>(HttpMethod.Get, $"history/activity-instance?processInstanceId={E(instanceId)}&sortBy=startTime&sortOrder=asc&{Page(firstResult, maxResults)}", null, ct);
}
