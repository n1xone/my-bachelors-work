using System.Text.Json;
using Camunda.Api;

namespace Bakalarka.ConsoleApp;

// No variable values are persisted. The journal lets a repeated Save finish cleanup
// after a timeout, lost response, or application restart without repeating migration.
internal sealed record LiveUpdateRecovery(string Source, string Target, string[] Deletions, bool Resume)
{
    private static string PathFor(string id) => Path.Combine(Directory.GetCurrentDirectory(), ".sandbox-models", ".live-updates", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id))) + ".json");
    public void Save(string id)
    {
        var path = PathFor(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, true);
    }
    public static void Clear(string id) => File.Delete(PathFor(id));
    public static async Task RecoverAsync(ICamundaClient api, ProcessInstance instance, CancellationToken ct)
    {
        var path = PathFor(instance.Id);
        if (!File.Exists(path)) return;
        var pending = JsonSerializer.Deserialize<LiveUpdateRecovery>(await File.ReadAllTextAsync(path, ct))!;
        if (instance.DefinitionId != pending.Source && instance.DefinitionId != pending.Target)
            throw new InvalidOperationException("Instance version changed outside pending update; inspect it before continuing.");
        if (instance.DefinitionId == pending.Target && pending.Deletions.Length > 0)
            await api.DeleteVariablesAsync(instance.Id, pending.Deletions, ct);
        if (pending.Resume && instance.Suspended) await api.SetSuspendedAsync(instance.Id, false, ct);
        Clear(instance.Id);
    }
}
