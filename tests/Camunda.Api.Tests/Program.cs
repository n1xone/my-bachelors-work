using System.Net;
using System.Text;
using System.Text.Json;
using Camunda.Api;
using Bakalarka.ConsoleApp;

var count = 0;
void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception("FAIL: " + message);
    count++;
    Console.WriteLine("PASS: " + message);
}

async Task Throws<T>(Func<Task> operation, string message) where T : Exception
{
    try
    {
        await operation();
    }
    catch (T) { Check(true, message); return; }
    throw new Exception("FAIL: " + message);
}

var handler = new Stub();
using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test/engine-rest/") };
ICamundaClient api = new CamundaClient(http);
handler.Response = """{"id":"p1","definitionId":"arbitrary:1:1","businessKey":"business","ended":false,"suspended":false}""";
await api.StartAsync("arbitrary key +&", new Dictionary<string, Variable>
{
    ["flag"] = Variable.Boolean(true),
    ["count"] = Variable.Integer(7),
    ["nothing"] = Variable.Null(),
    ["text"] = Variable.String("a<&\""),
    ["large"] = Variable.Long(9007199254740993)
}, "business");
Check(handler.Uri!.AbsolutePath == "/engine-rest/process-definition/key/arbitrary%20key%20%2B%26/start", "arbitrary process key is escaped");
using (var body = JsonDocument.Parse(handler.Body!))
{
    var variables = body.RootElement.GetProperty("variables");
    Check(variables.GetProperty("flag").GetProperty("value").GetBoolean(), "Boolean is JSON boolean");
    Check(variables.GetProperty("count").GetProperty("value").GetInt32() == 7, "Integer is JSON number");
    Check(variables.GetProperty("nothing").GetProperty("value").ValueKind == JsonValueKind.Null, "explicit null value survives serialization");
    Check(variables.GetProperty("large").GetProperty("value").GetInt64() == 9007199254740993, "Long retains precision");
}
handler.Response = """{"object":{"type":"Object","value":"{\"x\":1}","valueInfo":{"objectTypeName":"java.util.HashMap","serializationDataFormat":"application/json"}}}""";
var values = await api.GetVariablesAsync("p1");
Check(handler.Uri!.Query.Contains("deserializeValues=false"), "server-side object deserialization disabled");
Check(values["object"].ValueInfo!["serializationDataFormat"].GetString() == "application/json", "object metadata preserved");
handler.Status = HttpStatusCode.NoContent;
await api.CompleteTaskAsync("task-1", values);
Check(handler.Method == HttpMethod.Post && handler.Uri!.AbsolutePath.EndsWith("task/task-1/complete"), "204 completion accepted");
await api.CorrelateMessageAsync("any_message", "p1");
using (var body = JsonDocument.Parse(handler.Body!))
    Check(body.RootElement.GetProperty("processInstanceId").GetString() == "p1", "message correlation restricted to instance");
await api.SetVariablesAsync("p1", values);
Check(handler.Body!.Contains("modifications"), "variable update uses modifications envelope");
handler.Status = HttpStatusCode.OK;
handler.Response = "[]";
await api.FetchAndLockAsync("worker-A", [new("topic arbitrary", 12345, "p1")], 3);
using (var body = JsonDocument.Parse(handler.Body!))
{
    var topic = body.RootElement.GetProperty("topics")[0];
    Check(topic.GetProperty("processInstanceId").GetString() == "p1" && topic.GetProperty("lockDuration").GetInt64() == 12345,
        "fetch locks only requested instance with requested duration");
}

await api.GetDefinitionsAsync(100, 25);
Check(handler.Uri!.Query.Contains("firstResult=100&maxResults=25"), "pagination reaches engine");
await api.DeleteDeploymentAsync("deployment /1");
Check(handler.Method == HttpMethod.Delete && handler.Uri!.AbsolutePath.EndsWith("/deployment/deployment%20%2F1") && handler.Uri.Query == "?cascade=false",
    "deployment deletion is escaped and non-cascading");
await Throws<ArgumentOutOfRangeException>(() => api.GetDefinitionsAsync(-1), "negative offset rejected");
await Throws<ArgumentException>(() => api.FetchAndLockAsync("w", []), "empty topic list rejected");
handler.Status = HttpStatusCode.InternalServerError;
handler.Response = """{"type":"ProcessEngineException","message":"bad expression"}""";
var before = handler.Calls;

try
{
    await api.StartAsync("bad");
    throw new Exception("Missing engine error");
}
catch (CamundaException ex)
{
    Check(ex.StatusCode == HttpStatusCode.InternalServerError && ex.ResponseBody.Contains("bad expression"), "engine error status and details preserved");
}
Check(handler.Calls == before + 1, "mutation not automatically retried");
handler.Status = HttpStatusCode.NotFound;
await Throws<CamundaException>(() => api.GetInstanceAsync("missing"), "404 not misreported as completed process");
handler.Status = HttpStatusCode.OK;
handler.Response = """{"id":"d1","name":"test","deployedProcessDefinitions":{}}""";
using var bpmn = new MemoryStream(Encoding.UTF8.GetBytes("<definitions />"));
await api.DeployAsync("test", bpmn, "test.bpmn");
Check(handler.ContentType!.StartsWith("multipart/form-data") && handler.Body!.Contains("test.bpmn") && handler.Body.Contains("<definitions />"), "deployment is multipart with original BPMN");
Check(handler.Body.Contains("enable-duplicate-filtering") && handler.Body.Contains("true"), "deployment enables duplicate filtering");
Check(bpmn.CanRead, "deployment preserves caller stream ownership");
using var cts = new CancellationTokenSource();
cts.Cancel();
await Throws<OperationCanceledException>(() => api.GetDefinitionsAsync(ct: cts.Token), "cancellation propagated");
using var invalidHttp = new HttpClient { BaseAddress = new Uri("http://engine.test/engine-rest") };

try
{
    _ = new CamundaClient(invalidHttp);
    throw new Exception("Invalid URL accepted");
}
catch (ArgumentException) { Check(true, "missing trailing slash rejected"); }
handler.Status = HttpStatusCode.NoContent;
await api.ClaimTaskAsync("t1", "demo");
Check(handler.Uri!.AbsolutePath.EndsWith("/task/t1/claim") && handler.Body!.Contains("demo"), "claim uses selected user");
await api.UnclaimTaskAsync("t1");
Check(handler.Uri!.AbsolutePath.EndsWith("/task/t1/unclaim"), "unclaim endpoint");
await api.SubmitTaskFormAsync("t1", new Dictionary<string, Variable> { ["approved"] = Variable.Boolean(false) });
Check(handler.Uri!.AbsolutePath.EndsWith("/submit-form") && handler.Body!.Contains("false"), "form submission retains false and engine validation");
await api.LockExternalTaskAsync("exact-task", "worker", 30000);
Check(handler.Uri!.AbsolutePath.EndsWith("external-task/exact-task/lock"), "manual operation locks exact selected external task");
await api.SetExternalTaskRetriesAsync("t1", 3);
Check(handler.Method == HttpMethod.Put && handler.Uri!.AbsolutePath.EndsWith("/external-task/t1/retries"), "external retries endpoint");
await api.SetJobRetriesAsync("j1", 3);
Check(handler.Method == HttpMethod.Put && handler.Uri!.AbsolutePath.EndsWith("/job/j1/retries"), "job retries endpoint");
await api.SendSignalAsync("ready", "e1");
Check(handler.Body!.Contains("executionId") && handler.Body.Contains("e1"), "signal scoped to execution, not broadcast");
handler.Status = HttpStatusCode.OK;
handler.Response = "[]";
await api.GetChildInstancesAsync("parent");
Check(handler.Uri!.Query.Contains("superProcessInstance=parent"), "child processes query uses parent filter");
handler.Response = """[{"id":"execution-1","processInstanceId":"p1","ended":false,"activityId":"wait_for_message"}]""";
var executions = await api.GetExecutionsAsync("p1");
Check(executions.Single().ActivityId == "wait_for_message", "execution activity ID is available for receive-task selection");
handler.Response = """{"id":"p1","definitionId":"model:2:1"}""";
await api.StartByDefinitionAsync("model:2:1");
Check(handler.Uri!.AbsolutePath.Contains("process-definition/model%3A2%3A1/start"), "start selected exact version");
Check(VariableEditor.Parse("ne", "Boolean").Value.GetBoolean() == false, "Czech boolean input");
Check(VariableEditor.Parse("42", "Integer").Value.GetInt32() == 42, "typed integer input");
Check(VariableEditor.Parse("2000-01-01", "Date").Value.GetString() == "2000-01-01T00:00:00.000+0000", "Camunda date wire format");
Check(VariableEditor.Parse("27.07.2006", "Date").Value.GetString() == "2006-07-27T00:00:00.000+0000", "Czech date input");
Check(VariableEditor.Parse("7.7.2006", "Date").Value.GetString() == "2006-07-07T00:00:00.000+0000", "unpadded Czech date input");
Check(VariableEditor.Parse("2000-01-01T00:00:00.000+0000", "Date").Value.GetString()!.EndsWith("+0000"), "existing date can be edited");
var form = VariableEditor.ReadBpmnFields("""<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:c="http://camunda.org/schema/1.0/bpmn"><process id="p"><userTask id="t"><extensionElements><c:formData><c:formField id="approved" label="Approve" type="boolean" defaultValue="true" /></c:formData></extensionElements></userTask></process></definitions>""", "t");
Check(form.Single().Type == "boolean" && form.Single().Default == "true", "BPMN form metadata read without model-specific code");
var terminal = new Terminal(new StringReader("wrong\n2\n"), new StringWriter());
Check(await terminal.Menu(["A", "B"], default) == 2, "invalid menu input does not leave the menu");
var fileVariable = Variable.File([1, 2, 3], "invoice.pdf", "application/pdf");
Check(fileVariable.Type == "File" && fileVariable.Value.GetString() == "AQID" && fileVariable.ValueInfo!["filename"].GetString() == "invoice.pdf", "file variable retains base64 content and filename metadata");
Check(VariableEditor.Display(fileVariable) == "Soubor: invoice.pdf", "file display uses filename");
Check(VariableEditor.Display(fileVariable with { ValueInfo = new() }) == "Soubor: —", "file display accepts missing filename");
Check(VariableEditor.Display(fileVariable with { ValueInfo = null }) == "Soubor: —", "file display accepts missing metadata");
Check(VariableEditor.Parse("{\"x\":1}", "Json").Value.GetString() == "{\"x\":1}", "JSON variable retains serialized content");
await Throws<JsonException>(() => Task.FromResult(VariableEditor.Parse("{broken", "Json")), "invalid JSON rejected");

var tempRoot = Path.Combine(Path.GetTempPath(), "bpmn-editor-discovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(tempRoot, "models"));
Directory.CreateDirectory(Path.Combine(tempRoot, ".sandbox-models"));
Directory.CreateDirectory(Path.Combine(tempRoot, "IntegratedVisualizer", "Utility", "diagrams", "tests"));
Directory.CreateDirectory(Path.Combine(tempRoot, ".sandbox-models", "IntegratedVisualizer", "Utility", "diagrams"));
var sourceModel = Path.Combine(tempRoot, "models", "alpha.bpmn");
var sandboxModel = Path.Combine(tempRoot, ".sandbox-models", "beta.bpmn");
var legacyCopy = Path.Combine(tempRoot, ".sandbox-models", "alpha.bpmn");
var userDiagram = Path.Combine(tempRoot, "IntegratedVisualizer", "Utility", "diagrams", "registration.bpmn");
var userDiagramCopy = Path.Combine(tempRoot, ".sandbox-models", "IntegratedVisualizer", "Utility", "diagrams", "registration.bpmn");
var fixtureDiagram = Path.Combine(tempRoot, "IntegratedVisualizer", "Utility", "diagrams", "tests", "fixture.bpmn");
await File.WriteAllTextAsync(sourceModel, "<definitions />");
await File.WriteAllTextAsync(sandboxModel, "<definitions />");
await File.WriteAllTextAsync(legacyCopy, "<definitions />");
await File.WriteAllTextAsync(userDiagram, "<definitions />");
await File.WriteAllTextAsync(userDiagramCopy, "<definitions />");
await File.WriteAllTextAsync(fixtureDiagram, "<definitions />");
var discovered = BpmnEditorHost.DiscoverModelFiles(tempRoot);
Check(discovered.Contains(sourceModel) && !discovered.Contains(legacyCopy) && discovered.Contains(sandboxModel), "discovery collapses unchanged legacy copies but retains sandbox-only drafts");
Check(discovered.Contains(userDiagram) && !discovered.Contains(userDiagramCopy) && !discovered.Contains(fixtureDiagram), "saved user diagrams appear once while nested visualizer fixtures stay hidden");
var editorCloseSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using (BpmnEditorHost.RouteCtrlCToEditor(editorCloseSignal))
    Check(BpmnEditorHost.TryHandleEditorCtrlC() && editorCloseSignal.Task.IsCompleted, "Ctrl+C while editor is open returns through its close signal");
Check(!BpmnEditorHost.TryHandleEditorCtrlC(), "Ctrl+C routing returns to application shutdown outside the editor");
var sandboxPath = BpmnEditorHost.GetSandboxCopyPath(sourceModel, tempRoot);
Check(Path.GetDirectoryName(sandboxPath) == Path.Combine(tempRoot, ".sandbox-models", "models") && Path.GetFileName(sandboxPath) == "alpha.bpmn", "source models are copied into an isolated sandbox subtree");
var userSandboxPath = BpmnEditorHost.GetSandboxCopyPath(userDiagram, tempRoot);
Check(userSandboxPath.StartsWith(Path.Combine(tempRoot, ".sandbox-models", "IntegratedVisualizer"), StringComparison.Ordinal), "user diagrams keep their relative path in the sandbox");
var editableDiagram = BpmnEditorHost.ResolveEditableModelPath("IntegratedVisualizer/Utility/diagrams/registration.bpmn", tempRoot);
await File.WriteAllTextAsync(editableDiagram, "<definitions id=\"edited\" />");
Check(await File.ReadAllTextAsync(userDiagram) == "<definitions />", "editing a discovered diagram never changes its project source");
Directory.Delete(tempRoot, recursive: true);

var source = Enumerable.Range(0, 205).ToArray();
var offsets = new List<int>();
var collected = await Pagination.ReadAllAsync<int>((offset, limit) =>
{
    offsets.Add(offset);

    return Task.FromResult<IReadOnlyList<int>>(source.Skip(offset).Take(limit).ToArray());
});
Check(collected.SequenceEqual(source) && offsets.SequenceEqual([0, 100, 200]), "pagination reads full and partial pages without losing items");
var empty = await Pagination.ReadAllAsync<int>((offset, limit) => Task.FromResult<IReadOnlyList<int>>([]));
Check(empty.Count == 0, "pagination accepts empty result");
await Throws<HttpRequestException>(() => Pagination.ReadAllAsync<int>((offset, limit) => throw new HttpRequestException("offline")), "pagination propagates request failure");
var importRoot = Path.Combine(Path.GetTempPath(), "sandbox-import-" + Guid.NewGuid().ToString("N"));
try
{
    using var sourceHandler = new ImportStub();
    using var sourceHttp = new HttpClient(sourceHandler) { BaseAddress = new Uri("http://source.test/engine-rest/") };
    var sourceClient = new CamundaClient(sourceHttp);
    var definition = new ProcessDefinition("../../prace:14", "prace_1", "BAKALARKA", 14, "deployment", false, "tenant-a", "folder/registration.bpmn");
    var draft = await SandboxWorkspace.ImportAsync(sourceClient, definition, importRoot);
    Check(Path.GetFullPath(draft).StartsWith(Path.GetFullPath(importRoot) + Path.DirectorySeparatorChar), "untrusted definition IDs stay inside workspace");
    Check(File.ReadAllText(draft).Contains("prace_1"), "registration imported from main engine");
    Check(sourceHandler.Methods.All(m => m == HttpMethod.Get), "source import performs GET requests only");
    await File.WriteAllTextAsync(draft, "<definitions>edited</definitions>");
    var calls = sourceHandler.Methods.Count;
    Check(await SandboxWorkspace.ImportAsync(sourceClient, definition, importRoot) == draft && sourceHandler.Methods.Count == calls && File.ReadAllText(draft).Contains("edited"), "reimport preserves edited draft without network requests");
    handler.Response = "{\"id\":\"sandbox-deployment\",\"name\":\"sandbox\"}";
    await SandboxWorkspace.DeployAsync(api, draft, default);
    Check(handler.Body!.Contains("edited") && handler.Body.Contains("decision-data") && handler.Body.Contains("tenant-a") && handler.Body.Contains("folder/registration.bpmn"), "sandbox deployment retains edits, dependency resources, tenant and original resource names");
    sourceHandler.Definitions = [definition with { Id = "old", Version = 13 }, definition,
        definition with { Id = "second-tenant", TenantId = "tenant-b" }];
    var sync = await SandboxWorkspace.SynchronizeAsync(sourceClient, importRoot);
    Check(sync.Available == 2 && sync.Imported == 2 && sync.Errors.Count == 0, "catalogue imports latest version for each process and tenant");
    Check(File.ReadAllText(draft).Contains("edited"), "catalogue refresh preserves local edits");
    sourceHandler.Definitions = [definition with { Id = "new", Version = 15 }, definition,
        definition with { Id = "second-tenant", TenantId = "tenant-b" }];
    await SandboxWorkspace.SynchronizeAsync(sourceClient, importRoot);
    var catalogue = BpmnEditorHost.DiscoverModelFiles(importRoot);
    Check(catalogue.Count == 2 && !catalogue.Contains(draft) && File.Exists(draft), "catalogue shows one latest draft per tenant and retains older edits on disk");
    Check(sourceHandler.Methods.All(m => m == HttpMethod.Get), "catalogue synchronization never mutates source engine");
    var previousRoot = Directory.GetCurrentDirectory();
    try
    {
        Directory.SetCurrentDirectory(importRoot);
        sourceHandler.Offline = true;
        var output = new StringWriter();
        var offlineTerminal = new Terminal(new StringReader("1\n1\n0\n0\n"), output);
        var sandboxCalls = handler.Calls;
        await new InteractiveConsole(api, offlineTerminal, true, sourceClient).RunAsync(CamundaConnections.SandboxAddress, default);
        Check(output.ToString().Contains("uloženými kopiemi") && output.ToString().Contains("BAKALARKA"), "offline sandbox menu still opens cached main processes");
        Check(handler.Calls == sandboxCalls, "browsing cached models does not deploy or start processes");
        sourceHandler.Offline = false;
    }
    finally { Directory.SetCurrentDirectory(previousRoot); }
    var completeImports = Directory.GetDirectories(Path.Combine(importRoot, ".sandbox-models", "camunda")).Length;
    sourceHandler.FailResource = true;
    await Throws<CamundaException>(() => SandboxWorkspace.ImportAsync(sourceClient, definition with { Id = "another" }, importRoot), "failed resource import propagates error");
    Check(Directory.GetDirectories(Path.Combine(importRoot, ".sandbox-models", "camunda")).Length == completeImports, "failed import leaves no partial draft");
    await Throws<InvalidOperationException>(() => { CamundaConnections.EnsureSeparate("http://localhost:18080/engine-rest/"); return Task.CompletedTask; }, "main URL cannot alias local sandbox");
    CamundaConnections.EnsureSeparate("http://localhost:8080/engine-rest/");
}
finally { if (Directory.Exists(importRoot)) Directory.Delete(importRoot, true); }
Console.WriteLine($"{count} checks passed.");

sealed class Stub : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Response { get; set; } = "{}";

    public Uri? Uri
    {
        get; private set;
    }

    public HttpMethod? Method
    {
        get; private set;
    }

    public string? Body
    {
        get; private set;
    }

    public string? ContentType
    {
        get; private set;
    }

    public int Calls
    {
        get; private set;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        Uri = request.RequestUri;
        Method = request.Method;
        ContentType = request.Content?.Headers.ContentType?.ToString();
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

        return new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
    }
}

sealed class ImportStub : HttpMessageHandler
{
    public List<HttpMethod> Methods { get; } = [];
    public bool FailResource { get; set; }
    public bool Offline { get; set; }
    public ProcessDefinition[] Definitions { get; set; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Methods.Add(request.Method);
        if (Offline) throw new HttpRequestException("source offline");
        var path = request.RequestUri!.AbsolutePath;
        var body = path.EndsWith("/process-definition") ? JsonSerializer.Serialize(Definitions, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : path.EndsWith("/xml") ? "{\"bpmn20Xml\":\"<definitions />\"}"
            : path.EndsWith("/resources") ? "[{\"id\":\"bpmn\",\"name\":\"folder/registration.bpmn\"},{\"id\":\"dmn\",\"name\":\"../decision.dmn\"}]"
            : path.Contains("/bpmn/") ? "<definitions id=\"prace_1\" />" : "decision-data";
        return Task.FromResult(new HttpResponseMessage(FailResource && path.EndsWith("/data") ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
