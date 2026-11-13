using System.Net.Http.Headers;
using System.Text;

namespace Bakalarka.ConsoleApp;

internal static class CamundaConnections
{
    public const string SandboxAddress = "http://127.0.0.1:18080/engine-rest/";
    public static string MainAddress => Environment.GetEnvironmentVariable("CAMUNDA_URL") ?? "http://localhost:8080/engine-rest/";

    public static void EnsureSeparate(string mainAddress)
    {
        var main = new Uri(mainAddress);
        var sandbox = new Uri(SandboxAddress);
        if (main.IsLoopback && main.Port == sandbox.Port)
            throw new InvalidOperationException("Main Camunda and sandbox must use separate engines. CAMUNDA_URL points to the sandbox port.");
    }

    public static HttpClient Create(string address)
    {
        var client = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        var prefix = client.BaseAddress == new Uri(SandboxAddress) ? "CAMUNDA_SANDBOX_" : "CAMUNDA_";
        var username = Environment.GetEnvironmentVariable(prefix + "USERNAME");
        if (!string.IsNullOrEmpty(username))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + Environment.GetEnvironmentVariable(prefix + "PASSWORD"))));
        return client;
    }
}
