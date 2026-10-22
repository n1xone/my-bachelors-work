using System.Globalization;
using System.Net;

// Vsechny dynamicke hodnoty se pred vlozenim do HTML koduji.
public static class ApprovalPage
{
    public static IResult Render(string title, string message,
        ApprovalRegistration? registration = null, int statusCode = 200)
    {
        static string Encode(string value) => WebUtility.HtmlEncode(value);
        static string Field(string label, string value) =>
            $"<div><dt>{label}</dt><dd>{Encode(string.IsNullOrWhiteSpace(value) ? "—" : value)}</dd></div>";

        var details = "";
        if (registration is not null)
        {
            var date = registration.BirthDate.Split('T')[0];
            if (DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var birthDate))
                date = birthDate.ToString("d. M. yyyy", CultureInfo.InvariantCulture);

            details = "<section aria-labelledby='details'><h2 id='details'>Vaše údaje</h2><dl>"
                + Field("Jméno", registration.Name)
                + Field("Příjmení", registration.Surname)
                + Field("E-mail", registration.Email)
                + Field("Datum narození", date)
                + "</dl></section>";
        }

        var success = statusCode == 200;
        var html = $$"""
            <!doctype html>
            <html lang="cs">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta name="referrer" content="no-referrer">
                <title>{{Encode(title)}} · Registrace</title>
                <link rel="stylesheet" href="/approval.css">
            </head>
            <body>
                <main>
                    <div class="brand"><span class="brand-mark">R</span> Registrace</div>
                    <article>
                        <div class="status-icon {{(success ? "success" : "error")}}" aria-hidden="true">{{(success ? "✓" : "!")}}</div>
                        <p class="eyebrow">{{(success ? "POTVRZENÍ PŘIJATO" : "POTVRZENÍ REGISTRACE")}}</p>
                        <h1>{{Encode(title)}}</h1>
                        <p class="intro">{{Encode(message)}}</p>
                        {{details}}
                        <footer>{{(success ? "Děkujeme. Tímto je vaše potvrzení hotové." : "Pokud potíže přetrvávají, obraťte se na správce registrace.")}}</footer>
                    </article>
                    <p class="caption">Vaše registrace, přehledně na jednom místě.</p>
                </main>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }
}
