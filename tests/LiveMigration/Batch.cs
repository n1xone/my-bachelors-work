using System.Text;
using Camunda.Api;
using Bakalarka.ConsoleApp;

internal static class Batch
{
    public static async Task Run(ICamundaClient api)
    {
        var key = "migration_batch_" + Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Path.GetTempPath(), key);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "model.bpmn");
        var instances = new List<string>();
        var deployments = new List<string>();
        string Xml(string name) => $"""
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:c="http://camunda.org/schema/1.0/bpmn" targetNamespace="test">
        <process id="{key}" name="{name}" isExecutable="true" c:historyTimeToLive="1"><startEvent id="start"/><sequenceFlow id="a" sourceRef="start" targetRef="waiting"/><userTask id="waiting"/></process></definitions>
        """;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS: " + label); }
        async Task<ProcessInstance> Start(string name, string? tenant = null)
        {
            var deployment = await api.DeployResourcesAsync(key, new Dictionary<string, byte[]> { ["model.bpmn"] = Encoding.UTF8.GetBytes(Xml(name)) }, tenant);
            deployments.Add(deployment.Id);
            var definition = (await Pagination.ReadAllAsync((o, n) => api.GetDefinitionsAsync(o, n))).Single(d => d.DeploymentId == deployment.Id);
            var instance = await api.StartByDefinitionAsync(definition.Id);
            instances.Add(instance.Id);
            return instance;
        }
        try
        {
            var tenantInstance = await Start("tenant", "matrix-tenant");
            var first = await Start("first");
            var second = await Start("second");
            await api.SetSuspendedAsync(second.Id, true);
            var console = new InteractiveConsole(api, new Terminal(new StringReader(""), new StringWriter()));
            await File.WriteAllTextAsync(path, "<invalid/>");
            try { await console.ApplySavedModelAsync(path, CancellationToken.None); throw new Exception("Invalid deployment accepted"); }
            catch (CamundaException) { }
            Check((await api.GetInstanceAsync(first.Id)).DefinitionId == first.DefinitionId, "Invalid BPMN leaves current version unchanged");
            await File.WriteAllTextAsync(path, Xml("third"));
            await console.ApplySavedModelAsync(path, CancellationToken.None);
            var a = await api.GetInstanceAsync(first.Id);
            var b = await api.GetInstanceAsync(second.Id);
            Check(a.DefinitionId == b.DefinitionId && a.DefinitionId != first.DefinitionId && b.DefinitionId != second.DefinitionId, "Multiple instances on different versions migrate together");
            Check(!a.Suspended && b.Suspended, "Mixed active/suspended batch preserves state");
            Check((await api.GetInstanceAsync(tenantInstance.Id)).DefinitionId == tenantInstance.DefinitionId, "Same key in another tenant is untouched");
        }
        finally
        {
            foreach (var id in instances) await api.CancelAsync(id, "batch test cleanup");
            foreach (var definition in await Pagination.ReadAllAsync((o, n) => api.GetDefinitionsAsync(o, n)))
                if (definition.Key == key && !deployments.Contains(definition.DeploymentId)) deployments.Add(definition.DeploymentId);
            foreach (var deployment in deployments) await api.DeleteDeploymentAsync(deployment);
            Directory.Delete(folder, true);
        }
    }
}
