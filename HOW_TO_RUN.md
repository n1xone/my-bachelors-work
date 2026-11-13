# Návod k použití

## Spuštění

Start the application from the project root:

```bash
dotnet run
```

Choose **Sandbox Camunda** or **Main Camunda**. In the main menu, `0` returns to the server chooser; `0` in the chooser exits. The sandbox is option 1 and the main server is option 2. The watcher can still be started directly with `dotnet run -- watch` and stopped with Ctrl+C.

Výchozí adresa je `http://localhost:8080/engine-rest/`. Jinou adresu lze nastavit takto:

```bash
CAMUNDA_URL=http://localhost:18080/engine-rest/ dotnet run
```

Camundu spusťte samostatně, například ve svém Docker kontejneru. Pokud již běží, použijte stávající server. Připravenost ověříte příkazem `curl --fail http://localhost:8080/engine-rest/version`.

## Isolated Camunda Sandbox

Start the separate test engine:

```bash
docker compose -f docker-compose.sandbox.yml up -d
dotnet run -- sandbox definitions
dotnet run -- sandbox menu
```

The `sandbox` prefix forces the CLI to use `http://127.0.0.1:18080/engine-rest/` and ignores `CAMUNDA_URL`. All commands, including deployment, process start, tasks, variables, and cancellation, are then scoped to the isolated container instead of the regular engine on port 8080. Remove the sandbox and its database with `docker compose -f docker-compose.sandbox.yml down -v`. This is a separate Camunda engine, not a reverse proxy to production.

## BPMN Editor

Při prvním použití nainstalujte a sestavte browserový editor (vyžaduje Node.js/npm):

```bash
cd BpmnEditor
npm ci
npm run build
cd ..
```

Potom spusťte z kořene projektu:

```bash
dotnet run -- visualize models/order-approval.bpmn
```

Entering Sandbox automatically downloads the latest version of every main process into local storage. Choose **Modely procesů → Uložené procesy Camundy**, then `BAKALARKA | prace_1` (or another process). Its card offers **Otevřít ve vizualizátoru**, **Nasadit uloženou kopii do sandboxu a spustit test**, and **Publikovat tuto kopii na hlavní Camundu**. The terminal test works without opening a browser. When the main server is unavailable you can use the saved catalogue. To refresh it, choose **Sandbox koncepty → Obnovit katalog z hlavní Camundy**. Existing edits are preserved and older source versions stay on disk. The working catalogue shows one latest draft per process/tenant; fixtures are excluded.

Save in the browser, then press Ctrl+C in the terminal. The selected draft and its original deployment resources are deployed only to the sandbox; unchanged deployments use duplicate filtering. You can start the offered sandbox version and inspect tasks, variables and incidents. Main process instances are never cloned or modified by this workflow.

After testing, choose **Publikovat sandbox model na hlavní Camundu**, review the selected draft and target, then confirm publication. This publishes the whole imported package, preserving resource names and tenant. It does not migrate existing instances. Java delegates, external workers and services must be configured separately for sandbox execution; dependencies from other deployments must be imported separately. See README for isolation limits and authentication.

The standalone command `dotnet run -- visualize models/registration-console.bpmn` opens the same local editor, but the interactive sandbox menu provides the deploy-and-test flow.

For an optional command-line example, explicitly deploy `models/order-approval.bpmn` to the sandbox. Complete its approval task with `samples/order-approved.json` or `samples/order-rejected.json`:

```bash
dotnet run -- sandbox deploy models/order-approval.bpmn
dotnet run -- sandbox start order_approval
dotnet run -- sandbox tasks <instance-id>
dotnet run -- sandbox complete <approval-task-id> samples/order-approved.json
dotnet run -- sandbox fetch prepare_order cli-worker <instance-id>
dotnet run -- sandbox external-complete <external-task-id> cli-worker
dotnet run -- sandbox message <instance-id> order_delivered
```

Approval continues through preparation and delivery; rejection ends the process. These commands execute only in the sandbox engine.

## Registrace

1. Volitelnou testovací ukázku nahrajte explicitně: `dotnet run -- sandbox deploy models/registration-console.bpmn`. Nejde o skutečnou registraci `prace_1`; ta se importuje z hlavní Camundy.
2. Zvolte **1 — Modely procesů** → **Verze již nasazené v sandboxu** → **Registrace — konzolová ukázka**, klíč `console_registration` → **Spustit tuto verzi**.
3. Zadejte označení instance nebo ponechte prázdné. Na výzvu k další proměnné stiskněte Enter.
4. V kartě instance zvolte **1 — Úlohy** → **Vyplnit registraci** → **Vyplnit údaje a dokončit**.
5. Zadejte jméno, oddělení a `ano` pro schválení. Enter u dalších proměnných ukončí zadávání. Při volbě `ne` proces skončí větví zamítnutí.
6. Zvolte **2 — Externí úlohy** → `store_registration` → **Předat výsledek a dokončit**. Další proměnné nejsou potřeba. Jde o ukázku předání výsledku, nikoli o zápis do skutečné databáze.
7. Zvolte **3 — Zprávy / signály** → `registration_confirmed` → Enter. Událost se předává přímo Camundě; neposílají se e-maily ani odkazy.
8. Konzole zobrazí historii a stav **Dokončen**. Enter vás vrátí do menu.

## Invoice Receipt

Standardní model `invoice` už může být ve vaší Camundě dostupný. Menu načítá všechny dostupné verze; vyberte poslední verzi a spusťte ji.

Program nabídne dodavatele, částku, číslo, kategorii a soubor faktury. Enter potvrdí zobrazenou hodnotu. Pro první průchod ponechte `Misc`, částku `100` a ukázkový soubor `invoice.txt`. Lze zadat i vlastní soubor; používá jej serverový krok Archive Invoice.

V kartě instance otevřete **Úlohy → Approve Invoice → Vyplnit údaje a dokončit**:

- `ano` schválí fakturu. Potom dokončete **Prepare Bank Transfer**; další proměnné nejsou potřeba. Archivaci provádí server asynchronně, proto podle potřeby stiskněte Enter pro obnovení stavu.
- `ne` vytvoří podřízený proces **ReviewInvoice**. V kartě rodiče zvolte **7 — Podprocesy**, otevřete podřízený proces a dokončete **Assign Reviewer** s hodnotou `demo`. Potom v **Review Invoice** nastavte `clarified` na `ano`. Po dokončení podřízeného procesu se vrátíte k rodiči a můžete opravenou fakturu schválit.

ReviewInvoice lze spustit i samostatně. Pokud vaše distribuce Camundy neobsahuje ukázkovou fakturační aplikaci, její model a závislosti Java/DMN musí být nainstalované na serveru; samotný BPMN soubor je nenahradí.

## Objednávka

Vyberte model `order_approval`, spusťte jej a otevřete úlohu **Schválit objednávku**. Zadejte `ano` nebo `ne`. Při schválení dokončete externí úlohu `prepare_order` a v událostech předejte `order_delivered`. Při zamítnutí instance ihned skončí.

## Dostupné funkce

| Oblast | Funkce |
| --- | --- |
| Modely | Vybrat verzi, spustit ji, uložit XML a otevřít aktivní instance |
| Odstranění nasazení | Odstranit celé vybrané nasazení včetně jeho zdrojů; instance se nekaskádují |
| Aktivní procesy | Otevřít existující instanci bez ohledu na to, kdo ji spustil |
| Úlohy | Převzít úlohu, uvolnit ji, vyplnit a odeslat formulář |
| Externí úlohy | Dokončit konkrétní úlohu s výstupními daty, nahlásit chybu nebo nastavit počet pokusů |
| Zprávy / signály | Předat očekávanou událost přímo z konzole |
| Proměnné | Zobrazit typy a hodnoty, upravit je, přidat novou nebo načíst soubor |
| Pozastavení | Pozastavit instanci nebo ji obnovit |
| Historie | Zobrazit stav a aktivity, rozlišit dokončení a zrušení |
| Podprocesy | Otevřít volaný podřízený proces z jeho rodičovské instance |
| Joby / chyby | Zobrazit chyby, nastavit opakování nebo spustit job; časovač lze spustit předčasně |
| Aktivity | Zobrazit strom kroků a pokračovat v receive task pomocí execution |
| Zrušení procesu | Zadat důvod a zrušit vybranou instanci |

Pole formuláře BPMN se načítají automaticky. Pro standardní HTML formuláře fakturačních příkladů jsou připravené nápovědy. U ostatních modelů lze proměnné přidat podle názvu a typu. `Date` přijímá český formát `27.07.2006` i ISO formát `2006-07-27`; `Boolean` hodnoty `ano/ne` nebo `true/false`; `Double` číslo s desetinnou tečkou. `File` je cesta k lokálnímu souboru. `Object` je JSON objekt Camundy s poli `value`, `type: "Object"` a `valueInfo`. `Json` vyžaduje podporu tohoto typu na serveru, obvykle Spin.

Enter ponechá zobrazenou hodnotu nebo přeskočí prázdné pole. `:back` zruší zadávání před odesláním. Typy a omezení formuláře kontroluje také Camunda. Po síťové chybě se zápis automaticky neopakuje; obnovte stav a ověřte výsledek.

## Hranice

Všechny dostupné modely se načítají ze serveru, ale jejich Java delegáty, DMN, oprávnění a obchodní pravidla zůstávají na straně Camundy. Konzole neurčuje výsledek externího úkolu; zadá jej uživatel nebo samostatný worker. Oprávnění určuje server a hodnota `demo` při převzetí úkolu není přihlášením.

Odstranění v menu smaže celé nasazení včetně všech jeho zdrojů. Je vyžadováno potvrzení a požadavek nepoužívá kaskádní mazání; běžící instance proto tento příkaz nemaže a Camunda může odstranění odmítnout, pokud tomu brání vazby na instance. Pro úplné odstranění historie a běhů použijte administraci Camundy.

## Testy

```bash
dotnet build
dotnet run --project tests/Camunda.Api.Tests
python3 tests/watch.py
CAMUNDA_URL=http://localhost:18080/engine-rest/ python3 tests/console_flow.py
```

Poslední příkaz vytváří testovací instance na zadané Camundě, ověřuje ovládání přes konzoli a odstraňuje pouze své nedokončené instance. Použijte samostatný testovací engine se standardními modely Invoice/ReviewInvoice.
