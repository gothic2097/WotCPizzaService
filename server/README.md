# Pizza order server

A Windows C++20 console service for the Pizza-Driven Development exercise. It uses Winsock TCP, Windows SDK JSON, integer pricing, and a local JSON order store. No third-party libraries or package restore are required.

**Scope:** this repository implements the server and its tests. The planned C# WPF order builder is not implemented here. The network test is a protocol test client, not an interactive order builder. The complete exercise still needs the builder, its pricing-vector tests, and publication to a Git repository host.

## Build and run

Prerequisites: Windows 10/11, Visual Studio 2026 with **Desktop development with C++**, the v145 toolset, and a Windows SDK containing C++/WinRT headers. Use **Developer PowerShell for VS 2026**, from this repository's root:

```powershell
msbuild .\WotCPizzaServer.slnx /m /p:Configuration=Debug /p:Platform=x64
.\x64\Debug\WotCPizzaServer.exe
```

The server listens on **127.0.0.1:5050**. Ctrl+C stops accepting clients and joins active workers. Run and Debug in Visual Studio also work. For an optimized build, replace `Debug` with `Release` in both commands.

The build copies `WotCPizzaServer/menu.json` and `pricing-vectors.json` beside the executable. The server reads `menu.json` there and creates `orders.json` there on its first successful mutation. It never uses the shell's working directory to locate default data. Build steps do not overwrite `orders.json`.

Optional arguments: `--port 5051` and `--data C:\path\to\data`. A custom data directory must already exist, contain `menu.json`, and be writable. An exclusive `orders.lock` file handle prevents multiple server processes from using the same store. The lock file can remain after shutdown; the open handle, not file existence, owns the lock.

Edit the source menu and rebuild to change configuration. **Use a new, never-reused `menuVersion` whenever configuration changes.** Restart the service to load it. Existing orders retain their accepted prices, descriptions, and cooking durations. A restart detects changes to the last saved configuration made without changing its version. Older versions must not be reused manually. Stop the service and remove the output directory's `orders.json` only if you deliberately want to discard all demo orders.

### Run tests

After building, from the repository root:

```powershell
.\x64\Debug\WotCPizzaServer.exe --self-test
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\NetworkSmoke.ps1
```

The self-test executable returns nonzero on failure. The smoke script fails on any assertion and can target another build with `-Executable .\x64\Release\WotCPizzaServer.exe`. Both suites use isolated temporary directories and do not modify real orders. The network suite starts and stops its own server on an available loopback port, including a forced process restart.

Validated in this workspace: **Debug and Release x64 builds, 25 self-tests, and 20 network checks**, all passing. These dependency-free self-tests run through the executable rather than Visual Studio Test Explorer. Branch coverage has not been measured; no 80% coverage claim is made.

## API

### Transport

Connect to loopback TCP, send **one UTF-8 JSON object followed by LF (`\n`)**, read one JSON response line, then close. CRLF is accepted. Do not send pretty-printed multiline JSON or pipeline multiple requests. TCP reads/writes may be partial; the server assembles/sends the complete message. Request size is at most 1 MiB, with five-second receive/send deadlines. Request processing and disk operations are not covered by those I/O deadlines. At most 64 clients are handled concurrently; excess connections are closed and should be retried with backoff.

All field names are case-sensitive. All numeric input fields below are integers. Unknown extra fields, including client totals/status, are ignored rather than trusted. `customerId` is an anonymous uint32 grouping key (0 through 4,294,967,295), **not authentication**.

Request envelope:

```json
{"FunctionId":0}
```

Except for `GetMenu`, requests require `Input.customerId`. The examples below must be minified to one line before sending, with an LF appended.

| ID | Operation | Additional `Input` fields | Success response fields |
|---:|---|---|---|
| 0 | GetMenu | None; `Input` may be omitted | `menu` (including version, prices, rates, promos, settings) |
| 1 | GetOrder | `orderId` | `customerOrder` |
| 2 | GetAllOrders | None | `customerOrders`; unknown customers return `FAIL` |
| 3 | PlaceOrder | `customerOrder` request below | `customerOrder`; an identical retry returns code 4 |
| 4 | GetOrderStatus | `orderId` | `status`, `revision`, `timeTillReady` |
| 5 | UpdateOrder | `customerOrder`, `expectedRevision` | `customerOrder` |
| 6 | CancelOrder | `orderId` | `customerOrder` (cancelled) |
| 7 | IsPromoValid | `customerOrder`, `promoCode` | Code 7 and hypothetical `totals`; does not save/apply anything |
| 8 | CompleteOrder | `orderId` | `customerOrder` (delivered); restaurant/debug operation |

`UpdateOrder` takes the order ID from `customerOrder.orderId`; there is no second editable ID. `IsPromoValid` checks the whole proposed order after including the supplied code, without adding it twice if already selected. Revalidate after any order edit. It needs a valid draft GUID and the current menu version, but not a stored order.

Placement example, including a client-generated GUID:

```json
{
  "FunctionId": 3,
  "Input": {
	"customerId": 42,
	"customerOrder": {
	  "orderId": "00000000-0000-0000-0000-000000000001",
	  "menuVersion": "1",
	  "pizzas": [
		{
		  "pizzaCode": 2,
		  "count": 2,
		  "toppings": [
			{ "toppingBaseCode": 0, "toppings": [0] },
			{ "toppingBaseCode": 1, "toppings": [1] }
		  ]
		}
	  ],
	  "promoCodes": ["ILOVEPIZZA"],
	  "tipCents": 0,
	  "tipBasisPoints": 1500
	}
  }
}
```

This selects two large pizzas, each with pepperoni and chicken, ILOVEPIZZA, and a 15% tip. Expected totals:

```json
{
  "subtotalCents": 2856,
  "discountCents": 285,
  "totalBeforeTaxAndTipCents": 2571,
  "taxCents": 167,
  "tipCents": 410,
  "totalAfterTaxAndTipCents": 3148
}
```

A successful order response has `resultCode` and `customerOrder`. The latter contains the original request fields plus `customerId`, `status`, `revision`, `timeTillReady`, a printable `description`, and a nested `totals` object as above. The request's `tipCents` remains zero in percentage mode; **the calculated tip is `customerOrder.totals.tipCents`**. Format money as dollars and exactly two decimal places in the client.

Query and cancel examples:

```json
{"FunctionId":4,"Input":{"customerId":42,"orderId":"00000000-0000-0000-0000-000000000001"}}
{"FunctionId":6,"Input":{"customerId":42,"orderId":"00000000-0000-0000-0000-000000000001"}}
```

Send these on separate connections. For an update, use ID 5, supply the edited request, and add `expectedRevision` alongside `customerOrder`. Preserve the same request and expected revision when retrying that update. After a successful edit, subsequent edits must use the newly returned revision.

### Status and result codes

Statuses: `0 Not Started`, `1 Started`, `2 Ready for Pickup`, `3 Delivered`, `4 Cancelled`.

Result codes preserve the plan's numeric contract:

| Value | Name | Meaning |
|---:|---|---|
| -1 | FAIL | Not found, conflict, stale menu, invalid transition, storage failure, or internal failure |
| 0 | SUCCESS | Operation committed or query succeeded |
| 1 | CONNECTION_ERROR | Incomplete request, timeout, or connection error when a response is still possible |
| 2 | CONNECTION_INVALID_CALL | Unknown function ID |
| 3 | CONNECTION_INVALID_INPUT | Missing, wrong-type, out-of-range, or otherwise invalid input |
| 4 | ORDER_ALREADY_CREATED | Same accepted submission replayed; return current order |
| 5 | ORDER_ALREADY_STARTED | Order is not editable/cancellable; also used for attempts to edit cancelled orders |
| 6 | PROMO_INVALID | Unknown, duplicate, ineligible, or conflicting promo |
| 7 | PROMO_VALID | Proposed promo/order combination is valid |

Failures normally include string `error` and human-readable `message`. For example:

```json
{"resultCode":-1,"error":"revision_conflict","message":"Order changed. Fetch the current order before editing again."}
```

A rejected update/cancel due to lifecycle returns code 5, `message`, and the current `customerOrder`. Clients must handle these returned snapshots, not assume their local state is current. Input failures are not network failures: display their messages instead of retrying endlessly. Transport failures have an uncertain outcome; retry using the same identifiers/payloads.

## Assumptions and pricing contract

1. Money is signed 64-bit **integer cents**; percentages are integer **basis points** (`10000 = 100%`, `650 = 6.5%`, `1500 = 15%`). Windows JSON exposes numbers as doubles, but supported integers are safely within its exact-integer range and are converted before pricing. Pricing never uses floating-point arithmetic.
2. All amounts/rates are nonnegative. Round **down to cents** at each stated stage, as selected in the plan review. For each topping, compute `baseCents * categoryBasisPoints / 10000`, truncating before summing the unit price; multiply the resulting unit price by quantity. Sum all lines for the subtotal.
3. Validate promo eligibility against the **undiscounted subtotal**. SAVE5 requires **strictly more than 2000 cents**, not at least 2000. Codes are case-sensitive, without whitespace normalization. Reject unknown/duplicate codes. ILOVEPIZZA and SAVE5 conflict in either entry order.
4. Apply discounts before tax. A percentage discount is rounded down before subtraction. Flat discounts are capped at the remaining subtotal. For future compatible promos, apply them in lexical code order to the running discounted subtotal; client entry order does not change totals.
5. Tax is rounded down on the discounted subtotal. Tips are not taxed. Percentage tips are rounded down on **discounted subtotal plus tax**, excluding the tip itself. Dollar mode uses `tipCents` and zero `tipBasisPoints`; percentage mode uses zero `tipCents`. Both zero means no tip. Supplying two nonzero modes is invalid. Supported percentage tips are 0–100% in 0.01 percentage-point increments.
6. One to 100 lines, quantity 1–100 per line, at most 1000 pizzas total. Codes must exist in the current menu. A topping is selected at most once per category per line; duplicate categories are invalid. Identical pizza lines may remain separate. Money/subtotal/total is bounded to 1,000,000,000 cents ($10 million); calculations are bounded to avoid integer overflow. Revisions use the same numeric ceiling, reserving room for lifecycle transitions.
7. Client GUIDs use the standard 36-character hex-and-hyphen form, normalized to lowercase. GUIDs are globally unique across customer groups. The server accepts and returns the client-generated order ID. Never generate a new ID just because a reply timed out.
8. For retry equality, JSON property order is irrelevant; promo codes, topping categories, and toppings are normalized into sorted order. **Pizza line order remains significant.** Changing line order is an edit, not an identical submission retry.
9. Anonymous IDs group orders only. No account creation, sign-in security, ownership authentication, payments, or personal data are implemented. The loopback service is a local demonstration, not an Internet-facing service.
10. Cancellation is a retained terminal state. Delivered/cancelled IDs cannot be reused. `timeTillReady` for pending orders is their cooking duration, not a queue-aware wait estimate; started orders report remaining seconds, and terminal/ready orders report zero.

## Design write-up

### Money and one pricing specification

`Pricing.cpp` is a pure, data-driven pricing function used by placement, updates, and promo previews. The server derives every price from its immutable loaded menu; client totals and status cannot override it. The JSON boundary names units explicitly. `Models.h` keeps menu, requested lines, accepted orders, totals, and status distinct. Accepted totals and descriptions are stored snapshots: kitchen start does not reprice an accepted order.

The planned WPF client will mirror these integer operations for immediate running totals, as chosen during plan review. `pricing-vectors.json` is the shared conformance contract, with valid totals and invalid-promo cases. The client must run the same vectors and send its `menuVersion`. New placements/edits with a stale version fail rather than being silently repriced; fetch the menu, recalculate, and ask the customer to confirm. An identical original submission retry bypasses current-menu pricing and returns the already accepted order. The server remains authoritative, but client/server equality is not claimed until the client implementation passes the vectors. A server quote operation was deliberately not added.

### Extensibility

Add sizes, toppings, or topping categories to `menu.json`; no pricing-code change is required. Topping codes are scoped by category. Add percentage/flat promos with existing validation rules in the same file. Rules are data: type 0 (`ONE_PROMO_PER_ORDER`) means a given code cannot repeat, a restriction enforced globally; type 1 lists incompatible promo codes; type 3 specifies a strict `thresholdCents`. Every selected promo's rules are checked, so a one-sided conflict declaration is sufficient. Type 2 (`N_PER_CUSTOMER`) is explicitly rejected, not silently ignored.

These rules support new codes and combinations without core pricing changes. A genuinely new condition family needs a new validator, corresponding client behavior, and test vectors; configuration is not an arbitrary programming language. Per-customer limits were intentionally deferred to avoid reservations/account-like state beyond the exercise.

### Lifecycle, retries, and persistence

`PizzaServer` uses one mutex across validation, duplicate checks, mutations, persistence, and kitchen transitions. Requests return independent JSON snapshots, never pointers to unlocked mutable order state. A mutation builds a proposed value-state copy, writes/flushed `orders.json.tmp`, replaces `orders.json`, then publishes the in-memory state before acknowledging success. A write failure leaves the old state in place. Storage records original submissions, accepted snapshots, revisions, FIFO queue order, eligibility times, and cooking deadlines. Bad snapshots stop startup rather than silently dropping orders. This is process-restart recovery, not a claim of full power-loss/database durability.

A submission lost after commit is replayed with the same GUID: the stored original request matches and code 4 returns the current order, without enqueueing or charging again. Reusing the ID for a different original payload is an `idempotency_conflict`. Updates require the observed revision and retain the last successful update's input revision and payload. Replaying that latest update returns the current order even if cooking has since started; stale/different updates do not overwrite newer changes. Cancel and completion are idempotent in their respective terminal states. No unbounded history of old update responses is retained.

If cancellation races kitchen start, whichever holds the mutex first wins: either the cancellation is saved and removed from the queue, or the cooking transition is saved and cancellation returns code 5. There is no state in which cancellation succeeds while the kitchen also starts that order. A successful update preserves queue position and its original start eligibility time.

The listener ticks the kitchen about every 500 ms, independently of incoming connections. Defaults are a **10-second minimum pending window**, **three concurrent orders**, and **60 seconds per pizza** within an order. FIFO orders move `Not Started -> Started -> Ready for Pickup`. ID 8 simulates restaurant handoff, moving ready orders to `Delivered`; it is exposed only as an unauthenticated local debug action. The service prints the accepted final cents when cooking starts to simulate a charge. Absolute UTC deadlines survive restart; an already-started order can become ready on the next tick, while pending orders begin when the restarted kitchen has room. Clock changes affect this simulation. Active/ready sets are derived from status rather than duplicating the proposed unused delivery queue.

### Deliberate cuts and next work

- No WPF builder yet. No auth, payment provider, real kitchen/delivery, admin UI, TLS, containers, or deployment work. UI polish is lower priority than tested totals and state transitions.
- Windows-only APIs avoid external dependencies. Storage rewrites the complete snapshot under a single lock; suitable for a small exercise, not a high-throughput service. No paging, multi-process writers, backups, or automatic corruption recovery.
- No per-customer promo limits, menu hot reload, server quote endpoint, historical update-operation log, or queue-aware ETA. Very old update retries may return a conflict rather than their original success response, but cannot undo later work.
- Next: build the WPF client and run shared pricing vectors in C#, add the remaining wire/fault-injection tests, then consider transactional storage and richer request IDs if workload requires them. This implementation was completed over multiple assistant turns; no claim is made that the full exercise was finished inside three hours.

## Tests: chosen invariants and gaps

The tests prioritize errors that would charge the wrong amount, duplicate an order, lose an acknowledged change, or permit cancellation after cooking starts:

- Golden cents totals for quantities, multiple lines/categories, tax/tip rounding, both promos, strict SAVE5 eligibility, both conflict orders, and unknown/duplicate promos.
- Data-driven additions, fractional-cent topping rounding, discount clamping, invalid quantities/codes/tips/toppings, numeric bounds, malformed JSON, and stale menus.
- Dispatch, snapshots/description, anonymous customer separation, terminal-state restrictions, FIFO/capacity, update queue position, and persisted deadlines.
- Submission/update/cancel/completion retries, original submissions after updates, changed-payload conflicts, recovery after restart, corrupt-storage refusal, immutable historical totals, and revision-limit recovery.
- Forced storage failures for placement, update, cancellation, kitchen start, and completion verify rollback before a later retry.
- Eight simultaneous duplicate submissions yield one order; 20 cancel/start race iterations allow only a cancelled order or a started order with rejected cancellation.
- Real TCP tests cover malformed/missing input, fragmented requests, ignored forged totals/status, separate-process restart, and a placement whose response is discarded before retry.

Not covered exhaustively: every branch/error string, the future C# client, full parser fuzzing, every socket timeout/connection-cap path, slow readers, graceful console shutdown, heavy load, OS clock jumps, Win32 builds, and power failure at each filesystem instruction. The tests do not establish exhaustive race interleavings or a coverage percentage.

## Required changes to the planned WPF client

- Use integer cents/basis points, the rounding/tip rules above, and the shared vectors; no `float` money. Fetch version, tax, and promo definitions through GetMenu.
- Mirror function/status/result numbers exactly; **PlaceOrder is 3, not 2**. Send LF-delimited UTF-8 JSON on one connection per call. Keep calls asynchronous and prevent duplicate UI submissions, while still retaining retry identifiers.
- Preserve the original placement request while its outcome is uncertain; a timeout is not permission to change that payload or generate a new GUID. Store the returned revision and use `expectedRevision` for updates.
- Replace displayed accepted orders with response snapshots and read amounts from nested `totals`. On stale menus or revision conflicts, fetch/reconcile before retrying edits.
- Add line removal. Generate a draft order description on request before placement; stored orders already have `description` for receipts.
- Edit a draft copy in the pizza popup so closing without saving does not mutate bound items. Make Update call ID 5, not PlaceOrder.
- Stop polling cancelled/delivered orders; fetch full orders when details may have changed, not only status. Treat customer ID as grouping rather than account sign-in.

## AI usage note and submission checklist

GitHub Copilot was used to review the supplied plan, implement the server/tests, and draft this documentation. Generated output required correction: the first WinRT JSON implementation omitted collection definitions and collided with the Windows `GetObject` macro; review also found that revision limits needed space for kitchen transitions. The agent ran the builds, golden vectors, concurrency/rollback tests, and network retry/restart tests rather than treating generated code as validated.

**Before submission, add your own personally verified invariant and how you checked it; agent-run tests are not a claim of human verification.** One suggested independent check is the worked $31.48 example above: calculate its topping subtotal, discount, tax, and after-tax percentage tip yourself, then compare the API response. Run and inspect the cancel/start test as another candidate. Replace this checklist with an honest account of what you actually verified.

Publish the source to a Git host and provide its URL. Hosting/commits were not created by this implementation. Include the future builder's run instructions when it exists; do not describe the server-only repository as the completed end-to-end exercise.
