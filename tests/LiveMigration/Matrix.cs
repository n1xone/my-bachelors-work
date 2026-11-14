using Camunda.Api;
using Bakalarka.ConsoleApp;

internal static class Matrix
{
    public static async Task Run(ICamundaClient api)
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS: " + label); count++; }
        string Form(string field) => $"<extensionElements><c:formData>{field}</c:formData></extensionElements>";
        string Field(string type = "string", bool required = false) => $"<c:formField id=\"value\" type=\"{type}\">{(required ? "<c:validation><c:constraint name=\"required\" config=\"true\"/></c:validation>" : "")}</c:formField>";
        async Task Scenario(string label, string oldInput, string newInput, string oldWait, string newWait, string answers = "", bool suspended = false, bool reject = false, Action<Dictionary<string, Variable>>? verify = null, string future = "", bool advance = true, string? fault = null, bool recover = false)
        {
            var key = "migration_matrix_" + Guid.NewGuid().ToString("N");
            var folder = Path.Combine(Path.GetTempPath(), key);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "model.bpmn");
            string Xml(string input, string wait, bool changed) => $"""
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:c="http://camunda.org/schema/1.0/bpmn" targetNamespace="test">
            <message id="message" name="{key}_message"/>
            <process id="{key}" isExecutable="true" c:historyTimeToLive="1">
            <startEvent id="start"/><sequenceFlow id="a" sourceRef="start" targetRef="input"/>
            <userTask id="input">{input}</userTask><sequenceFlow id="b" sourceRef="input" targetRef="waiting"/>
            {wait}<sequenceFlow id="c" sourceRef="waiting" targetRef="future"/>
            <userTask id="future" name="{(changed ? "new" : "old")}">{(changed ? future : "")}</userTask>
            <sequenceFlow id="d" sourceRef="future" targetRef="end"/><endEvent id="end"/>
            </process></definitions>
            """.Replace("targetRef=\"waiting\"", wait.Contains("id=\"replacement\"") ? "targetRef=\"replacement\"" : "targetRef=\"waiting\"").Replace("sourceRef=\"waiting\"", wait.Contains("id=\"replacement\"") ? "sourceRef=\"replacement\"" : "sourceRef=\"waiting\"");
            string? id = null;
            try
            {
                await File.WriteAllTextAsync(path, Xml(oldInput, oldWait, false));
                using (var f = File.OpenRead(path)) await api.DeployAsync(key, f, "model.bpmn");
                var original = await api.StartAsync(key, new Dictionary<string, Variable> { ["value"] = Variable.String("old"), ["unrelated"] = Variable.String("keep") });
                id = original.Id;
                if (advance) await api.CompleteTaskAsync((await api.GetTasksAsync(id)).Single().Id);
                if (suspended) await api.SetSuspendedAsync(id, true);
                var taskIds = (await api.GetTasksAsync(id)).Select(t => t.Id).Order().ToArray();
                var externalIds = (await api.GetExternalTasksAsync(id)).Select(t => t.Id).Order().ToArray();
                var jobCount = (await api.GetJobsAsync(id)).Count;
                var subscriptions = (await api.GetSubscriptionsAsync(id)).Count;
                var history = (await api.GetActivityHistoryAsync(id)).Where(h => h.EndTime is not null).Select(h => h.Id).Order().ToArray();
                await File.WriteAllTextAsync(path, Xml(newInput, newWait, true));
                using var faultHttp = new HttpClient(new FaultHandler(fault)) { BaseAddress = new Uri(CamundaConnections.MainAddress) };
                var console = new InteractiveConsole(fault is null ? api : new CamundaClient(faultHttp), new Terminal(new StringReader(answers), new StringWriter()));
                Exception? failure = null;
                try { await console.ApplySavedModelAsync(path, CancellationToken.None); } catch (Exception ex) { failure = ex; }
                if (recover)
                {
                    Check(failure is not null, label + ": partial failure reported");
                    Check((await api.GetInstanceAsync(id)).Suspended, label + ": partial update held for recovery");
                    await new InteractiveConsole(api, new Terminal(new StringReader(""), new StringWriter())).ApplySavedModelAsync(path, CancellationToken.None);
                    failure = null;
                }
                if (reject) Check(failure is not null, label + ": rejection reported");
                else if (failure is not null) throw new Exception(label, failure);
                var actual = await api.GetInstanceAsync(id);
                Check(actual.Id == id && (reject ? actual.DefinitionId == original.DefinitionId : actual.DefinitionId != original.DefinitionId), label + ": identity/version");
                Check(actual.Suspended == suspended, label + ": suspension state restored/preserved");
                Check(taskIds.SequenceEqual((await api.GetTasksAsync(id)).Select(t => t.Id).Order()), label + ": current tasks preserved");
                Check(externalIds.SequenceEqual((await api.GetExternalTasksAsync(id)).Select(t => t.Id).Order()), label + ": external tasks preserved");
                Check(jobCount == (await api.GetJobsAsync(id)).Count && subscriptions == (await api.GetSubscriptionsAsync(id)).Count, label + ": timers/subscriptions preserved");
                Check(history.SequenceEqual((await api.GetActivityHistoryAsync(id)).Where(h => h.EndTime is not null).Select(h => h.Id).Order()), label + ": completed history preserved");
                var values = await api.GetVariablesAsync(id);
                Check(values["unrelated"].Value.GetString() == "keep", label + ": unrelated data preserved");
                verify?.Invoke(values);
            }
            finally
            {
                if (id is not null) await api.CancelAsync(id, "migration matrix cleanup");
                var defs = await Pagination.ReadAllAsync((o, n) => api.GetDefinitionsAsync(o, n));
                foreach (var deployment in defs.Where(d => d.Key == key).Select(d => d.DeploymentId).Distinct()) await api.DeleteDeploymentAsync(deployment);
                Directory.Delete(folder, true);
            }
        }
        const string wait = "<userTask id=\"waiting\"/>";
        await Scenario("schema only", "", "", wait, wait);
        await Scenario("future form not prompted", "", "", wait, wait, future: Form(Field(required: true)));
        await Scenario("optional new field may be skipped", "", Form("<c:formField id=\"optional\" type=\"string\"/>"), wait, wait, "\n\n");
        await Scenario("required rejects empty then accepts", "", Form("<c:formField id=\"added\" type=\"string\"><c:validation><c:constraint name=\"required\" config=\"true\"/></c:validation></c:formField>"), wait, wait, "\n\nfilled\n\n", verify: v => Check(v["added"].Value.GetString() == "filled", "required input saved"));
        await Scenario("String to Long rejects invalid input", Form(Field()), Form(Field("long", true)), wait, wait, "invalid\n42\n\n", verify: v => Check(v["value"].Type == "Long" && v["value"].Value.GetInt64() == 42, "type converted"));
        await Scenario("deletion", Form(Field()), "", wait, wait, verify: v => Check(!v.ContainsKey("value"), "runtime variable deleted"));
        await Scenario("cancel input", Form(Field()), Form(Field("long", true)), wait, wait, ":back\n", reject: true, verify: v => Check(v["value"].Value.GetString() == "old", "cancel preserves data"));
        await Scenario("already suspended", Form(Field()), "", wait, wait, suspended: true);
        await Scenario("current form", "", Form(Field("long", true)), wait, wait, "5\n\n", advance: false);
        await Scenario("enum validates allowed values", Form(Field()), Form("<c:formField id=\"value\" type=\"enum\"><c:value id=\"allowed\" name=\"Allowed\"/></c:formField>"), wait, wait, "wrong\n\nallowed\n\n", verify: v => Check(v["value"].Value.GetString() == "allowed", "enum checked"));
        await Scenario("migration server failure", Form(Field()), "", wait, wait, reject: true, fault: "migration", verify: v => Check(v.ContainsKey("value"), "failed migration retains removed data"));
        await Scenario("deletion failure and retry", Form(Field()), "", wait, wait, fault: "delete", recover: true, verify: v => Check(!v.ContainsKey("value"), "retry finishes deletion"));
        await Scenario("lost migration response and retry", Form(Field()), "", wait, wait, fault: "lost", recover: true, verify: v => Check(!v.ContainsKey("value"), "lost response recovered"));
        const string external = "<serviceTask id=\"waiting\" c:type=\"external\" c:topic=\"matrix_work\"/>";
        await Scenario("external worker task", "", "", external, external);
        const string timer = "<intermediateCatchEvent id=\"waiting\"><timerEventDefinition><timeDuration>P1D</timeDuration></timerEventDefinition></intermediateCatchEvent>";
        await Scenario("timer", "", "", timer, timer);
        const string message = "<intermediateCatchEvent id=\"waiting\"><messageEventDefinition messageRef=\"message\"/></intermediateCatchEvent>";
        await Scenario("message subscription", "", "", message, message);
        await Scenario("renamed current step explicit mapping", "", "", wait, "<userTask id=\"replacement\"/>", "2\n");
        await Scenario("renamed field", Form(Field()), Form("<c:formField id=\"renamed\" type=\"string\"/>"), wait, wait, "replacement value\n\n", verify: v => Check(!v.ContainsKey("value") && v["renamed"].Value.GetString() == "replacement value", "rename collected explicitly"));
        await Scenario("numeric bounds", Form(Field()), Form("<c:formField id=\"value\" type=\"long\"><c:validation><c:constraint name=\"min\" config=\"10\"/></c:validation></c:formField>"), wait, wait, "3\n\n12\n\n", verify: v => Check(v["value"].Value.GetInt64() == 12, "numeric min enforced"));
        const string parallel = "<subProcess id=\"waiting\"><startEvent id=\"s\"/><sequenceFlow id=\"p1\" sourceRef=\"s\" targetRef=\"fork\"/><parallelGateway id=\"fork\"/><sequenceFlow id=\"p2\" sourceRef=\"fork\" targetRef=\"left\"/><sequenceFlow id=\"p3\" sourceRef=\"fork\" targetRef=\"right\"/><userTask id=\"left\"/><userTask id=\"right\"/></subProcess>";
        await Scenario("parallel subprocess", "", "", parallel, parallel);
        await Scenario("incompatible current activity", "", "", wait, external, "0\n", reject: true);
        Console.WriteLine($"MATRIX: {count} assertions passed.");
    }
}

internal sealed class FaultHandler(string? mode) : DelegatingHandler(new HttpClientHandler())
{
    private bool fired;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var match = mode == "delete" ? path.EndsWith("/variables") && request.Method == HttpMethod.Post : path.EndsWith("/migration/execute");
        if (mode is not null && match && !fired)
        {
            fired = true;
            if (mode == "lost") { using var response = await base.SendAsync(request, ct); response.EnsureSuccessStatusCode(); throw new HttpRequestException("Simulated lost response"); }
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StringContent("Injected test failure") };
        }
        return await base.SendAsync(request, ct);
    }
}
