# Pizza order builder

A .NET 10 WPF client for the Pizza-Driven Development assignment. It follows `WotCPizzaClient/Documents/HighLevelPlanIter5.md` and communicates with the separate C++ `WotCPizzaServer` process over loopback TCP.

The application has **no third-party dependencies**. MSTest and the test runner are test-only NuGet packages. No accounts, real payments, hosted services, or menu administration are implemented.

## Run locally

Requirements: Windows, the .NET 10 SDK / Windows Desktop runtime, and Visual Studio with .NET desktop development. Building the neighboring server also requires its C++ desktop workload, Windows SDK, and v145 toolset. The following commands assume the two repositories are siblings:

```text
Projects/
  WotCPizzaClient/
  WotCPizzaServer/
```

From the client repository in **Developer PowerShell for Visual Studio**, build and run the server:

```powershell
msbuild ..\WotCPizzaServer\WotCPizzaServer.slnx /m /p:Configuration=Debug /p:Platform=x64
..\WotCPizzaServer\x64\Debug\WotCPizzaServer.exe
```

In a second terminal, from the client repository:

```powershell
dotnet run --project .\WotCPizzaClient\WotCPizzaClient.csproj
```

Alternatively, set `WotCPizzaClient` as the startup project in Visual Studio and press F5. The client currently uses `127.0.0.1:5050`; it has no endpoint-selection UI. Do not select a different server port for the interactive client. Tests inject their own temporary ports.

The server build copies `menu.json` beside its executable; its normal run saves `orders.json` there. The client does not write menus, orders, customer IDs, or pending requests to disk. The server source and existing orders were not changed by this implementation.

### Using the builder

1. Enter a customer ID from 0 to 4,294,967,295 and select **Sign in**. This selects anonymous order grouping, not authentication.
2. Select **Start new order**, then **Add Pizza**. Choose a size, check toppings across the category filters, and enter a quantity. Closing the editor without confirming discards its changes.
3. Set a dollar or percentage tip and optionally apply a promo. Totals update on every draft change. Invalid input or an invalidated promo blocks submission.
4. Select **Place Order**. The server's returned order, totals, status, revision, and receipt replace the submitted draft.
5. Pending orders may be edited or cancelled. **Refresh Now** checks current status; automatic refresh defaults to five minutes. After a successful placement or update, dismissing the confirmation schedules the next refresh in 15 seconds. Once that refresh completes, the configured interval resumes; the minutes setting is unchanged. The default server pending window is only ten seconds, so the kitchen may win an update/cancel race even when the last displayed status is Not Started.
6. **See order** describes a new draft. **See receipt** always shows the last accepted server receipt, even while a pending order has unsaved edits.
7. **Simulate pickup** is an explicit restaurant/debug action available for ready orders. It calls `CompleteOrder` to mark the order Delivered. Cooking transitions are otherwise advanced by the server timer.

Only one new order is retained at a time. Unsaved edits to placed orders remain in memory when switching selections. When a refresh finds a changed revision, **Reconcile changes** requires a decision before resubmission. A started or closed order cannot be edited.

## Tests

From the client repository:

```powershell
dotnet test .\WotCPizzaClient.slnx
```

Tests also appear in Visual Studio Test Explorer. The six `ServerIntegration` tests look for the neighboring Debug executable shown above, or use `PIZZA_SERVER_EXE`. Without an available server executable they are marked inconclusive/skipped, not reported as passing integration tests.

```powershell
$env:PIZZA_SERVER_EXE = 'C:\path\to\WotCPizzaServer.exe'
dotnet test .\WotCPizzaClient.Tests\WotCPizzaClient.Tests.csproj --filter 'TestCategory=ServerIntegration'
```

To run only client/unit and loopback transport tests:

```powershell
dotnet test .\WotCPizzaClient.Tests\WotCPizzaClient.Tests.csproj --filter 'TestCategory!=ServerIntegration'
```

Integration tests launch **their own server processes**, with separate random temporary directories and available loopback ports. They never attach to the interactive server or use its `orders.json`. They retain temporary data under `%TEMP%\WotCPizzaClient.Tests` and print its location for diagnosis. Test fixtures are embedded copies of the server's `menu.json` and `pricing-vectors.json`; they are not an application fallback menu. If the reference fixtures change, copy both files together and review the expected results.

### Coverage

Install the development utility locally once, then collect coverage:

```powershell
dotnet tool install --tool-path .\.tools dotnet-coverage
.\.tools\dotnet-coverage collect -f cobertura -o .\TestResults\coverage.cobertura.xml "dotnet test .\WotCPizzaClient.Tests\WotCPizzaClient.Tests.csproj"
```

Verified in this workspace:

- Solution build succeeds.
- **80 tests passed, zero failed, zero skipped**, including six real-server integration tests and post-submission refresh scheduling checks.
- Actual WPF executable opens its main window and closes normally with exit code zero.
- Client assembly coverage: **78.32% lines, 60.17% branches**. These figures are not a claim of exhaustive coverage. The report also includes the test assembly separately.

Important invariants covered: shared integer-pricing vectors; promo conflicts in both entry orders; strict SAVE5 threshold; quantity/amount bounds; tip parsing; detached editor state; immediate totals; unchanged placement/update JSON after timeouts; unresolved-request action blocking; malformed success responses; revision reconciliation; preservation of invalid tip text; historical totals; partial TCP/UTF-8 responses; EOF, timeout, and cancellation; real-server pricing, retries, cancellation, preview, and cooking/pickup.

Still to verify manually: complete mouse/keyboard workflows, resizing/high-DPI/accessibility, modal Retry/Exit interactions during window closure, and long-running refresh behavior. Automated tests do not exhaust server storage failures, all network disconnect timings, connection saturation, or every lifecycle race. The neighboring server has its own self-tests and network smoke script; those suites were not rerun as part of this client's validation.

## API summary

Each call opens a TCP connection, sends one compact UTF-8 JSON object followed by LF, reads a response through LF, then closes. Field names and integer enum values match the server. No HTTP, TLS, or authentication is used. Keep this demo on loopback; customer IDs are not credentials.

| FunctionId | Operation | Additional Input fields | Successful response |
| --- | --- | --- | --- |
| 0 | GetMenu | None; Input omitted | menu |
| 1 | GetOrder | customerId, orderId | customerOrder |
| 2 | GetAllOrders | customerId | customerOrders |
| 3 | PlaceOrder | customerId, customerOrder | customerOrder |
| 4 | GetOrderStatus | customerId, orderId | status, revision, timeTillReady |
| 5 | UpdateOrder | customerId, customerOrder, expectedRevision | customerOrder |
| 6 | CancelOrder | customerId, orderId | customerOrder |
| 7 | IsPromoValid | customerId, customerOrder, promoCode | totals with PROMO_VALID |
| 8 | CompleteOrder | customerId, orderId | customerOrder |

Menu request:

```json
{"FunctionId":0}
```

Place one small plain pizza with a 15% tip:

```json
{"FunctionId":3,"Input":{"customerId":42,"customerOrder":{"orderId":"00000000-0000-0000-0000-000000000001","menuVersion":"1","pizzas":[{"pizzaCode":0,"count":1,"toppings":[]}],"promoCodes":[],"tipCents":0,"tipBasisPoints":1500}}}
```

Each example needs a trailing LF on the wire. With the fixture menu, the second example has subtotal 500 cents, tax 32 cents, tip 79 cents, and final total 611 cents. Client-generated order IDs are normally random GUIDs; the constant above is only an example.

Responses contain numeric `resultCode`, optional `error`/`message`, and the operation's payload. Result codes are `FAIL=-1`, `SUCCESS=0`, `CONNECTION_ERROR=1`, `CONNECTION_INVALID_CALL=2`, `CONNECTION_INVALID_INPUT=3`, `ORDER_ALREADY_CREATED=4`, `ORDER_ALREADY_STARTED=5`, `PROMO_INVALID=6`, and `PROMO_VALID=7`. Status values are Not Started=0, Started=1, Ready for pick up=2, Delivered=3, Cancelled=4.

A full `customerOrder` flattens request fields and adds `customerId`, `status`, `revision`, `timeTillReady`, `description`, and nested `totals`. Request `tipCents` remains zero in percentage mode; the calculated amount is **totals.tipCents**. Errors such as `stale_menu` and `revision_conflict` are domain failures, not instructions to retry endlessly.

## Design and assumptions

### Money and pricing authority

Money is represented as `long` cents, rates as integer basis points (10,000 = 100%). Each topping is independently rounded down against the base price before summing the unit price; quantities are then applied. Compatible promos apply in ordinal lexical order to the running subtotal, rounding each percentage discount down. Flat discounts are capped at the remaining subtotal. Tax applies to the discounted subtotal. Percentage tips apply to discounted subtotal plus tax, excluding tip; tips are not taxed. Each calculation stage matches the C++ implementation.

The server remains authoritative: it validates and prices every mutation, and the client retains accepted totals and descriptions. There is no general quote endpoint, so a small pure client pricing component mirrors the server for immediate feedback. Shared fixtures and real-server tests reduce drift without introducing another service or framework. Promo preview is also checked against the local calculation; a discrepancy is surfaced rather than silently treated as a valid preview.

Dollar input requires whole cents; percentages accept 0–100 in 0.01 percentage-point increments. Negative inputs, fractions of a cent, and mixed tip modes are rejected. The default tip is zero. Display and input use dollar amounts and a decimal point, independent of the host's currency setting. General monetary calculations round down, but invalid user precision is rejected, not silently truncated.

SAVE5 eligibility uses the **undiscounted subtotal strictly greater than $20**. Promo codes are case-sensitive, with surrounding input whitespace trimmed. Duplicate codes are rejected; conflicts are checked in both directions. `N_PER_CUSTOMER` is deliberately unsupported, matching the server. Limits are 1–100 lines, 1–100 pizzas per line, at most 1,000 pizzas total, and supported amounts up to 1,000,000,000 cents.

### Data-driven extensibility and MVVM

`Models/Contracts.cs` defines wire DTOs and shared enums. `Services/Pricing.cs` handles deterministic validation/pricing. `Services/PizzaService.cs` owns framing, serialization, deadlines, and network I/O. `MainViewModel` coordinates the lifecycle; `PizzaEditorViewModel` owns a detached editor. Views bind to observable state and commands. Window code handles presentation events and the UI timer, not pricing or server mutations.

New sizes, toppings, categories, and promos using existing rule types come from GetMenu without changing core pricing logic or UI definitions. A genuinely new validation rule requires coordinated client/server implementation. UI messages live in `Resources/Strings.resx`; no extra localization package is required. The only injected abstractions are external network and dialog boundaries, so tests can exercise the real workflow without mocking its implementation.

There is no need for a database Unit of Work, CQRS framework, DI container, or generic repository in this client. Accepted snapshots and editable requests provide the necessary separation without extra layers.

### Retries, revisions, and lifecycle

Starting a new draft generates its GUID once. Before sending, the request is serialized once; transport retries reuse that exact JSON, including the original GUID, payload, and expected revision. Only one asynchronous UI operation runs at a time. Editing and replacement actions are disabled while a call is pending; a progress indicator appears after one second. A 15-second cancellable client deadline covers connection, send, processing wait, and response. Requests are bounded to 1 MiB and responses to 16 MiB. The server has its own receive/send deadlines.

If a response is lost, the user can retry or exit. An identical placement retry returning ORDER_ALREADY_CREATED is successful and applies the current server snapshot. Updates reuse their original expected revision. Domain errors are shown directly. A stale menu is fetched and repriced for review, never automatically resubmitted. Changed server revisions retain unsaved edits and require explicit reconciliation.

Cancel/start races are resolved by the server, not by client timing. The client applies the returned snapshot and explains when the kitchen won. It does not roll back a server transition or pretend a failed cancellation succeeded. Polling uses GetOrderStatus, fetching a complete order when the revision changes. Pending cooking duration is explicitly not presented as a queue wait estimate.

**Memory-only recovery is an intentional choice.** Exiting with an unresolved mutation warns that the server might already have accepted it. The original retry request is lost on exit or a crash. On the next sign-in, GetAllOrders reloads accepted orders for that customer; inspect them before placing a replacement. This does not provide durable exactly-once recovery across client restarts. A local pending-request journal would be the next improvement if restart recovery becomes a requirement.

For orders from an older menu version, the client displays the saved receipt and totals instead of reconstructing historical prices from today's menu. Editing a pending historical order requires explicit **Review with current menu**; unavailable items must be replaced or removed, and the recalculated request is sent only after Update Order. The server must use a new, never-reused menu version whenever configuration changes.

### Deliberate scope limits

- Windows/WPF only; no web/mobile client or deployment setup.
- Anonymous customer grouping; no authentication, real payments, or delivery integration.
- No disk cache or durable pending-request recovery.
- No historical structured menu archive; saved receipts are the historical display fallback.
- Endpoint selection is injectable in code/tests, not exposed in the UI.
- A plain Retry/Exit dialog rather than an automatic backoff/retry system.
- Full order history is loaded in one response; no pagination. Histories larger than the response bound need a coordinated server API change.
- No exhaustive visual/accessibility testing or production-load guarantees.

## Diagnosing integration problems

Do not change client prices or suppress server errors to make a failing integration appear successful.

- **Cannot connect:** confirm the server console says it is listening on 127.0.0.1:5050, and check for port conflicts. An untested server path or process startup failure can be the cause.
- **Timeout after placement/update:** preserve the retry data. The mutation may have succeeded before the reply was lost. Retry unchanged; after exiting, check the same customer's orders first.
- **Malformed/incomplete response:** inspect the server console, especially `Client connection` messages. Verify LF framing, exact field names, enum values, and the expected payload. The client does not treat an incomplete success response as acceptance.
- **storage_error or stuck kitchen transitions:** inspect the server's write permissions and `Kitchen update not committed` logs. Check whether another process owns the same data directory. Do not delete `orders.json` to hide the failure.
- **stale_menu:** review the fetched menu and new total. Server configuration changes need a new menu version and server restart.
- **revision_conflict / ORDER_ALREADY_STARTED:** refresh the accepted snapshot and reconcile. The default ten-second pending window can expire while a user edits; this is expected behavior, not a client-side reason to force an update.
- **Pricing mismatch:** compare the exact menu version, integer input values, per-topping rounding, promo order, tax, and tip stages. Re-run the shared vectors and isolated integration tests before changing either implementation.
- **Integration test failure:** inspect the test's retained temporary directory and captured server stdout/stderr. Compare with the server's own `--self-test` and `tests/NetworkSmoke.ps1` suites to narrow the failure.

## AI usage and submission notes

GitHub Copilot assisted with assignment/plan review, C# implementation, WPF bindings, tests, and documentation. Two concrete generated-code issues were corrected during validation: the assumption that `InvalidDataException` would be caught as `IOException`, and a private read-only `TestContext`-typed property that prevented MSTest from discovering integration tests. Regression tests verify that malformed mutation responses preserve the original retry request, and the corrected tests now run against the real server.

The automated verification described here was executed during the AI-assisted implementation. It is not a claim that the candidate independently reviewed or manually tested every feature. Before submission, add your own example of an invariant you personally verified, complete the manual UI checks above, and review the design decisions and remaining cuts.

Publish the client and server to a Git repository host as required by the assignment; publication, remote configuration, and commits were not performed by this implementation. Include clear links or sibling checkout instructions for both repositories.
