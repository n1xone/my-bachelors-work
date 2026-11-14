# Live migration regression tests

Run `dotnet run --project tests/LiveMigration` with a running Camunda 7 at `CAMUNDA_URL` (default localhost:8080). These tests deploy uniquely named BPMN models, create their own instances, invoke the application's Save callback, then cancel their instances and remove their deployments. Do not point them at an engine where test deployments are disallowed.

Verified on local Camunda 7.24.0 on 2026-11-14: **174 assertions** (6 original scenario, 164 matrix, 4 batch).

Coverage:

- Same instance ID and current task IDs after migration; completed activity history retained.
- New required input on an already completed form, deletion of obsolete runtime variables, preservation of unrelated variables.
- Changed future path actually executes; repeated Save is idempotent.
- Schema-only update, future forms not requested prematurely, current form changes.
- Optional new field can be skipped; required empty value retries.
- String-to-Long conversion with invalid input first; enumeration membership; numeric minimum.
- Renamed field requests an explicit value and removes the old variable.
- Input cancellation restores old execution and data.
- Originally suspended instances stay suspended.
- External task identity, timers, message subscriptions, parallel subprocess tasks.
- Renamed current activity maps explicitly; incompatible replacement is rejected without changing the instance.
- Simulated server rejection of migration, deletion failure after successful migration, and lost successful migration response.
- A new console object recovers pending updates on retry without restarting the instance.
- Invalid BPMN deployment, instances from multiple versions, mixed suspension states, same process key isolated by tenant.

The fault tests use a delegating HTTP handler against the real engine. They inject one failure at a defined boundary; they do not simulate a server crash or database failure. Tests invoke the actual backend Save callback, not browser mouse interactions. They do not establish universal compatibility for arbitrary BPMN migrations, custom Java validators, external HTML forms, multi-instance scopes or external service implementations.

Fixes found by these tests: optional fields incorrectly treated as required; enum/numeric validation missing from migration input; retry could skip variable deletion after an already successful migration. Recovery metadata is stored under `.sandbox-models/.live-updates/` without variable values.
