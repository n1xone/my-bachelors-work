using System.Security.Cryptography;
using System.Text.Json;

// Trvale uloziste pro jednu instanci. Data uchovavejte mimo verejny koren webu a zalohujte je.
public sealed class RegistrationStore(IWebHostEnvironment environment)
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private readonly string path = Path.Combine(environment.ContentRootPath, "Data", "registrations.json");
    public List<Invitation> Read() => File.Exists(path)
        ? JsonSerializer.Deserialize<List<Invitation>>(File.ReadAllText(path)) ?? [] : [];
    public void Save(List<Invitation> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(records));
        File.Move(path + ".tmp", path, true);
    }
    public static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
public sealed class Invitation
{
    public string Token { get; set; } = RegistrationStore.Token();
    public DateTimeOffset Expires { get; set; } = DateTimeOffset.UtcNow.AddDays(2);
    public string? ProcessId { get; set; }
    public string? TaskId { get; set; }
    public string? Session { get; set; }
    public bool Cancelled { get; set; }
    public bool Submitted { get; set; }
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    public string? ProtectedDemoPassword { get; set; }
}
