namespace Camunda.Api;

/// <summary>Obecny pristup ke Camunda 7. ID, klice, promenne a temata urcuje volajici.</summary>
public interface ICamundaClient
{
    Task<Deployment> DeployAsync(string name, Stream bpmn, string fileName, CancellationToken ct = default);
    Task DeleteDeploymentAsync(string deploymentId, CancellationToken ct = default);
    Task<IReadOnlyList<ProcessDefinition>> GetDefinitionsAsync(int firstResult = 0, int maxResults = 100, CancellationToken ct = default);
    Task<BpmnXml> GetBpmnAsync(string definitionId, CancellationToken ct = default);
    Task<ProcessInstance> StartAsync(string key, IReadOnlyDictionary<string, Variable>? variables = null, string? businessKey = null, CancellationToken ct = default);
    Task<IReadOnlyList<ProcessInstance>> GetInstancesAsync(string? definitionKey = null, int firstResult = 0, int maxResults = 100, CancellationToken ct = default);
    Task<ProcessInstance> GetInstanceAsync(string instanceId, CancellationToken ct = default);
    Task CancelAsync(string instanceId, string reason, CancellationToken ct = default);
    Task SetSuspendedAsync(string instanceId, bool suspended, CancellationToken ct = default);
    Task<Dictionary<string, Variable>> GetVariablesAsync(string instanceId, CancellationToken ct = default);
    Task SetVariablesAsync(string instanceId, IReadOnlyDictionary<string, Variable> variables, CancellationToken ct = default);
    Task<ActivityInstance> GetActivitiesAsync(string instanceId, CancellationToken ct = default);
    Task<IReadOnlyList<UserTask>> GetTasksAsync(string instanceId, int firstResult = 0, int maxResults = 100, CancellationToken ct = default);
    Task CompleteTaskAsync(string taskId, IReadOnlyDictionary<string, Variable>? variables = null, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalTask>> GetExternalTasksAsync(string instanceId, int firstResult = 0, int maxResults = 100, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalTask>> FetchAndLockAsync(string workerId, IReadOnlyList<TopicSubscription> topics, int maxTasks = 10, CancellationToken ct = default);
    Task CompleteExternalTaskAsync(string taskId, string workerId, IReadOnlyDictionary<string, Variable>? variables = null, CancellationToken ct = default);
    Task FailExternalTaskAsync(string taskId, string workerId, string errorMessage, int retries, long retryTimeout, CancellationToken ct = default);
    Task CorrelateMessageAsync(string messageName, string instanceId, IReadOnlyDictionary<string, Variable>? variables = null, CancellationToken ct = default);
    Task<IReadOnlyList<EventSubscription>> GetSubscriptionsAsync(string instanceId, int firstResult = 0, int maxResults = 100, CancellationToken ct = default);
    Task<IReadOnlyList<Incident>> GetIncidentsAsync(string instanceId, int firstResult = 0, int maxResults = 100, CancellationToken ct = default);
    Task<HistoricProcess> GetHistoryAsync(string instanceId, CancellationToken ct = default);
    Task<IReadOnlyList<HistoricActivity>> GetActivityHistoryAsync(string instanceId, int firstResult = 0, int maxResults = 100, CancellationToken ct = default);
    Task<ProcessInstance> StartByDefinitionAsync(string definitionId, IReadOnlyDictionary<string, Variable>? variables = null, string? businessKey = null, CancellationToken ct = default);
    Task<Dictionary<string, Variable>> GetTaskFormVariablesAsync(string taskId, CancellationToken ct = default);
    Task SubmitTaskFormAsync(string taskId, IReadOnlyDictionary<string, Variable> variables, CancellationToken ct = default);
    Task ClaimTaskAsync(string taskId, string userId, CancellationToken ct = default);
    Task UnclaimTaskAsync(string taskId, CancellationToken ct = default);
    Task LockExternalTaskAsync(string taskId, string workerId, long lockDuration, CancellationToken ct = default);
    Task SetExternalTaskRetriesAsync(string taskId, int retries, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> GetJobsAsync(string instanceId, int firstResult = 0, int maxResults = 100, CancellationToken ct = default);
    Task SetJobRetriesAsync(string jobId, int retries, CancellationToken ct = default);
    Task ExecuteJobAsync(string jobId, CancellationToken ct = default);
    Task TriggerExecutionAsync(string executionId, IReadOnlyDictionary<string, Variable>? variables = null, CancellationToken ct = default);
    Task SendSignalAsync(string signalName, string executionId, IReadOnlyDictionary<string, Variable>? variables = null, CancellationToken ct = default);
    Task<IReadOnlyList<Execution>> GetExecutionsAsync(string instanceId, int firstResult = 0, int maxResults = 100, CancellationToken ct = default);
    Task<IReadOnlyList<HistoricProcess>> GetProcessHistoryAsync(int firstResult = 0, int maxResults = 100, CancellationToken ct = default);
    Task<IReadOnlyList<ProcessInstance>> GetChildInstancesAsync(string parentId, int firstResult = 0, int maxResults = 100, CancellationToken ct = default);
}
