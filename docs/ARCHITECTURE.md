# Návrh a rozsah API

## Cíl

Zadání požaduje C# API pro přístup k BPMN procesům běžícím v Camundě, použitelné z další verifikační komponenty. Výsledkem je knihovna `Camunda.Api`. Interaktivní konzole a automatické scénáře jsou její spotřebitelé. Verifikátor není implementován; API poskytuje vstupní data a řídicí operace.

Definice procesu je verzovaný model. Instance je konkrétní běh s vlastními proměnnými. Engine vykonává automatické kroky, čeká na user task, external task, zprávu nebo timer a může vytvořit více souběžných větví či zavolat další proces. Proto se stav reprezentuje stromem aktivit, seznamy úloh, odběrů událostí a jobs; nikoliv jediným číslem kroku.

## Vrstvy

`ICamundaClient` obsahuje obecný asynchronní kontrakt. `CamundaClient` jej mapuje na Camunda 7 REST. Vlastníkem `HttpClient` je volající: určuje URL, autentizaci, timeout a životnost spojení. `CamundaException` zachovává HTTP status a odpověď enginu. Zápisy se automaticky neopakují, protože při ztracené odpovědi už mohly být provedené.

`InteractiveConsole` řeší navigaci a předává operace přes rozhraní; je rozdělená do souborů pro modely, instance a úlohy. `Terminal` centralizuje výpis, vstup a chyby. `VariableEditor` čte `camunda:formData` a převádí vstupy na typované proměnné. `ExampleInputs` obsahuje pouze doplňkové nápovědy známých modelů, včetně HTML formulářů invoice, a neovlivňuje obecný transport. Veřejná knihovna neobsahuje názvy konkrétních modelů ani SMTP.

Serverem je Camunda. C# knihovna je klientský proxy/adaptér vzdáleného rozhraní, nikoliv síťový forward proxy. Pro použití z C# komponenty není potřeba další HTTP server. Ten lze doplnit jako dalšího spotřebitele stejného kontraktu, pokud bude výslovně požadován.

## Podporované oblasti

| Oblast | Operace |
| --- | --- |
| Repository | Deployment BPMN, stránkování definic, načtení XML, smazání celého deploymentu bez kaskády |
| Runtime | Start podle klíče nebo přesného ID definice, instance, podprocesy, suspend/resume/delete |
| Proměnné | Čtení a zápis, typy String/Boolean/Integer/Long/Double/Date/Null/Json/Object/File, metadata a base64 u File |
| User tasks | Seznam, claim/unclaim, complete a submit-form s backend validací |
| External tasks | Seznam, fetch-and-lock podle tématu, lock přesného ID, complete/failure/retries |
| Události | Odběry zpráv a signal, korelace zprávy instanci, signal jednomu execution, receive-task continuation |
| Diagnostika | Activity tree, přechodové instance, incidenty, jobs, execute/retries |
| Historie | Stránkovaný seznam běhů, detail procesu a historie aktivit |

Seznamy mají explicitní `firstResult/maxResults`; menu podle potřeby načítá další stránky. Start z nabídky používá ID zvolené definice, takže nezamění verzi ani tenant. `claim` pouze přiřazuje úlohu; není autentizací ani obcházením serverové autorizace. Soubor se předává jako typovaná proměnná File s názvem, MIME typem a base64 obsahem. Uploady jsou bufferované v paměti, proto nejsou určené pro velké dokumenty.

Externí úloha se zamyká až po dokončení konzolového vstupu a přesným ID. Ruční dokončení deklaruje výsledek, samo neprovádí doménové účinky. Automatické serverové Java úlohy a business rule tasks nadále vykonává Camunda a potřebují vlastní serverové závislosti.

## Uživatelský výstup

Interaktivní menu ukazuje krátkou kartu vybraného běhu, oddělené nabídky, stav úloh a chyby. Během zadávání nejsou do výstupu vkládány události z pozadí. Po akci se karta znovu načte; Enter explicitně obnovuje stav. Samostatný `watch` poskytuje časovaný výpis změn. Hesla/tokeny jsou ve výpisu hodnot podle názvu maskovány; nejde o klasifikaci všech možných citlivých údajů.

Nová konzolová registrace má samostatný klíč `console_registration`. Nepřepisuje model ani rozpracované instance původního `prace_1`. Starý web je pouze archivní příklad a hlavní program ho nespouští.

## Hranice

REST čtení netvoří společnou transakci; mezi dotazy se běh může změnit. Menu ani watcher neslibují atomický snapshot či úplný audit událostí. Rychlé kroky mezi dvěma poll cykly lze minout. 404 runtime instance se nikdy automaticky nevydává za úspěšné dokončení; výsledek se zjišťuje z historie. Historie závisí na konfiguraci a retention serveru.

Absence incidentu nedokazuje absenci deadlocku. Čekání na uživatele není samo o sobě deadlock. Analýza všech dosažitelných stavů a formální důkaz patří do budoucí verifikační komponenty.

Podporované operace jsou nezávislé na modelu, ale knihovna nepokrývá celý REST API: chybí například uživatelská administrace, migrace, batch operace, změny libovolného process instance modification, administrace tenantů, detailní local variable scopes, delegace/resolve workflow a úplná správa deploymentů. Konzole umí odstranit celé deploymenty bez kaskádního mazání instancí. Nevykonává HTML/JavaScript formuláře ani libovolnou obchodní logiku. Camunda 8 není podporována.

## Ověření

Kontraktní testy používají `HttpMessageHandler` bez závislosti na síti a testovacích NuGet balících. Ověřují typy proměnných, souborová metadata, form submission, cílení zpráv/signal a locků, stránkování, chyby, zrušení a absenci retry zápisů. Další testy ověřují parsování BPMN formuláře, datum a číselnou nabídku.

`tests/watch.py` spouští skutečný CLI proces proti řízenému HTTP stubu, mění úlohy a stav a ověřuje reconnect, claim, proměnné, konec procesu, pokračování sledování a Ctrl+C.

`tests/console_flow.py` spouští skutečné menu proti Camunda Run s invoice příklady: registrace bez pošty, claim/unclaim, přímá zpráva, odmítnutá objednávka, standardní invoice se souborem a samostatný i vnořený ReviewInvoice. Testy vytvářejí vlastní unikátní business keys a uklízejí jen své běhy. `demo` zůstává automatickou kontrolou učebních modelů včetně incidentu a zrušení.

Podklady: [REST API](https://docs.camunda.org/manual/7.24/reference/rest/), [proměnné](https://docs.camunda.org/manual/7.24/reference/rest/overview/variables/), [submit-form / claim implementace](https://github.com/camunda/camunda-bpm-platform/blob/master/engine-rest/engine-rest/src/main/java/org/camunda/bpm/engine/rest/sub/task/impl/TaskResourceImpl.java).
