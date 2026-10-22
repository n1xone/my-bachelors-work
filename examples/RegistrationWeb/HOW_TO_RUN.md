# Spuštění

1. Spusť Camundu.
2. V prvním terminálu spusť ngrok:

   ```bash
   ngrok http 5102
   ```

3. Ve druhém terminálu spusť aplikaci .NET:

   ```bash
   dotnet run --launch-profile http
   ```

4. Otevři registrační odkaz vypsaný v konzoli .NET.

## Sledování registrace v Camundě

Otevření `/register` spustí proces `prace_1` a ponechá jej na úloze
`Fill registration form`. Obnovení stránky ve stejné relaci používá stejný
proces. Časovač 20 minut běží již během vyplňování formuláře. Pokud úloha
zanikne, při dalším otevření formuláře nebo odeslání platných údajů aplikace
nabídne zahájení nové registrace.

V Cockpitu otevři aktuální nasazenou verzi procesu `prace_1` a její Process
Instances. Po odeslání formuláře proces pokračuje ke zpracování údajů a čekání
na potvrzení e-mailu (`Wait for user approval`). Rychlé automatické kroky mohou
proběhnout mezi obnoveními Cockpitu. Dokončený proces již není aktivní instancí.

V `Data/registrations.json` jsou lokální relace propojené s Camundou přes
`ProcessId` a `TaskId`. Před odesláním formuláře ještě neznáme jméno ani e-mail
návštěvníka; po odeslání lze registraci identifikovat podle proměnné `userEmail`.
Soubor obsahuje soukromé údaje a přístupové tokeny; není určen ke zveřejnění.

Tlačítko `Nová registrace` odešle POST `/register/new`: nejprve zruší
předchozí nepotvrzený proces aktuální relace a zneplatní jeho potvrzovací odkaz.
Potvrzené registrace nechá dokončit. Při nedostupnosti Camundy se nová
registrace nezahájí, aby předchozí proces nezůstal opuštěný.

Nepotvrzené pokusy aplikace automaticky ruší po 48 hodinách od zahájení
(podle uloženého `Expires`), včetně starších záznamů. Kontrola běží ve workeru;
aplikace i Camunda musí běžet. Po výpadku se úklid zopakuje. Opakované
odeslání e-mailu dobu neprodlužuje. Nové nasazení BPMN není nutné.
