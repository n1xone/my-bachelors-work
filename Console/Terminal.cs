using System.Text.Json;
using Camunda.Api;

namespace Bakalarka.ConsoleApp;

internal sealed class EndOfInputException : Exception;

internal sealed class BackException : Exception;

/// <summary>Jednotny interaktivni vstup a vystup bez volani enginu a pravidel modelu.</summary>
internal sealed class Terminal(TextReader input, TextWriter output)
{
    public void Line(string text = "") => output.WriteLine(text);

    public void BeginScreen()
    {
        // Avoid alternate-screen mode in VS Code terminals. It clears the scrollback history,
        // which makes previous terminal output disappear and prevents users from reviewing past steps.
        ClearScreen();
    }

    public void ClearScreen()
    {
        if (!ReferenceEquals(output, Console.Out))
            return;

        output.Write("\u001b[2J\u001b[H");
        output.Flush();
    }

    public void EndScreen()
    {
        // Keep the normal terminal buffer so the user can still scroll and inspect earlier output.
    }

    public void Heading(string title)
    {
        Line();
        Line(new string('─', 72));
        Line(Clean(title));
        Line(new string('─', 72));
    }

    public void Field(string name, string? value) => Line($"  {name,-18} {Clean(value ?? "—")}");

    public void Success(string text) => Line("  ✓ " + Clean(text));

    public void Error(Exception ex)
    {
        var message = ex.Message;

        if (ex is CamundaException engine)
        {
            try
            {
                using var error = JsonDocument.Parse(engine.ResponseBody);
                message = error.RootElement.GetProperty("message").GetString() ?? message;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }
            message = $"Camunda HTTP {(int)engine.StatusCode!}: {message}";
        }
        else if (ex.InnerException is not null)
            message += " — " + ex.GetBaseException().Message;
        Line("  ! " + Clean(message));
        Line("  Stav lze obnovit. Po chybě sítě ověřte výsledek před opakováním zápisu.");
    }

    public async Task<string> Ask(string prompt, CancellationToken ct)
    {
        output.Write("  " + prompt + " > ");
        var text = await input.ReadLineAsync(ct) ?? throw new EndOfInputException();

        if (text.Trim() == ":back")
            throw new BackException();

        return text.Trim();
    }

    public async Task<int> Menu(
        string[] choices,
        CancellationToken ct,
        string back = "Zpět")
    {
        for (var i = 0; i < choices.Length; i++)
            Line($"  {i + 1,2}. {choices[i]}");
        Line($"   0. {back}");

        while (true)
        {
            var value = await Ask("Vyberte číslo", ct);

            if (int.TryParse(value, out var number) && number >= 0 && number <= choices.Length)
                return number;
            Line("  Zadejte číslo ze seznamu.");
        }
    }

    public async Task<T?> Select<T>(
        IReadOnlyList<T> rows,
        Func<T, string> label,
        CancellationToken ct) where T : class
    {
        if (rows.Count == 0)
        {
            Line("  Seznam je prázdný.");

            return null;
        }

        const int pageSize = 20;
        var offset = 0;
        while (true)
        {
            var currentPage = rows.Skip(offset).Take(pageSize).ToArray();
            var options = currentPage.Select(x => Clean(label(x))).ToArray();
            int? prevIndex = null;
            int? nextIndex = null;
            if (offset > 0)
            {
                prevIndex = options.Length + 1;
                options = [..options, "← Předchozí stránka"];
            }
            if (offset + pageSize < rows.Count)
            {
                nextIndex = options.Length + 1;
                options = [..options, "Další stránka →"];
            }

            var index = await Menu(options, ct);
            if (index == 0)
                return null;
            if (index <= currentPage.Length)
                return currentPage[index - 1];
            if (prevIndex is not null && index == prevIndex)
            {
                offset = Math.Max(0, offset - pageSize);
                continue;
            }
            if (nextIndex is not null && index == nextIndex)
            {
                offset += pageSize;
                continue;
            }

            Line("  Zadejte číslo ze seznamu.");
        }
    }

    public async Task Pause(CancellationToken ct) => await Ask("Enter — pokračovat", ct);

    public static string Clean(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    public static string Short(string? text, int width = 42)
    {
        var clean = Clean(text ?? "—");

        return clean.Length <= width ? clean : clean[..(width - 1)] + "…";
    }

    public static string State(string value) => value switch
    {
        "COMPLETED" => "Dokončen",

        "ACTIVE" => "Probíhá",

        "SUSPENDED" => "Pozastaven",

        "EXTERNALLY_TERMINATED" => "Zrušen",

        "INTERNALLY_TERMINATED" => "Ukončen uvnitř procesu",

        _ => value
    };
}
