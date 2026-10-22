using System.Text.Json;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
// Mistni tajne udaje SMTP jsou mimo wwwroot a nejsou ve sprave verzi.
// Opetovne nacteni umozni zapnout odesilani bez restartu aplikace.
builder.Configuration.AddJsonFile("Data/smtp.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();
var publicUrl = builder.Configuration["App:PublicUrl"];
if (IsLocalUrl(publicUrl))
{
    var ngrokUrl = await DiscoverNgrokUrlAsync();
    if (ngrokUrl != null)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:PublicUrl"] = ngrokUrl
        });
    }
}
builder.Services.AddSingleton<ApprovalLinks>();
builder.Services.AddSingleton<RegistrationStore>();
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddDataProtection();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 100, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddHttpClient("camunda", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Camunda:BaseUrl"] ?? "http://localhost:8080/engine-rest/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHostedService<RegistrationWorker>();
var app = builder.Build();
_ = app.Services.GetRequiredService<UserRepository>(); // Migrate legacy users once before serving.
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; form-action 'self'";
    // Odmitnout pozadavky z jinych webu; tokeny pozvanky a schvaleni nelze uhodnout.
    if (context.Request.Method == "POST" && context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
    { context.Response.StatusCode = 403; return; }
    try { await next(context); }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        app.Logger.LogWarning("Camunda request failed: {Type}", ex.GetType().Name);
        if (!context.Response.HasStarted)
            await WebPages.Page("Služba není dostupná", "Zkuste to prosím znovu za chvíli. Vaše pozvánka zůstává uložená.", 503).ExecuteAsync(context);
    }
});
app.UseRateLimiter();
app.UseStaticFiles();
app.MapRegistration();
// Osobni udaje a administrativni korelace zprav nejsou verejne koncove body.
app.MapGet("/approve", (string? token, ApprovalLinks links, UserRepository users) =>
{
    if (token == null || !links.TryGetRegistration(token, out var item)) return Invalid();
    if (item.Confirmed) return RegistrationResult.Render(item, users, $"/approve/status?token={token}", $"/approve?token={token}");
    return WebPages.Page("Potvrďte svůj e-mail", $"<p class='intro'>Kliknutím potvrďte svou registraci.</p><form method='post' action='/approve'><input type='hidden' name='token' value='{WebPages.E(token)}'><button>Potvrdit e-mail</button></form>");
});
app.MapGet("/approve/status", (string? token, ApprovalLinks links, UserRepository users) =>
{
    if (token == null || !links.TryGetRegistration(token, out var item)) return Results.NotFound();
    return Results.Json(new { state = RegistrationResult.State(item, users) });
});
app.MapPost("/approve", async (HttpContext context, ApprovalLinks links, IHttpClientFactory clients, UserRepository users) =>
{
    var form = await context.Request.ReadFormAsync(); var token = form["token"].ToString();
    await links.Gate.WaitAsync();
    try
    {
        if (!links.TryGetRegistration(token, out var item)) return Invalid();
        if (item.Confirmed) return RegistrationResult.Render(item, users, $"/approve/status?token={token}", $"/approve?token={token}");
        using var http = clients.CreateClient("camunda");
        // Priznak se ulozi ve stejne transakci enginu jako korelace a umozni opakovani po ztrate odpovedi.
        using var state = await http.GetAsync($"process-instance/{item.ProcessInstanceId}/variables/emailVerified");
        var verified = state.IsSuccessStatusCode && (await state.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetBoolean();
        if (!verified)
        {
            using var response = await http.PostAsJsonAsync("message", new
            {
                messageName = "approve_user", processInstanceId = item.ProcessInstanceId,
                processVariables = new { emailVerified = new { value = true, type = "Boolean" } }
            });
            if (!response.IsSuccessStatusCode)
                return WebPages.Page("Potvrzení se nezdařilo", "Proces ještě není připraven nebo již skončil. Zkuste potvrzení zopakovat za chvíli.", 409);
        }
        links.Update(token, confirmed: true); return RegistrationResult.Render(item, users, $"/approve/status?token={token}", $"/approve?token={token}");
    }
    finally { links.Gate.Release(); }
});
await app.StartAsync();
foreach (var address in app.Urls)
    Console.WriteLine($"Registrace: {address.TrimEnd('/')}/register");
var activePublicUrl = app.Configuration["App:PublicUrl"];
if (!IsLocalUrl(activePublicUrl))
    Console.WriteLine($"Veřejná registrace: {activePublicUrl!.TrimEnd('/')}/register");
await app.WaitForShutdownAsync();
static IResult Invalid() => WebPages.Page("Odkaz není platný", "Odkaz vypršel nebo neexistuje. Požádejte správce o novou registraci.", 410);
static bool IsLocalUrl(string? value) => !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.IsLoopback;

static async Task<string?> DiscoverNgrokUrlAsync()
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        using var response = await client.GetAsync("http://127.0.0.1:4040/api/tunnels");
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (!json.TryGetProperty("tunnels", out var tunnels)) return null;
        foreach (var tunnel in tunnels.EnumerateArray())
        {
            if (tunnel.GetProperty("proto").GetString() != "https") continue;
            var url = tunnel.GetProperty("public_url").GetString();
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                return uri.GetLeftPart(UriPartial.Authority);
        }
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
    {
    }
    return null;
}
