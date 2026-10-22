# Prezentace projektu

Hlavním výsledkem je znovupoužitelná knihovna `Camunda.Api` v C#. Není závislá na registračním procesu a jiná aplikace ji může použít přes `ICamundaClient`. Konzole je přehledným klientem této knihovny.

Schéma: **konzole nebo budoucí verifikátor → C# API → REST → server Camunda → instance BPMN**.

Spusťte `dotnet run`. Nabídka zobrazuje modely dostupné v enginu, včetně standardních Invoice a ReviewInvoice. Lze předvést spuštění konkrétní verze, práci s úkolem, změnu řešitele, typovaný vstup, dokončení, zprávy a historii. U standardní fakturace je podporována souborová příloha a přechod do podřízeného procesu. Nový příklad `console_registration` lze projít celý v terminálu bez e-mailu.

## Návrh prezentace

> Implementoval jsem obecné C# API pro přístup ke Camunda 7 a interaktivní konzolovou aplikaci. Umožňuje nahrávat BPMN, spouštět konkrétní verzi procesu, číst stav a proměnné, pracovat s uživatelskými a externími úkoly, událostmi, historií a chybami. Stejné API lze použít pro různé modely, včetně standardních procesů Camundy. Budoucí verifikační komponenta může použít toto API bez samostatného sestavování REST požadavků.

Obecnost se vztahuje na podporované operace, nikoli na automatické pochopení obchodní logiky libovolného BPMN. Výsledek externího úkolu zadává operátor nebo worker. Java delegáty a DMN musí být dostupné na serveru. API není formální verifikátor a nedokazuje absenci zacyklení.

Kód je rozdělen na transport a DTO v `src/Camunda.Api`, navigaci v `InteractiveConsole`, výstup v `Terminal` a zadávání proměnných ve `VariableEditor`. Nápovědy ukázkových modelů jsou oddělené v `ExampleInputs`. Původní webová aplikace zůstává archivním příkladem a nové spuštění ji nepoužívá.

Podrobný návod: [HOW_TO_RUN.md](../HOW_TO_RUN.md).
