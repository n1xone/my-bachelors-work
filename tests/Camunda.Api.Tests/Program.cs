using System.Net;
using System.Text;
using System.Text.Json;
using Camunda.Api;
using Bakalarka.ConsoleApp;

var count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    count++;
    Console.WriteLine("PASS: " + message);
}
async Task Throws<T>(Func<Task> operation, string message) where T : Exception
{
    try { await operation(); }
    catch (T) { Check(true, message); return; }
    throw new Exception("FAIL: " + message);
}
var handler = new Stub();
using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test/engine-rest/") };
ICamundaClient api = new CamundaClient(http);
handler.Response = """{"id":"p1","definitionId":"arbitrary:1:1","businessKey":"business","ended":false,"suspended":false}""";
await api.StartAsync("arbitrary key +&", new Dictionary<string, Variable>
{
    ["flag"] = Variable.Boolean(true), ["count"] = Variable.Integer(7),
    ["nothing"] = Variable.Null(), ["text"] = Variable.String("a<&\""),
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
try { await api.StartAsync("bad"); throw new Exception("Missing engine error"); }
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
Check(bpmn.CanRead, "deployment preserves caller stream ownership");
using var cts = new CancellationTokenSource();
cts.Cancel();
await Throws<OperationCanceledException>(() => api.GetDefinitionsAsync(ct: cts.Token), "cancellation propagated");
using var invalidHttp = new HttpClient { BaseAddress = new Uri("http://engine.test/engine-rest") };
try { _ = new CamundaClient(invalidHttp); throw new Exception("Invalid URL accepted"); }
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
Console.WriteLine($"{count} checks passed.");

sealed class Stub : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Response { get; set; } = "{}";
    public Uri? Uri { get; private set; }
    public HttpMethod? Method { get; private set; }
    public string? Body { get; private set; }
    public string? ContentType { get; private set; }
    public int Calls { get; private set; }
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
