namespace Bakalarka.ConsoleApp;

/// <summary>Volitelne napovedy prikladu, nikoli chovani enginu ani seznam povolenych modelu.</summary>
internal static class ExampleInputs
{
    public static IReadOnlyList<InputField> For(string key, string? activity) => (key, activity) switch
    {
        ("invoice", null) =>
        [
            new("creditor", "Dodavatel", "String", "Example supplier"),
            new("amount", "Částka", "Double", "100"),
            new("invoiceNumber", "Číslo faktury", "String", "CONSOLE-001"),
            new("invoiceCategory", "Kategorie", "String", "Misc", "Kategorie: Misc, Travel Expenses, Software License Costs"),
            new("invoiceDocument", "Soubor faktury (cesta)", "File", Path.Combine(AppContext.BaseDirectory, "samples", "invoice.txt"),
                "Enter — ukázková příloha invoice.txt; nebo zadejte cestu k vlastnímu souboru.")
        ],

        ("invoice", "approveInvoice") => [new("approved", "Schválit fakturu?", "Boolean", "true")],

        ("ReviewInvoice", "assignReviewer") => [new("reviewer", "Kontrolor (ID uživatele)", "String", "demo")],

        ("ReviewInvoice", "reviewInvoice") => [new("clarified", "Jsou nejasnosti ohledně faktury vyřešeny?", "Boolean", "true")],

        ("order_approval", "review_order") => [new("approved", "Schválit objednávku?", "Boolean", "true")],

        _ => []
    };
}
