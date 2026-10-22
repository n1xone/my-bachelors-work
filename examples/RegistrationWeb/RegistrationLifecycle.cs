using System.Net;
using System.Text.Json;

public static class RegistrationLifecycle
{
    // Volajici drzi zamykaci branu registracniho uloziste. Brana schvaleni radi zruseni
    // a korelaci zpravy, aby potvrzene registrace mohly normalne skoncit.
    public static async Task CancelAsync(HttpClient http, Invitation item, ApprovalLinks links,
        string reason, CancellationToken ct = default)
    {
        if (item.Cancelled) return;
        await links.Gate.WaitAsync(ct);
        try
        {
            if (links.ForProcess(item.ProcessId)?.Confirmed == true) return;
            if (item.ProcessId != null)
            {
                if (await IsActiveAsync(http, item.ProcessId, ct))
                {
                    using var verified = await http.GetAsync($"process-instance/{item.ProcessId}/variables/emailVerified", ct);
                    if (verified.IsSuccessStatusCode)
                    {
                        var value = await verified.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                        if (value.GetProperty("value").ValueKind == JsonValueKind.True) return;
                    }
                    else if (verified.StatusCode != HttpStatusCode.NotFound)
                    {
                        // Proces muze skoncit mezi pozadavky. Camunda vraci 500 pri cteni
                        // promenne z execution, ktery uz neexistuje.
                        if (await IsActiveAsync(http, item.ProcessId, ct)) verified.EnsureSuccessStatusCode();
                    }
                    using var response = await http.DeleteAsync(
                        $"process-instance/{item.ProcessId}?deleteReason={Uri.EscapeDataString(reason)}", ct);
                    if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
                }
                links.Cancel(item.ProcessId);
            }
            item.Cancelled = true;
            item.PasswordHash = null;
            item.ProtectedDemoPassword = null;
        }
        finally { links.Gate.Release(); }
    }
    private static async Task<bool> IsActiveAsync(HttpClient http, string processId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"process-instance/{processId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}
