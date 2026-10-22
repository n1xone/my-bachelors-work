# Návod k použití

## Spuštění

Spusťte Camundu, počkejte na její načtení a v kořenové složce projektu spusťte:

```bash
dotnet run
```

Otevře se české konzolové menu. Pokud stále běží starší web nebo sledovač, ukončete ho pomocí Ctrl+C. Sledovač lze spustit samostatně příkazem `dotnet run -- watch`.

Výchozí adresa je `http://localhost:8080/engine-rest/`. Jinou adresu lze nastavit takto:

```bash
CAMUNDA_URL=http://localhost:18080/engine-rest/ dotnet run
```

Pokud ještě nemáte server, spusťte `docker compose up -d`. Nespouštějte druhý server na stejném portu, pokud už běží `camunda7`. Připravenost ověříte příkazem `curl --fail http://localhost:8080/engine-rest/version`.

## Registrace bez e-mailu

1. V hlavním menu zvolte **5 — Přidat ukázkové modely bez e-mailu**. Nahraje se registrace a objednávka. Opakované nahrání stejné verze omezuje filtrování duplicit.
2. Zvolte **1 — Modely procesů** → **Registrace — konzolová ukázka**, klíč `console_registration` → **Spustit tuto verzi**.
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

## Objednavka

Vyberte model `order_approval`, spusťte jej a otevřete úlohu **Schválit objednávku**. Zadejte `ano` nebo `ne`. Při schválení dokončete externí úlohu `prepare_order` a v událostech předejte `order_delivered`. Při zamítnutí instance ihned skončí.

## Dostupne funkce

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

Starý model `prace_1` zůstává zachován. Jeho čekání na `approve_user` lze obsloužit v nabídce **Zprávy**. Pro novou ukázku použijte `console_registration`, který neobsahuje původní e-mailovou logiku. Pokud stále běží starý webový worker, bude dál samostatně zpracovávat jeho úkoly; ukončete jej pomocí Ctrl+C.

## Testy

```bash
dotnet build
dotnet run --project tests/Camunda.Api.Tests
python3 tests/watch.py
CAMUNDA_URL=http://localhost:18080/engine-rest/ python3 tests/console_flow.py
```

Poslední příkaz vytváří testovací instance na zadané Camundě, ověřuje ovládání přes konzoli a odstraňuje pouze své nedokončené instance. Použijte samostatný testovací engine se standardními modely Invoice/ReviewInvoice.
