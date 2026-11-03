using System.Net;
using System.Text.Json;

namespace Camunda.Api;

/// <summary>Typovana promenna Camundy. Serializovane objekty zachovavaji metadata valueInfo.</summary>
public sealed record Variable(
    JsonElement Value,
    string Type,
    Dictionary<string, JsonElement>? ValueInfo = null)
{
    public static Variable String(string value) => new(JsonSerializer.SerializeToElement(value), "String");

    public static Variable Boolean(bool value) => new(JsonSerializer.SerializeToElement(value), "Boolean");

    public static Variable Integer(int value) => new(JsonSerializer.SerializeToElement(value), "Integer");

    public static Variable Long(long value) => new(JsonSerializer.SerializeToElement(value), "Long");

    public static Variable Double(double value) => new(JsonSerializer.SerializeToElement(value), "Double");

    public static Variable File(
        byte[] content,
        string filename,
        string mimeType = "application/octet-stream") =>
        new(JsonSerializer.SerializeToElement(Convert.ToBase64String(content)), "File", new()
        {
            ["filename"] = JsonSerializer.SerializeToElement(filename),
            ["mimeType"] = JsonSerializer.SerializeToElement(mimeType)
        });

    public static Variable Null() => new(JsonSerializer.SerializeToElement<object?>(null), "Null");
}

public sealed record ProcessDefinition(
    string Id,
    string Key,
    string? Name,
    int Version,
    string DeploymentId,
    bool Suspended,
    string? TenantId = null);

public sealed record Deployment(
    string Id,
    string Name,
    Dictionary<string, ProcessDefinition>? DeployedProcessDefinitions);

public sealed record ProcessInstance(
    string Id,
    string DefinitionId,
    string? BusinessKey,
    bool Ended,
    bool Suspended);

public sealed record UserTask(
    string Id,
    string? Name,
    string TaskDefinitionKey,
    string ProcessInstanceId,
    string? Assignee);

public sealed record ExternalTask(
    string Id,
    string TopicName,
    string ProcessInstanceId,
    string ActivityId,
    string? WorkerId,
    int? Retries,
    string? ErrorMessage,
    Dictionary<string, Variable>? Variables);

public sealed record TopicSubscription(
    string TopicName,
    long LockDuration = 30000,
    string? ProcessInstanceId = null);

public sealed record ActivityInstance(
    string Id,
    string ActivityId,
    string? ActivityName,
    string ActivityType,
    ActivityInstance[] ChildActivityInstances,
    TransitionInstance[] ChildTransitionInstances);

public sealed record TransitionInstance(
    string Id,
    string ActivityId,
    string? ActivityName,
    string ActivityType,
    string ExecutionId);

public sealed record Incident(
    string Id,
    string IncidentType,
    string? IncidentMessage,
    string ActivityId,
    string ProcessInstanceId);

public sealed record EventSubscription(
    string Id,
    string EventType,
    string? EventName,
    string ActivityId,
    string ExecutionId);

// Casove udaje Camundy pouzivaji posun +0000; retezce zachovavaji puvodni format protokolu.
public sealed record HistoricProcess(
    string Id,
    string ProcessDefinitionId,
    string? BusinessKey,
    string State,
    string StartTime,
    string? EndTime,
    string? DeleteReason,
    string? SuperProcessInstanceId = null);

public sealed record HistoricActivity(
    string Id,
    string ActivityId,
    string? ActivityName,
    string ActivityType,
    string StartTime,
    string? EndTime,
    bool Canceled,
    bool CompleteScope);

public sealed record BpmnXml(string Id, string Bpmn20Xml);

/// <summary>Zachovava stav enginu a telo chyby bez opakovani zapisu, ktery uz mohl probehnout.</summary>
public sealed class CamundaException(HttpStatusCode statusCode, string responseBody)
    : HttpRequestException($"Camunda returned {(int)statusCode}: {responseBody}", null, statusCode)
{
    public string ResponseBody { get; } = responseBody;
}

public sealed record Job(
    string Id,
    string? ExecutionId,
    string? ProcessInstanceId,
    int Retries,
    bool Suspended,
    string? ExceptionMessage,
    string? DueDate);

public sealed record Execution(
    string Id,
    string? ProcessInstanceId,
    bool Ended,
    string? ActivityId = null);
