public static class RegistrationResult
{
    public static string State(ApprovalRegistration? approval, UserRepository users) => approval?.Confirmed != true
        ? "waiting" : users.ContainsEmail(approval.Email) ? "registered" : "confirmed";

    public static string Watch(string state, string statusUrl, string resultUrl) => $"""
        <div id="registration-status" data-state="{WebPages.E(state)}" data-status-url="{WebPages.E(statusUrl)}" data-result-url="{WebPages.E(resultUrl)}" aria-live="polite"></div>
        <script src="/registration-status.js" defer></script>
        <p><a class="button secondary" href="{WebPages.E(resultUrl)}">Obnovit stav registrace</a></p>
        """;

    public static IResult Render(ApprovalRegistration approval, UserRepository users, string statusUrl, string resultUrl)
    {
        var user = users.FindByEmail(approval.Email);
        if (user == null)
            return WebPages.Page("E-mail byl potvrzen", "<p class='intro'>Dokončujeme uložení vaší registrace. Výsledek se zobrazí automaticky.</p>"
                + Watch("confirmed", statusUrl, resultUrl));
        string Field(string label, string value) => $"<div><dt>{label}</dt><dd>{WebPages.E(value)}</dd></div>";
        return WebPages.Page("Jste zaregistrováni", "<div class='status-icon success' aria-hidden='true'>✓</div>"
            + "<p class='intro'>Váš e-mail je potvrzen a účet byl uložen. Registrace je dokončena.</p>"
            + "<section><h2>Vaše údaje</h2><dl>"
            + Field("Jméno", user.Name) + Field("Příjmení", user.Surname)
            + Field("E-mail", user.Email) + Field("Datum narození", user.BirthDate.ToString("dd. MM. yyyy"))
            + "</dl></section><p class='caption'>Tuto stránku můžete zavřít. V tomto prohlížeči najdete přehled znovu na stránce registrace.</p>"
            + WebPages.NewRegistration());
    }
}
