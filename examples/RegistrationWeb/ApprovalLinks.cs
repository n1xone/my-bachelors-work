using System.Text.Json;

public sealed class ApprovalLinks(IWebHostEnvironment environment)
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private readonly object sync = new();
    private readonly string path = Path.Combine(environment.ContentRootPath, "Data", "approvals.json");
    private List<ApprovalRegistration> Read() => File.Exists(path) ? JsonSerializer.Deserialize<List<ApprovalRegistration>>(File.ReadAllText(path)) ?? [] : [];
    private void Save(List<ApprovalRegistration> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(records));
        File.Move(path + ".tmp", path, true);
    }
    public string Create(string processInstanceId, ExternalTaskInfo task)
    {
        lock (sync)
        {
            var records = Read(); var existing = records.Find(x => x.ProcessInstanceId == processInstanceId);
            if (existing != null) return existing.Token;
            string Value(string name) => task.Variables?.GetValueOrDefault(name)?.Value?.ToString() ?? "";
            var item = new ApprovalRegistration(processInstanceId, Value("userFName"), Value("userRName"), Value("userEmail"), Value("birthDate"));
            records.Add(item); Save(records); return item.Token;
        }
    }
    public bool TryGetRegistration(string token, out ApprovalRegistration registration)
    {
        lock (sync) { registration = Read().Find(x => x.Token == token && !x.Cancelled && x.Expires > DateTimeOffset.UtcNow)!; return registration != null; }
    }
    public ApprovalRegistration? ForProcess(string? processId)
    {
        lock (sync) return Read().Find(x => x.ProcessInstanceId == processId);
    }
    public List<ApprovalRegistration> Pending() { lock (sync) return Read().Where(x => !x.Cancelled && !x.Sent && !x.Confirmed && x.Expires > DateTimeOffset.UtcNow).ToList(); }
    // Zaradit pouze existujici nepotvrzenou registraci; ucet zde nevytvaret ani nepotvrzovat.
    public void RequestResend(string email)
    {
        lock (sync)
        {
            var records = Read();
            var item = records.LastOrDefault(x => !x.Cancelled && x.Expires > DateTimeOffset.UtcNow && !x.Confirmed && string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
            if (item == null || item.LastResend > DateTimeOffset.UtcNow.AddMinutes(-1)) return;
            item.LastResend = DateTimeOffset.UtcNow;
            item.Sent = false;
            Save(records);
        }
    }
    public void Cancel(string processId)
    {
        lock (sync)
        {
            var records = Read();
            var item = records.Find(x => x.ProcessInstanceId == processId);
            if (item == null || item.Confirmed) return;
            item.Cancelled = true;
            Save(records);
        }
    }
    public void Update(string token, bool confirmed = false)
    {
        lock (sync)
        {
            var records = Read(); var item = records.Find(x => x.Token == token);
            if (item == null) return;
            if (confirmed) item.Confirmed = true; else item.Sent = true;
            Save(records);
        }
    }
}
public sealed record ApprovalRegistration(string ProcessInstanceId, string Name, string Surname, string Email, string BirthDate)
{
    public string Token { get; set; } = RegistrationStore.Token();
    public DateTimeOffset Expires { get; set; } = DateTimeOffset.UtcNow.AddDays(2);
    public DateTimeOffset LastResend { get; set; }
    public bool Cancelled { get; set; }
    public bool Sent { get; set; }
    public bool Confirmed { get; set; }
}
