using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Camunda.Api;

namespace Bakalarka.ConsoleApp;

/// <summary>Imports deployment resources into an independent editable workspace, never writing to the source engine.</summary>
internal static class SandboxWorkspace
{
    internal sealed record ResourceFile(string Name, string File);
    internal sealed record Manifest(ProcessDefinition Source, ResourceFile[] Resources);

    internal sealed record SyncResult(int Available, int Imported, IReadOnlyList<string> Errors);

    public static async Task<SyncResult> SynchronizeAsync(ICamundaClient source, string root, CancellationToken ct = default)
    {
        var definitions = await Pagination.ReadAllAsync((offset, limit) => source.GetDefinitionsAsync(offset, limit, ct));
        var latest = definitions.GroupBy(d => (d.Key, d.TenantId))
            .Select(group => group.MaxBy(d => d.Version)!).ToArray();
        var imported = 0;
        var errors = new List<string>();
        foreach (var definition in latest)
        {
            try
            {
                await ImportAsync(source, definition, root, ct);
                imported++;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or System.Xml.XmlException or InvalidOperationException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                errors.Add($"{definition.Key}: {ex.Message}");
            }
        }
        return new(latest.Length, imported, errors);
    }

    // Older source snapshots stay on disk, but do not multiply rows in the working catalogue.
    public static IReadOnlyList<string> LatestDrafts(IEnumerable<string> paths) => paths
        .Select(path => (Path: path, Source: ReadManifest(path)?.Source))
        .GroupBy(item => item.Source is null ? (item.Path, (string?)null) : (item.Source.Key, item.Source.TenantId))
        .Select(group => group.OrderByDescending(item => item.Source?.Version ?? 0).First().Path)
        .OrderBy(Label, StringComparer.OrdinalIgnoreCase).ToArray();

    public static async Task<string> ImportAsync(ICamundaClient source, ProcessDefinition definition,
        string root, CancellationToken ct = default)
    {
        // Server identifiers and resource names are never used as filesystem paths.
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition.Id)));
        var parent = Path.Combine(root, ".sandbox-models", "camunda");
        var folder = Path.Combine(parent, id);
        var path = Path.Combine(folder, "model.bpmn");
        if (File.Exists(path)) return path;
        var xml = (await source.GetBpmnAsync(definition.Id, ct)).Bpmn20Xml;
        XDocument.Parse(xml);
        var resources = await source.GetDeploymentResourcesAsync(definition.DeploymentId, ct);
        if (definition.Resource is null || !resources.Any(r => r.Name == definition.Resource))
            throw new InvalidOperationException("Source deployment does not identify the BPMN resource; import canceled.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var files = new List<ResourceFile>();
            foreach (var resource in resources)
            {
                var file = resource.Name == definition.Resource ? "model.bpmn" : $"resource-{files.Count}.bin";
                var bytes = await source.GetDeploymentResourceAsync(definition.DeploymentId, resource.Id, ct);
                await File.WriteAllBytesAsync(Path.Combine(temporary, file), bytes, ct);
                files.Add(new(resource.Name, file));
            }
            await File.WriteAllTextAsync(Path.Combine(temporary, "source.json"),
                JsonSerializer.Serialize(new Manifest(definition, files.ToArray())), ct);
            Directory.Move(temporary, folder);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        return path;
    }

    public static Manifest? ReadManifest(string path)
    {
        var metadata = Path.Combine(Path.GetDirectoryName(path)!, "source.json");
        return File.Exists(metadata) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(metadata)) : null;
    }

    public static string Label(string path)
    {
        var manifest = ReadManifest(path);
        return manifest is null ? BpmnEditorHost.RelativePathForDisplay(path, Directory.GetCurrentDirectory())
            : $"{manifest.Source.Name ?? manifest.Source.Key} | {manifest.Source.Key} | source v{manifest.Source.Version} | tenant={manifest.Source.TenantId ?? "—"}";
    }

    public static async Task<Deployment> DeployAsync(ICamundaClient target, string path, CancellationToken ct)
    {
        var manifest = ReadManifest(path);
        if (manifest is null)
        {
            await using var file = File.OpenRead(path);
            return await target.DeployAsync(Path.GetFileNameWithoutExtension(path), file, Path.GetFileName(path), ct);
        }
        var resources = new Dictionary<string, byte[]>();
        foreach (var resource in manifest.Resources)
        {
            if (resource.File != Path.GetFileName(resource.File))
                throw new InvalidOperationException("Invalid workspace resource path.");
            resources.Add(resource.Name, await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, resource.File), ct));
        }
        return await target.DeployResourcesAsync("sandbox-" + manifest.Source.Key, resources, manifest.Source.TenantId, ct);
    }
}
