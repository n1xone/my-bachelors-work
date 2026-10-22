using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

public static class RegistrationEndpoints
{
    public static void MapRegistration(this WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/register"));
        app.MapGet("/admin", () => Results.Redirect("/register"));
        app.MapGet("/register", async (HttpContext context, RegistrationStore store, IConfiguration config, ApprovalLinks links, UserRepository users, EmailSender mail, IHttpClientFactory clients) =>
        {
            if (context.Request.Query["new"] == "true")
                return WebPages.Page("Nová registrace", WebPages.NewRegistration());
            await store.Gate.WaitAsync();
            try
            {
                var records = store.Read();
                var session = context.Request.Cookies["registration-session"];
                var item = records.LastOrDefault(x => session != null && x.Session == session);
                if (item == null)
                {
                    item = new Invitation { Session = RegistrationStore.Token() };
                    records.Add(item); store.Save(records);
                }
                SetSession(context, config, item.Session!);
                if (item.Cancelled || item.Expires <= DateTimeOffset.UtcNow) return Invalid();
                if (item.Submitted) return StatusPage(item, links, users, mail);
                using var http = clients.CreateClient("camunda");
                if (!await EnsureTaskAsync(http, item, item.Token, store, records)) return Invalid();
                return WebPages.Form(item.Token);
            }
            finally { store.Gate.Release(); }
        });
        app.MapPost("/register/new", async (HttpContext context, RegistrationStore store, ApprovalLinks links, IHttpClientFactory clients) =>
        {
            await store.Gate.WaitAsync();
            try
            {
                var records = store.Read();
                var session = context.Request.Cookies["registration-session"];
                using var http = clients.CreateClient("camunda");
                foreach (var item in records.Where(x => session != null && x.Session == session && !x.Cancelled))
                {
                    await RegistrationLifecycle.CancelAsync(http, item, links, "User started a new registration");
                    store.Save(records);
                }
                context.Response.Cookies.Delete("registration-session");
                return Results.Redirect("/register");
            }
            finally { store.Gate.Release(); }
        });
        app.MapGet("/invite/{token}", async (string token, HttpContext context, RegistrationStore store, EmailSender mail, ApprovalLinks links, UserRepository users, IHttpClientFactory clients) =>
        {
            await store.Gate.WaitAsync();
            try
            {
                var records = store.Read();
                var item = records.Find(x => x.Token == token);
                if (item is null || item.Cancelled || item.Expires < DateTimeOffset.UtcNow) return Invalid();
                if (item.Session != null && context.Request.Cookies["registration-session"] != item.Session)
                    return WebPages.Page("Pozvánka již byla přijata", "Použijte prohlížeč, ve kterém jste pozvánku přijali.", 409);
                if (item.Cancelled || item.Expires <= DateTimeOffset.UtcNow) return Invalid();
                if (item.Submitted) return StatusPage(item, links, users, mail);
                if (item.TaskId != null)
                {
                    using var http = clients.CreateClient("camunda");
                    if (!await EnsureTaskAsync(http, item, token, store, records)) return Invalid();
                }
                return item.TaskId != null ? WebPages.Form(token) : WebPages.Page("Pozvánka k registraci", $"<p class='intro'>Přijměte pozvánku a vyplňte registraci. Na vyplnění máte 20 minut.</p><form method='post' action='/invite/{WebPages.E(token)}/accept'><button>Přijmout pozvánku</button></form>");
            }
            finally { store.Gate.Release(); }
        });
        app.MapPost("/invite/{token}/accept", async (string token, HttpContext context, RegistrationStore store, IHttpClientFactory clients, IConfiguration config) =>
        {
            await store.Gate.WaitAsync();
            try
            {
                var records = store.Read(); var item = records.Find(x => x.Token == token);
                if (item is null || item.Cancelled || item.Expires < DateTimeOffset.UtcNow) return Invalid();
                if (item.Session != null && context.Request.Cookies["registration-session"] != item.Session) return Results.Conflict();
                using var http = clients.CreateClient("camunda");
                if (!await EnsureTaskAsync(http, item, token, store, records)) return Invalid();
                item.Session ??= RegistrationStore.Token(); store.Save(records);
                SetSession(context, config, item.Session!);
                return Results.Redirect($"/invite/{token}");
            }
            finally { store.Gate.Release(); }
        });
        app.MapPost("/invite/{token}/submit", async (string token, HttpContext context, RegistrationStore store, IHttpClientFactory clients, EmailSender mail, UserRepository users, ApprovalLinks links, IDataProtectionProvider dataProtection) =>
        {
            var form = await context.Request.ReadFormAsync();
            var name = form["name"].ToString().Trim(); var surname = form["surname"].ToString().Trim();
            var email = form["email"].ToString().Trim(); var password = form["password"].ToString();
            var birthDate = form["birthDate"].ToString();
            var validName = Regex.IsMatch(name, @"^[A-Za-zА-Яа-яЁёČčŠšŽžÁáÉéÍíÓóÚúÝýŮůĚěŘřŤťĎďŇň -]{1,100}$")
                && Regex.IsMatch(surname, @"^[A-Za-zА-Яа-яЁёČčŠšŽžÁáÉéÍíÓóÚúÝýŮůĚěŘřŤťĎďŇň -]{1,100}$");
            var validEmail = email.Length <= 254 && MailAddress.TryCreate(email, out var address)
                && address.Address == email && email.Contains('.');
            var validBirthDate = DateOnly.TryParseExact(birthDate, "yyyy-MM-dd", out var birth)
                && birth <= DateOnly.FromDateTime(DateTime.UtcNow);
            var validPassword = password.Length is >= 8 and <= 128
                && password.Any(char.IsLetter) && password.Any(char.IsDigit);
            if (!validName || !validEmail || !validBirthDate || !validPassword)
                return WebPages.Form(token, name, surname, email, birthDate,
                    "Některé údaje nejsou platné. Opravte je níže; heslo musí mít alespoň 8 znaků a obsahovat písmeno i číslici.", 400);
            await store.Gate.WaitAsync();
            try
            {
                var records = store.Read(); var item = records.Find(x => x.Token == token);
                if (item is null || item.Cancelled || item.Expires < DateTimeOffset.UtcNow || item.Session == null || context.Request.Cookies["registration-session"] != item.Session) return Invalid();
                if (item.Cancelled || item.Expires <= DateTimeOffset.UtcNow) return Invalid();
                if (item.Submitted) return StatusPage(item, links, users, mail);
                using var http = clients.CreateClient("camunda");
                var emailTaken = users.ContainsEmail(email);
                if (emailTaken)
                    return WebPages.Page("E-mail je již použitý",
                        "<p>Pokud jste registraci ještě nepotvrdili, můžete požádat o nové potvrzení. Jinak se vraťte k formuláři a použijte jinou adresu.</p>"
                        + DeliveryNote(mail) + ResendForm(token, email) + BackLink(), 409);
                if (item.TaskId == null && !await EnsureTaskAsync(http, item, token, store, records)) return Invalid();
                // Obnovit uspesne dokonceni po ztrate odpovedi; nikdy nedokoncit jiny ukol.
                using var task = await http.GetAsync($"task/{item.TaskId}");
                if (task.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    using var variable = await http.GetAsync($"process-instance/{item.ProcessId}/variables/webRegistration");
                    if (!variable.IsSuccessStatusCode) return Invalid();
                    item.Submitted = true; store.Save(records); return StatusPage(item, links, users, mail);
                }
                task.EnsureSuccessStatusCode();
                item.Email = email;
                item.PasswordHash = new PasswordHasher<Invitation>().HashPassword(item, password);
                item.ProtectedDemoPassword = dataProtection.CreateProtector("RegistrationDemoPassword").Protect(password);
                store.Save(records);
                object V(object value, string type = "String") => new { value, type };
                using var response = await http.PostAsJsonAsync($"task/{item.TaskId}/complete", new { variables = new Dictionary<string, object>
                {
                    ["userFName"] = V(name), ["userRName"] = V(surname), ["userEmail"] = V(email),
                    ["birthDate"] = V(birth.ToString("yyyy-MM-dd") + "T00:00:00.000+0000", "Date"), ["webRegistration"] = V(true, "Boolean"),
                    ["passwordValidated"] = V(true, "Boolean"), ["userExists"] = V(false, "Boolean")
                }});
                response.EnsureSuccessStatusCode(); item.Submitted = true; store.Save(records);
                return StatusPage(item, links, users, mail);
            }
            finally { store.Gate.Release(); }
        });
        app.MapGet("/invite/{token}/status", async (string token, HttpContext context, RegistrationStore store, ApprovalLinks links, UserRepository users) =>
        {
            await store.Gate.WaitAsync();
            try
            {
                var item = store.Read().Find(x => x.Token == token);
                if (item == null || item.Session == null
                    || context.Request.Cookies["registration-session"] != item.Session) return Results.NotFound();
                if (item.Cancelled || item.Expires <= DateTimeOffset.UtcNow)
                    return Results.Json(new { state = "expired" });
                return Results.Json(new { state = RegistrationResult.State(links.ForProcess(item.ProcessId), users) });
            }
            finally { store.Gate.Release(); }
        });
        app.MapPost("/invite/{token}/resend", async (string token, HttpContext context, RegistrationStore store, ApprovalLinks links, EmailSender mail, IHttpClientFactory clients) =>
        {
            var form = await context.Request.ReadFormAsync();
            var email = form["email"].ToString().Trim();
            await store.Gate.WaitAsync();
            try
            {
                var records = store.Read();
                var item = records.Find(x => x.Token == token);
                if (item == null || item.Cancelled || item.Expires <= DateTimeOffset.UtcNow || item.Session == null
                    || context.Request.Cookies["registration-session"] != item.Session) return Invalid();
                if (email.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email)
                    return WebPages.Page("Neplatný e-mail", "<p>Zadejte platnou e-mailovou adresu.</p>" + BackLink(), 400);
                var previous = records.LastOrDefault(x => x.Submitted && x.ProcessId != null
                    && string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
                if (previous != null)
                {
                    using var http = clients.CreateClient("camunda");
                    using var state = await http.GetAsync($"process-instance/{previous.ProcessId}");
                    if (state.StatusCode == System.Net.HttpStatusCode.NotFound)
                        return WebPages.Page("Registrace již není aktivní",
                            "<p>Původní proces již skončil nebo byl odstraněn. Pokud jste e-mail nepotvrdili, vyplňte novou registraci. Stejnou adresu můžete znovu použít, pokud účet ještě nebyl vytvořen.</p>" + BackLink(), 409);
                    state.EnsureSuccessStatusCode();
                }
                await links.Gate.WaitAsync();
                try { links.RequestResend(email); }
                finally { links.Gate.Release(); }
                // Stejna odpoved pro nezname, potvrzene, zarazene i docasne blokovane adresy.
                return WebPages.Page("Žádost přijata",
                    "<p>Pokud pro tuto adresu čeká nepotvrzená registrace, potvrzení znovu připravíme. Další žádost můžete odeslat za minutu.</p>"
                    + DeliveryNote(mail) + BackLink());
            }
            finally { store.Gate.Release(); }
        });
    }
    private static void SetSession(HttpContext context, IConfiguration config, string session) =>
        context.Response.Cookies.Append("registration-session", session, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps || config["App:PublicUrl"]?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true,
            SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(2)
        });

    private static async Task<bool> EnsureTaskAsync(HttpClient http, Invitation item, string token,
        RegistrationStore store, List<Invitation> records)
    {
                // Obchodni klic umozni pri opakovani najit start se ztracenou odpovedi.
                if (item.ProcessId == null)
                {
                    var existing = await http.GetFromJsonAsync<JsonElement>($"process-instance?businessKey={token}");
                    if (existing.GetArrayLength() > 0) item.ProcessId = existing[0].GetProperty("id").GetString();
                    else
                    {
                        using var response = await http.PostAsJsonAsync("process-definition/key/prace_1/start", new { businessKey = token });
                        response.EnsureSuccessStatusCode();
                        item.ProcessId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
                    }
                    store.Save(records);
                }
                var tasks = await http.GetFromJsonAsync<JsonElement>($"task?processInstanceId={item.ProcessId}&taskDefinitionKey=reg");
                if (tasks.GetArrayLength() != 1) return false;
                item.TaskId = tasks[0].GetProperty("id").GetString();
                using var claim = await http.PostAsJsonAsync($"task/{item.TaskId}/claim", new { userId = "invite-" + token });
                if (!claim.IsSuccessStatusCode && tasks[0].GetProperty("assignee").GetString() != "invite-" + token)
                    return false;
        store.Save(records);
        return true;
    }

    private static IResult Invalid() => WebPages.Page("Registrace již není aktivní", "<p>Odkaz vypršel nebo registrační úloha již není dostupná. Na vyplnění formuláře máte 20 minut od zahájení registrace.</p><p><a class='button secondary' href='/register?new=true'>Zahájit novou registraci</a></p>", 410);
    private static string BackLink() => "<p><a class='button secondary' href='/register'>Zpět k registraci</a></p>";
    private static string DeliveryNote(EmailSender mail) => mail.IsPickup
        ? "<p class='notice'>Testovací režim: e-maily se nyní nedoručují do schránky. Potvrzovací odkaz vám předá správce. Není třeba se registrovat znovu.</p>"
        : "<p class='intro'>Zkontrolujte také složku spam. Doručení může chvíli trvat.</p>";
    private static string ResendForm(string token, string? email) => $"""
        <form method="post" action="/invite/{WebPages.E(token)}/resend">
        <label>E-mail<input type="email" name="email" value="{WebPages.E(email)}" autocomplete="email" maxlength="254" required></label>
        <button>Znovu odeslat potvrzení</button></form>
        """;
    private static IResult StatusPage(Invitation item, ApprovalLinks links, UserRepository users, EmailSender mail)
    {
        var approval = links.ForProcess(item.ProcessId);
        var resultUrl = $"/invite/{item.Token}";
        if (approval?.Confirmed == true)
            return RegistrationResult.Render(approval, users, resultUrl + "/status", resultUrl);
        if (item.Cancelled || approval?.Cancelled == true || item.Expires <= DateTimeOffset.UtcNow) return Invalid();
        return Waiting(item.Token, item.Email, mail);
    }
    private static IResult Waiting(string token, string? email, EmailSender mail) => WebPages.Page(
        mail.IsPickup ? "Registrace čeká na potvrzení" : "Zkontrolujte svůj e-mail",
        "<p class='intro'>Údaje byly odeslány. Registraci není třeba vyplňovat znovu.</p>"
        + DeliveryNote(mail) + RegistrationResult.Watch("waiting", $"/invite/{token}/status", $"/invite/{token}") + ResendForm(token, email) + WebPages.NewRegistration());
}
