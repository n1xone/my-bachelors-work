using System.Text;
using Camunda.Api;
using Bakalarka.ConsoleApp;

using var http = CamundaConnections.Create(CamundaConnections.MainAddress);
var api = new CamundaClient(http);
var key = "live_update_test_" + Guid.NewGuid().ToString("N");
var folder = Path.Combine(Path.GetTempPath(), key);
Directory.CreateDirectory(folder);
var path = Path.Combine(folder, "model.bpmn");
var deployments = new List<string>();
string? instanceId = null;
string Model(bool updated) => $$"""
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:c="http://camunda.org/schema/1.0/bpmn" targetNamespace="test">
 <process id="{{key}}" isExecutable="true" c:historyTimeToLive="1">
  <startEvent id="start"/><sequenceFlow id="a" sourceRef="start" targetRef="input"/>
  <userTask id="input"><extensionElements><c:formData><c:formField id="{{(updated ? "added" : "removed")}}" type="string"><c:validation><c:constraint name="required" config="true"/></c:validation></c:formField></c:formData></extensionElements></userTask>
  <sequenceFlow id="b" sourceRef="input" targetRef="waiting"/><userTask id="waiting"/>
  <sequenceFlow id="c" sourceRef="waiting" targetRef="{{(updated ? "newStep" : "oldStep")}}"/>
  <userTask id="{{(updated ? "newStep" : "oldStep")}}"/>
  <sequenceFlow id="d" sourceRef="{{(updated ? "newStep" : "oldStep")}}" targetRef="end"/><endEvent id="end"/>
 </process>
</definitions>
""";
void Assert(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS: " + label); }
try
{
    await File.WriteAllTextAsync(path, Model(false));
    using (var file = File.OpenRead(path)) deployments.Add((await api.DeployAsync(key, file, "model.bpmn")).Id);
    var instance = await api.StartAsync(key, new Dictionary<string, Variable> { ["removed"] = Variable.String("old"), ["unrelated"] = Variable.String("keep") });
    instanceId = instance.Id;
    await api.CompleteTaskAsync((await api.GetTasksAsync(instanceId)).Single().Id);
    var waiting = (await api.GetTasksAsync(instanceId)).Single();
    await File.WriteAllTextAsync(path, Model(true));
    var console = new InteractiveConsole(api, new Terminal(new StringReader("new value\n\n"), Console.Out));
    await console.ApplySavedModelAsync(path, CancellationToken.None);
    var migrated = await api.GetInstanceAsync(instanceId);
    Assert(migrated.Id == instance.Id && migrated.DefinitionId != instance.DefinitionId, "Same instance ID; new definition");
    Assert(!migrated.Suspended, "Automatically resumed after data collection");
    Assert((await api.GetTasksAsync(instanceId)).Single().Id == waiting.Id, "Current task preserved");
    var values = await api.GetVariablesAsync(instanceId);
    Assert(values["added"].Value.GetString() == "new value" && !values.ContainsKey("removed") && values.ContainsKey("unrelated"), "Added input, removed obsolete field, preserved unrelated data");
    await api.CompleteTaskAsync(waiting.Id);
    Assert((await api.GetTasksAsync(instanceId)).Single().TaskDefinitionKey == "newStep", "Execution follows the NEW route without restart");
    await console.ApplySavedModelAsync(path, CancellationToken.None);
    Assert((await api.GetInstanceAsync(instanceId)).DefinitionId == migrated.DefinitionId, "Repeated save does not restart or remigrate");
}
finally
{
    if (instanceId is not null) await api.CancelAsync(instanceId, "live migration test cleanup");
    foreach (var definition in await api.GetDefinitionsAsync(0, 10000))
        if (definition.Key == key && !deployments.Contains(definition.DeploymentId)) deployments.Add(definition.DeploymentId);
    foreach (var deployment in deployments) await api.DeleteDeploymentAsync(deployment);
    Directory.Delete(folder, true);
}
await Matrix.Run(api);
await Batch.Run(api);
