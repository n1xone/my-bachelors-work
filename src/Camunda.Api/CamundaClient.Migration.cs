using System.Text.Json;

namespace Camunda.Api;

public sealed partial class CamundaClient
{
    public Task<JsonElement> ValidateMigrationAsync(JsonElement plan, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "migration/validate", plan, ct);
    public Task DeleteVariablesAsync(string instanceId, string[] names, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"process-instance/{E(instanceId)}/variables", new { deletions = names }, ct);
    public Task<JsonElement> GenerateMigrationAsync(string source, string target, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "migration/generate", new { sourceProcessDefinitionId = source, targetProcessDefinitionId = target, updateEventTriggers = true }, ct);

    public Task MigrateAsync(JsonElement plan, string instanceId, IReadOnlyDictionary<string, Variable> variables, CancellationToken ct = default)
    {
        var enriched = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(plan.GetRawText())!;
        enriched["variables"] = JsonSerializer.SerializeToElement(variables, Json);
        return SendAsync(HttpMethod.Post, "migration/execute", new { migrationPlan = enriched, processInstanceIds = new[] { instanceId }, skipCustomListeners = false, skipIoMappings = false }, ct);
    }
}
