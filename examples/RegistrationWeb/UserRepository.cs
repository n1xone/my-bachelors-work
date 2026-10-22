using System.Text;
using System.Text.Json;

// Zdroj pravdy je citelny textovy soubor. Hodnoty JSON bezpecne koduji nove radky.
public sealed class UserRepository
{
    private readonly string path;
    private readonly object gate = new();
    public UserRepository(IWebHostEnvironment environment)
    {
        path = Path.Combine(environment.ContentRootPath, "Data", "users.txt");
        var legacy = Path.Combine(environment.ContentRootPath, "Data", "users.json");
        if (!File.Exists(path))
            Write(File.Exists(legacy)
                ? JsonSerializer.Deserialize<Dictionary<string, User>>(File.ReadAllText(legacy)) ?? [] : []);
    }
    public bool ContainsEmail(string email) => FindByEmail(email) != null;
    public User? FindByEmail(string email)
    {
        lock (gate) return Read().Values.FirstOrDefault(x => string.Equals(x.Email.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
    }
    public void Save(string taskId, User user)
    {
        lock (gate)
        {
            var users = Read();
            if (users.ContainsKey(taskId) || users.Values.Any(x => string.Equals(x.Email, user.Email, StringComparison.OrdinalIgnoreCase))) return;
            users.Add(taskId, user); Write(users);
        }
    }
    private Dictionary<string, User> Read()
    {
        var users = new Dictionary<string, User>();
        if (!File.Exists(path)) return users;
        var fields = new Dictionary<string, string>();
        void Finish()
        {
            if (fields.Count == 0) return;
            users.Add(fields["Task ID"], new User { Name = fields["Name"], Surname = fields["Surname"],
                Email = fields["Email"], Password = fields.GetValueOrDefault("Password"), BirthDate = DateOnly.ParseExact(fields["Birth date"], "yyyy-MM-dd") });
            fields.Clear();
        }
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.StartsWith("---")) { Finish(); continue; }
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon < 1) throw new InvalidDataException("Invalid users.txt field.");
            fields.Add(line[..colon].Trim(), JsonSerializer.Deserialize<string>(line[(colon + 1)..].Trim())
                ?? throw new InvalidDataException("Missing users.txt value."));
        }
        Finish(); return users;
    }
    private void Write(Dictionary<string, User> users)
    {
        var text = new StringBuilder("# REGISTERED USERS — demo database\n# UTF-8; one record per block. Includes plaintext demo passwords; keep this file private.\n# Edit quoted values only; keep Task ID unique.\n\n");
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        void Field(string label, string value) => text.AppendLine($"{label,-12}: {JsonSerializer.Serialize(value, options)}");
        foreach (var (id, user) in users)
        {
            text.AppendLine("------------------------------------------------------------");
            Field("Task ID", id); Field("Name", user.Name); Field("Surname", user.Surname);
            Field("Email", user.Email); Field("Birth date", user.BirthDate.ToString("yyyy-MM-dd"));
            Field("Password", user.Password ?? "[unavailable: registered before password capture]");
            text.AppendLine();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", text.ToString(), Encoding.UTF8);
        File.Move(path + ".tmp", path, true);
    }
}
