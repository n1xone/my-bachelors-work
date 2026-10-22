# API pro přístup k BPMN procesům v Camunda enginu

Samostatná knihovna **Camunda.Api v C# / .NET 10** a interaktivní konzole pro **Camunda 7**. Hlavním cílem je obecné programové rozhraní pro další aplikaci, například verifikační komponentu. Konzole toto rozhraní používá k ovládání skutečného enginu.

## Spuštění

```bash
# Pokud uz mate vlastni Camundu spustenou, dalsi engine nepotrebujete.
dotnet run
```

Výchozí server: `http://localhost:8080/engine-rest/`. Jiné připojení:

```bash
CAMUNDA_URL=http://localhost:18080/engine-rest/ dotnet run
```

Volitelná autentizace: `CAMUNDA_USERNAME` a `CAMUNDA_PASSWORD` pro HTTP Basic. Pro nový lokální engine je připraven `docker compose up -d` (port 8080). Před připojením počkejte na dokončení startu Camundy.

Konzole používá české popisky, nabídky a nápovědu. Názvy modelů a aktivit zůstávají tak, jak jsou definované v BPMN.

## Ovládání

Menu zůstává otevřené do `0 / Ukončit` nebo Ctrl+C. Čísla vybírají položky, `0` vrací zpět, `:back` ruší rozepsaný vstup. Po operaci se načte nový stav. Enter v kartě instance aktualizuje přehled. Síťové chyby nevyvolávají automatické opakování zápisu.

- **Modely:** všechny dostupné nasazené definice, včetně standardních Invoice Receipt a ReviewInvoice, jejich verzí a případných tenantů. Start používá konkrétní ID vybrané definice.
- **Odstranění nasazení:** výběr nasazení, přehled jeho definic a výslovné potvrzení. Odstraňují se všechny definice v nasazení; instance se nekaskádují.
- **Instance:** aktuální úlohy, externí úlohy, čekající události, incidenty, pozastavení, obnovení, zrušení a přechod do podprocesu vytvořeného call activity.
- **Úlohy:** claim/unclaim, konzolový vstup proměnných a submit-form s validací na serveru.
- **Externí úlohy:** výběr přesného úkolu, zamknutí až po zadání dat, dokončení, ohlášení chyby a změna počtu pokusů.
- **Události:** korelace zprávy vybrané instanci a signal cílený na konkrétní execution. Potvrzení může proběhnout přímo z konzole, bez e-mailu.
- **Data:** typované proměnné včetně souborů, BPMN XML, strom aktivit a historie.
- **Jobs:** diagnostika, opakování po chybě a explicitní spuštění jobu; lze tím spustit i časovač před jeho termínem.

Konkrétní návody: **[HOW_TO_RUN.md](HOW_TO_RUN.md)**. Návrh a hranice: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Procesy pro demonstraci

| Proces | Účel |
| --- | --- |
| `console_registration` | Nová registrace bez pošty, hesel a webových stránek: formulář, rozhodnutí, ručně dokončená externí úloha, zpráva z konzole |
| `order_approval` | Schválení/zamítnutí objednávky, externí úloha a zpráva o doručení |
| `invoice` | Originální Invoice Receipt dodaný v Camunda Run; vstupní průvodce obsahuje soubor faktury potřebný pro serverové archivování |
| `ReviewInvoice` | Originální přezkoumání faktury; přiřazení kontrolora, vyjasnění, také otevření z rodičovského invoice |
| `prace_1` | Původní registrační model, pokud je již nasazený; dostupný obecnými operacemi stejně jako ostatní modely |

Položka **5 hlavního menu** nasadí nové výukové modely registrace a objednávky. Standardní modely Camundy konzole načítá z vašeho serveru; nekopíruje je bez jejich DMN a Java delegátů. Pokud server nebyl spuštěn s ukázkovou invoice aplikací, nebudou v seznamu. Jakýkoliv další vykonatelný BPMN lze nasadit z nabídky nebo příkazem `deploy`.

Obchodní efekt externí úlohy provádí volající/worker. Konzolové dokončení znamená předání jejího výsledku, nikoliv automatické odeslání e-mailu či zápis do cizí databáze. Nový hlavní program nespouští SMTP ani původní web. Původní web a jeho data jsou zachované v `examples/RegistrationWeb/` jako archivní příklad; pro nové ovládání je nespouštějte.

## Architektura

```text
Interaktivní konzole / budoucí verifikační komponenta
                     │ ICamundaClient
                     ▼
               CamundaClient (C#)
                     │ REST / HTTP
                     ▼
               Server Camunda 7
                     │
            BPMN definice a instance
```

| Soubor / složka | Odpovědnost |
| --- | --- |
| `src/Camunda.Api/ICamundaClient.cs` | Veřejný asynchronní kontrakt |
| `src/Camunda.Api/CamundaClient*.cs` | HTTP adaptér, serializace, chyby a obecné operace |
| `src/Camunda.Api/Models.cs` | DTO a typované proměnné |
| `Console/Terminal.cs` | Jednotný výpis, číselné nabídky a obsluha vstupu |
| `Console/InteractiveConsole*.cs` | Navigace, karta instance, provádění vybrané operace |
| `Console/VariableEditor.cs` | Typovaný vstup a čtení metadat BPMN formuláře |
| `Console/ExampleInputs.cs` | Oddělené vstupní nápovědy pro ukázkové modely |
| `Console/ProcessWatcher.cs` | Samostatný režim průběžného sledování |
| `Console/ScenarioDemo.cs` | Automatické integrační scénáře |
| `models/`, `samples/` | Dvě výukové BPMN a malé testovací přílohy |
| `tests/` | Kontraktní a integrační ověření |

Knihovna nezná registrační formuláře, názvy invoice aktivit, SMTP ani konzoli. Nápovědy specifické pro modely jsou pouze v konzolové vrstvě. U nového BPMN se pole čtou z `camunda:formData`; chybějící proměnné lze vždy zadat podle názvu a typu. HTML/JavaScript formuláře se v konzoli nevykonávají.

## Příkazy a ověření

```bash
dotnet run -- help                      # všechny samostatné příkazy
dotnet run -- watch                     # průběžný výpis změn, samostatný terminál
dotnet run -- watch console_registration
dotnet run -- demo                      # automatický test, bez e-mailu

dotnet build
dotnet run --project tests/Camunda.Api.Tests
python3 tests/watch.py
# Samostatna Camunda Run s priklady je nutna; vytvari se vlastni testovaci instance:
CAMUNDA_URL=http://localhost:18080/engine-rest/ python3 tests/console_flow.py
```

Ověřeno na Camunda Run 7.24.0: 38 kontraktních a vstupních kontrol, pět konzolových integračních scénářů (včetně vnořeného ReviewInvoice) a čtyři automatické demo scénáře prošly.

`watch` čte stav v intervalu, není úplným auditem. Může minout rychlé změny a nevidí neuložené psaní ve formuláři. Menu záměrně nevypisuje asynchronní události doprostřed rozepsaného vstupu; obnovení stavu je explicitní.

Projekt není úplnou náhradou administrace Camundy ani formálním verifikátorem. Poskytuje přístup k modelům, běhu a vybraným řídicím operacím pro další komponentu. Camunda 8 není podporována.

## Zdroje

- [Camunda 7 REST API](https://docs.camunda.org/manual/7.24/reference/rest/)
- [Typované proměnné včetně File](https://docs.camunda.org/manual/7.24/reference/rest/overview/variables/)
- [Implementace submit-form a claim v Camunda](https://github.com/camunda/camunda-bpm-platform/blob/master/engine-rest/engine-rest/src/main/java/org/camunda/bpm/engine/rest/sub/task/impl/TaskResourceImpl.java)
