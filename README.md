# API pro přístup k BPMN procesům v Camundě

Bakalářský projekt v C# / .NET 10. Obsahuje knihovnu `Camunda.Api` pro Camunda 7 REST API a konzolovou aplikaci pro práci s procesy. Knihovnu může použít i další C# aplikace, například verifikační komponenta.

## Spuštění

Potřebujete .NET 10 SDK a běžící Camundu 7. Camundu spusťte samostatně, například ve svém Docker kontejneru. Potom v kořeni projektu spusťte konzoli:

```bash
dotnet run
```

Výchozí adresa je `http://localhost:8080/engine-rest/`. Připojení k jinému serveru:

```bash
CAMUNDA_URL=http://localhost:18080/engine-rest/ dotnet run
```

Pro HTTP Basic autentizaci nastavte `CAMUNDA_USERNAME` a `CAMUNDA_PASSWORD`.

Run `dotnet run` and choose **Main Camunda** for direct engine operations, or **Sandbox** for isolated editing and tests. Entering the sandbox automatically saves the latest version of every source process (separately per tenant). **Modely procesů → Uložené procesy Camundy** opens the local catalogue; each model offers the editor, a terminal test, and publication. `0` returns to the server chooser.

V menu se vybírá číslem, `0` vrací zpět a `:back` ruší zadávání. Enter v přehledu instance obnoví stav. Podrobné postupy jsou v [HOW_TO_RUN.md](HOW_TO_RUN.md).

```bash
dotnet run -- help     # seznam příkazů
dotnet run -- watch    # průběžné sledování změn
dotnet run -- demo     # automatické scénáře na připojené Camundě
dotnet run -- visualize models/order-approval.bpmn  # локальный BPMN-редактор в браузере
```

## Možnosti

- Nahrání BPMN, seznam definic a verzí, stažení XML a odstranění nasazení bez kaskádního mazání instancí.
- Spuštění procesu, přehled instancí a podprocesů, pozastavení, obnovení a zrušení.
- Čtení a zápis typovaných proměnných včetně souborů.
- Převzetí a dokončení uživatelských úloh, odeslání formuláře s validací na serveru.
- Zamknutí a dokončení externích úloh, hlášení chyby a nastavení počtu pokusů.
- Zprávy, signály, strom aktivit, jobs, incidenty a historie.

Sandbox workflow:

1. Enter Sandbox. The catalogue synchronizes all current processes from the main Camunda (`CAMUNDA_URL`, default port 8080), without deploying or starting anything.
2. Complete deployment resources are downloaded with GET requests into `.sandbox-models/camunda/`. Existing edits are preserved. The catalogue shows one latest source version per process/tenant; older drafts remain on disk. If the main server is offline, saved copies remain usable. Refresh is available under **Sandbox koncepty → Obnovit katalog z hlavní Camundy**.
3. Select a saved model: open it in the browser editor, or deploy and test it directly from the terminal. Save browser edits and return with Ctrl+C. Deployment, starts, tasks, variables and cancellation target only port 18080.
4. **Publikovat sandbox model na hlavní Camundu** is a separate confirmed action, also available from the model card. It publishes the whole imported deployment, including accompanying models, DMN and forms, preserving resource names and tenant. With the same BPMN process ID this becomes the latest main version for new starts by key; existing main instances remain on their original versions.

Build the editor with `cd BpmnEditor && npm ci && npm run build`. Start the isolated engine with `docker compose -f docker-compose.sandbox.yml up -d`. New containers start without the bundled example application. Existing deployments are not automatically deleted. The editor catalogue contains saved drafts; source files can be explicitly imported by path. Demo models in `models/` remain CLI/test fixtures and are no longer offered for installation in the menu.

Sandbox authentication uses `CAMUNDA_SANDBOX_USERNAME` / `CAMUNDA_SANDBOX_PASSWORD`; main credentials are never automatically sent to the sandbox. The application rejects a main URL pointing to the local sandbox port. Configure genuinely separate engines; URL validation cannot detect arbitrary proxies pointing to the same database.

Deployment resources do not include Java delegate binaries, running workers, external databases, or dependencies in other deployments. Install these separately in the sandbox and point services at test systems before execution. Referenced subprocesses can be imported separately; hard-coded version bindings may need adjusting for sandbox versions. Production instance state is not cloned. REST resource transfer follows the [Camunda 7 deployment API](https://docs.camunda.org/rest/camunda-bpm-platform/7.24/).

The import/publication layer is implemented by `SandboxWorkspace` and separate source/target API clients. A forwarding HTTP proxy is not required. The browser editor in `BpmnEditor/` is the active visualizer. The unused standalone WPF visualizer has been archived outside the source tree. `models/` retains the two integration examples used by `ScenarioDemo`; these fixtures are not automatically deployed.

## Struktura

```text
Konzole / další C# aplikace
          │ ICamundaClient
          ▼
     CamundaClient
          │ HTTP / JSON
          ▼
       Camunda 7
```

| Cesta | Obsah |
| --- | --- |
| `Program.cs` | Připojení k serveru a výběr příkazu |
| `src/Camunda.Api/ICamundaClient.cs` | Veřejné rozhraní knihovny |
| `src/Camunda.Api/CamundaClient*.cs` | REST požadavky, serializace a zpracování chyb |
| `src/Camunda.Api/Models.cs` | Datové typy odpovědí a proměnných |
| `Console/InteractiveConsole*.cs` | Menu, přehled instance a práce s úlohami |
| `Console/Terminal.cs` | Vstup a výstup terminálu |
| `Console/VariableEditor.cs` | Formulářová pole a převod hodnot na typované proměnné |
| `Console/ExampleInputs.cs` | Nápovědy pro ukázkové procesy |
| `Console/Pagination.cs` | Načtení stránkovaných seznamů pro menu a sledovač |
| `Console/ProcessWatcher.cs` | Sledování změn pravidelným dotazováním |
| `Console/ScenarioDemo.cs` | Automatické scénáře |
| `models/`, `samples/` | BPMN modely a vzorová příloha faktury |
| `tests/` | Testy knihovny a konzole |

Soubory `InteractiveConsole.*` tvoří jeden částečný (`partial`) třídní celek; stejně tak `CamundaClient.*`. Složky `bin/` a `obj/` vytváří .NET při sestavení. V průzkumníku VS Code jsou skryté.

## Použití knihovny

Připojte projekt `src/Camunda.Api/Camunda.Api.csproj` ke své C# aplikaci:

```csharp
using Camunda.Api;

using var http = new HttpClient
{
    BaseAddress = new Uri("http://localhost:8080/engine-rest/")
};
ICamundaClient api = new CamundaClient(http);
var instances = await api.GetInstancesAsync(firstResult: 0, maxResults: 100);
foreach (var instance in instances)
{
    var activities = await api.GetActivitiesAsync(instance.Id);
    Console.WriteLine($"{instance.Id}: {activities.ChildActivityInstances.Length} aktivních větví");
}
```

Volající vlastní `HttpClient` a nastavuje autentizaci i timeout. Seznamy podporují `firstResult/maxResults`; ukázka čte první stránku. `CamundaException` obsahuje HTTP status a tělo chyby. Zápisy se automaticky neopakují, protože při ztrátě odpovědi už mohly být provedeny.

## Omezení

Knihovna podporuje Camundu 7 a uvedené operace, nikoli celý REST API. Neobsahuje formální verifikátor, správu uživatelů ani migrace procesů. Absence incidentu sama o sobě nedokazuje absenci deadlocku.

Jednotlivé dotazy netvoří atomický snímek. `watch` může minout změny mezi dvěma dotazy. Výsledek ukončené instance se čte z historie; její dostupnost závisí na nastavení serveru.

Obchodní logiku externí úlohy provádí její worker. Ruční dokončení v konzoli pouze předá výsledek Camundě. Java delegáty a DMN musí být dostupné na serveru. Konzole čte `camunda:formData`, ale nevykonává HTML/JavaScript formuláře. Soubory se při nahrávání načítají do paměti.

## Testy

Bez běžící Camundy:

```bash
dotnet build
dotnet run --project tests/Camunda.Api.Tests
python3 tests/watch.py
```

Testy knihovny ověřují HTTP požadavky, typy proměnných, chyby a konzolový vstup. Test sledovače spouští lokální HTTP server a skutečný CLI proces. Python testy používají standardní knihovnu.

Pro ověření menu na skutečném serveru použijte samostatnou Camunda Run se standardními příklady Invoice/ReviewInvoice:

```bash
CAMUNDA_URL=http://localhost:18080/engine-rest/ python3 tests/console_flow.py
```

Test vytváří vlastní instance a uklízí své nedokončené běhy. Příkaz `demo` ověřuje učební modely, včetně chyby externí úlohy a zrušení procesu.

## Dokumentace Camundy

- [Camunda 7 REST API](https://docs.camunda.org/manual/7.24/reference/rest/)
- [Typované proměnné](https://docs.camunda.org/manual/7.24/reference/rest/overview/variables/)
