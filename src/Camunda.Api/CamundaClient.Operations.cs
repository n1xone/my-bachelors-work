namespace Camunda.Api;

public sealed partial class CamundaClient
{
    public Task<ProcessInstance> StartByDefinitionAsync(string definitionId, IReadOnlyDictionary<string, Variable>? variables = null, string? businessKey = null, CancellationToken ct = default) =>
        SendAsync<ProcessInstance>(HttpMethod.Post, $"process-definition/{E(definitionId)}/start", new { variables, businessKey }, ct);

    public Task<Dictionary<string, Variable>> GetTaskFormVariablesAsync(string taskId, CancellationToken ct = default) =>
        SendAsync<Dictionary<string, Variable>>(HttpMethod.Get, $"task/{E(taskId)}/form-variables?deserializeValues=false", null, ct);

    public Task SubmitTaskFormAsync(string taskId, IReadOnlyDictionary<string, Variable> variables, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"task/{E(taskId)}/submit-form", new { variables }, ct);

    public Task ClaimTaskAsync(string taskId, string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return SendAsync(HttpMethod.Post, $"task/{E(taskId)}/claim", new { userId }, ct);
    }

    public Task UnclaimTaskAsync(string taskId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"task/{E(taskId)}/unclaim", new { }, ct);

    public Task LockExternalTaskAsync(string taskId, string workerId, long lockDuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfLessThan(lockDuration, 1);
        return SendAsync(HttpMethod.Post, $"external-task/{E(taskId)}/lock", new { workerId, lockDuration }, ct);
    }

    public Task SetExternalTaskRetriesAsync(string taskId, int retries, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retries);
        return SendAsync(HttpMethod.Put, $"external-task/{E(taskId)}/retries", new { retries }, ct);
    }

    public async Task<IReadOnlyList<Job>> GetJobsAsync(string instanceId, int firstResult = 0, int maxResults = 100, CancellationToken ct = default) =>
        await SendAsync<Job[]>(HttpMethod.Get, $"job?processInstanceId={E(instanceId)}&{Page(firstResult, maxResults)}", null, ct);

    public Task SetJobRetriesAsync(string jobId, int retries, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retries);
        return SendAsync(HttpMethod.Put, $"job/{E(jobId)}/retries", new { retries }, ct);
    }

    public Task ExecuteJobAsync(string jobId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"job/{E(jobId)}/execute", new { }, ct);

    public Task TriggerExecutionAsync(string executionId, IReadOnlyDictionary<string, Variable>? variables = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"execution/{E(executionId)}/signal", new { variables }, ct);

    public Task SendSignalAsync(string signalName, string executionId, IReadOnlyDictionary<string, Variable>? variables = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        return SendAsync(HttpMethod.Post, "signal", new { name = signalName, executionId, variables }, ct);
    }

    public async Task<IReadOnlyList<Execution>> GetExecutionsAsync(string instanceId, int firstResult = 0, int maxResults = 100, CancellationToken ct = default) =>
        await SendAsync<Execution[]>(HttpMethod.Get, $"execution?processInstanceId={E(instanceId)}&{Page(firstResult, maxResults)}", null, ct);

    public async Task<IReadOnlyList<HistoricProcess>> GetProcessHistoryAsync(int firstResult = 0, int maxResults = 100, CancellationToken ct = default) =>
        await SendAsync<HistoricProcess[]>(HttpMethod.Get, $"history/process-instance?sortBy=startTime&sortOrder=desc&{Page(firstResult, maxResults)}", null, ct);

    public async Task<IReadOnlyList<ProcessInstance>> GetChildInstancesAsync(string parentId, int firstResult = 0, int maxResults = 100, CancellationToken ct = default) =>
        await SendAsync<ProcessInstance[]>(HttpMethod.Get, $"process-instance?superProcessInstance={E(parentId)}&{Page(firstResult, maxResults)}", null, ct);
}
