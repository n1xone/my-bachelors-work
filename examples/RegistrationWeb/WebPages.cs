using System.Net;
public static class WebPages
{
    public static string NewRegistration() => "<form method='post' action='/register/new'><p class='caption'>Nová registrace zruší předchozí nepotvrzenou registraci.</p><button class='secondary'>Nová registrace</button></form>";
    public static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    public static IResult Page(string title, string body, int status = 200) => Results.Content($"""
        <!doctype html><html lang="cs"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{E(title)} · Registrace</title><link rel="stylesheet" href="/approval.css"></head>
        <body><main><div class="brand"><span class="brand-mark">R</span> Registrace</div><article><h1>{E(title)}</h1>{body}</article></main></body></html>
        """, "text/html; charset=utf-8", statusCode: status);
    public static IResult Form(string token, string? name = null, string? surname = null,
        string? email = null, string? birthDate = null, string? validationError = null, int status = 200)
    {
        var error = string.IsNullOrEmpty(validationError) ? "" : $"<p class='notice' role='alert'>{E(validationError)}</p>";
        return Page("Vaše registrace", $"""
        <p class="intro">Vyplňte údaje do 20 minut od zahájení registrace. Na váš e-mail odešleme odkaz pro potvrzení.</p>
        <form method="post" action="/invite/{E(token)}/submit">
        {error}
        <label>Jméno<input name="name" value="{E(name)}" autocomplete="given-name" maxlength="100" required></label>
        <label>Příjmení<input name="surname" value="{E(surname)}" autocomplete="family-name" maxlength="100" required></label>
        <label>Datum narození<input name="birthDate" type="date" value="{E(birthDate)}" max="{DateTime.UtcNow:yyyy-MM-dd}" required></label>
        <label>E-mail<input name="email" type="email" value="{E(email)}" autocomplete="email" maxlength="254" required></label>
        <label>Heslo<input name="password" type="password" autocomplete="new-password" minlength="8" maxlength="128" required></label>
        <p class="intro">Heslo musí obsahovat alespoň 8 znaků, písmeno a číslici.</p>
        <button>Odeslat registraci</button></form>
        """, status);
    }
}
