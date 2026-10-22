> Původní samostatný webový příklad. Hlavní C# API a konzole jsou popsány v [kořenovém README](../../README.md). Příkazy tohoto návodu spouštějte z `examples/RegistrationWeb/`.

# Registrace uživatelů s Camundou

Webová aplikace v C# / ASP.NET Core (.NET 10), která umožňuje registraci a potvrzení e-mailu. Camunda řídí průběh procesu, aplikace zobrazuje stránky a vykonává jednotlivé úlohy.

## Průběh registrace

1. Otevření `/register` zahájí nebo obnoví registraci a spustí odpovídající proces v Camundě.
2. Uživatel vyplní formulář. Aplikace ověří údaje a dokončí úlohu formuláře v Camundě.
3. Proces pokračuje automatickými kroky a aplikace odešle odkaz pro potvrzení e-mailu.
4. Uživatel otevře odkaz a stiskne tlačítko potvrzení. Aplikace předá Camundě zprávu `approve_user`.
5. Aplikace uloží uživatele, odešle závěrečný e-mail a zobrazí výsledek registrace.

## Struktura kódu

| Soubor | Odpovědnost |
| --- | --- |
| `Program.cs` | Spuštění serveru, konfigurace služeb a komunikace s Camundou, obsluha potvrzení e-mailu na `/approve`. |
| `RegistrationEndpoints.cs` | Webové adresy pro registraci, validace formuláře, spuštění procesu, opakované odeslání potvrzení a nová registrace. |
| `RegistrationWorker.cs` | Zpracování externích úloh Camundy na pozadí, odesílání potvrzovacích e-mailů a úklid prošlých registrací. |
| `RegistrationLifecycle.cs` | Zrušení nepotvrzené registrace a souvisejícího procesu. |
| `recovered_registration.bpmn` | Model procesu: úlohy, podmínky, přechody a časovače. |
| `RegistrationStore.cs` | Ukládání rozpracovaných registrací, relací prohlížeče a identifikátorů procesu a úlohy. |
| `ApprovalLinks.cs` | Vytváření a kontrola potvrzovacích tokenů, stav odeslání a potvrzení. |
| `UserRepository.cs` | Ukládání uživatelů a vyhledávání podle e-mailu, ochrana před duplicitami. |
| `WebPages.cs` | HTML šablona stránek a registrační formulář. |
| `RegistrationResult.cs` | Zobrazení stavu registrace a výsledných údajů uživatele. |
| `EmailSender.cs` | Odesílání přes SMTP nebo ukládání testovacích e-mailů do souborů. |
| `User.cs` | Datový model uživatele. |
| `ExternalTask.cs`, `ExternalTaskData.cs` | Datové modely úloh a proměnných získaných z Camundy. |
| `wwwroot/` | CSS a JavaScript pro vzhled stránek a pravidelnou kontrolu stavu registrace. |

`ApprovalPage.cs` obsahuje samostatnou šablonu potvrzovací stránky, která se nyní v ostatním C# kódu nepoužívá.

## Úlohy na pozadí

Worker zpracovává čtyři témata (*topics*):

- `check_user` – kontrola, zda je e-mail již registrovaný.
- `create_account` – příprava záznamu a tokenu pro potvrzení; uživatel se ukládá až později.
- `db_save` – uložení uživatele přes `UserRepository`.
- `send_email` – odeslání zprávy o dokončení registrace.

## Data a pomocné soubory

Data se ukládají lokálně do `Data/`: `registrations.json` obsahuje rozpracované registrace, `approvals.json` potvrzovací odkazy a `users.txt` dokončené registrace. V této demoverzi obsahuje `users.txt` také hesla v otevřeném textu. Testovací e-maily se ukládají do `Data/mail/`.

Nastavení je v `appsettings*.json`, parametry lokálního spuštění v `Properties/launchSettings.json`. Složka `scripts/` obsahuje pomocné skripty pro Gmail a veřejné demo. Soubor `tests/flow.py` ověřuje registrační scénáře s imitací Camundy.

Postup spuštění je v [HOW_TO_RUN.md](HOW_TO_RUN.md). Pro seznámení s kódem doporučujeme pořadí: `Program.cs` → `RegistrationEndpoints.cs` → `RegistrationWorker.cs` → třídy pro ukládání dat.
