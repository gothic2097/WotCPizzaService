# WotC Pizza Service

A pizza order builder (C# WPF client) and order service (C++ server) for the Senior Software Developer take-home exercise.

Customers assemble an order, see a live total, and submit it to the restaurant. They can check status and, until the kitchen starts the order, update or cancel it. The service runs as a separate process and communicates over TCP with JSON.

## How to run

### Prerequisites
- Windows
- Visual Studio 2022 (or later) with C++ and .NET desktop development workloads
- .NET 10 SDK (for the client and tests)

### Order service (C++)
```powershell
# From the server folder, build (Debug or Release x64)
# Then run:
.\x64\Debug\WotCPizzaServer.exe
# or
.\x64\Release\WotCPizzaServer.exe
```
Listens on `127.0.0.1:5050` by default.  
`--port <n>` changes the port.  
`--data <dir>` selects a data directory (defaults to the executable folder).  
`--self-test` runs the built-in self-tests and exits.

The server loads `menu.json` on startup and persists orders to `orders.json` in the data directory.

### Order builder (C# WPF)
```powershell
cd client
dotnet run
# or open the solution in Visual Studio and run
```
Connects to `127.0.0.1:5050` by default. Enter a customer ID (0–4,294,967,295) and sign in. The ID only groups anonymous orders; there is no authentication.

### Tests
**Server self-tests** (no extra dependencies):
```powershell
.\x64\Debug\WotCPizzaServer.exe --self-test
# or Release
```
**Server network smoke tests** (PowerShell, against a running or launched server):
```powershell
.\tests\NetworkSmoke.ps1
```
**Client tests**:
```powershell
cd client
dotnet test
```
Client integration tests that launch the C++ server look for the neighboring Debug executable or the `PIZZA_SERVER_EXE` environment variable and skip when unavailable.

## Order service API

Simple TCP. One UTF-8 JSON object per connection, terminated by LF. Requests are bounded to 1 MiB; the server closes the connection after the response.

All requests include `FunctionId`. Most include `Input.customerId`. Responses always include `resultCode`. Domain results may include `error`, `message`, and order snapshots.

| FunctionId | Name            | Key inputs                                      | Notes |
|------------|-----------------|-------------------------------------------------|-------|
| 0          | GetMenu         | (none)                                          | Returns menu + settings |
| 1          | GetOrder        | customerId, orderId                             | Full order snapshot |
| 2          | GetAllOrders    | customerId                                      | Array of orders; `not_found` if none |
| 3          | PlaceOrder      | customerId, customerOrder (with orderId, menuVersion, …) | Idempotent on identical (customerId, orderId, payload) |
| 4          | GetOrderStatus  | customerId, orderId                             | status, revision, timeTillReady |
| 5          | UpdateOrder     | customerId, customerOrder, expectedRevision     | Only while NOT_STARTED; returns current order on conflict |
| 6          | CancelOrder     | customerId, orderId                             | Only while NOT_STARTED; idempotent |
| 7          | IsPromoValid    | customerId, customerOrder, promoCode            | Hypothetical totals; no side effects |
| 8          | CompleteOrder   | customerId, orderId                             | Debug: READY_FOR_PICK_UP → DELIVERED |

**Example PlaceOrder request** (minified on the wire; shown pretty-printed):
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
          "pizzaCode": 1,
          "toppings": [
            { "toppingBaseCode": 0, "toppings": [2, 5] },
            { "toppingBaseCode": 1, "toppings": [1] }
          ],
          "count": 2
        }
      ],
      "promoCodes": ["ILOVEPIZZA"],
      "tipCents": 0,
      "tipBasisPoints": 1500
    }
  }
}
```

**Result codes** (shared with client): `SUCCESS` (0), `FAIL` (-1), `CONNECTION_*`, `ORDER_ALREADY_CREATED`, `ORDER_ALREADY_STARTED`, `PROMO_VALID`, `PROMO_INVALID`.

Statuses: `NOT_STARTED` (0), `STARTED`, `READY_FOR_PICK_UP`, `DELIVERED`, `CANCELLED`.

## Assumptions

- **Money**: All amounts are integer cents (`int64_t` / `long`). Rates (tax, topping percentages, promo percentages, tip percentages) use integer basis points (10,000 = 100%). No floating-point arithmetic in pricing.
- **Tax base**: Tax is applied to the discounted subtotal (after promos, before tip). Tips are not taxed.
- **Tip base**: Percentage tips are calculated on (discounted subtotal + tax). Dollar tips are taken as-is. Both fields zero means no tip. Percentage and dollar modes are mutually exclusive.
- **Promo application**: Compatible promos are applied in lexical order to the running discounted subtotal. Percentage discounts are rounded down per promo. Flat discounts are capped at the remaining subtotal. `SAVE5` requires the *undiscounted* subtotal to be strictly greater than $20.00 and conflicts with `ILOVEPIZZA` (and vice versa).
- **Topping cost**: Each topping is priced as `floor(basePriceCents * costBasisPoints / 10000)` and added before multiplying by quantity. Duplicate toppings or categories on a line are rejected.
- **Rounding**: Every money calculation that can produce a fractional cent rounds down (toward zero) to the nearest cent at each stage (topping, discount, tax, tip).
- **Order ID**: Client-generated GUID string. Globally unique. Normalized to lowercase on the server. Used as an idempotency key together with the exact original payload.
- **Customer ID**: Any uint32. Only groups anonymous orders; no accounts or authentication.
- **Menu version**: Sent with every placement, update, and promo preview. Stale versions are rejected. Accepted orders retain the totals and description from the menu version that was current when they were placed/updated.
- **Kitchen simulation**: A background tick (≈500 ms) advances orders. Pending orders become eligible after a short delay (default 10 s). Capacity and time-per-pizza come from `menu.json`. Status transitions use absolute deadlines so they survive restart. No real kitchen or delivery integration.
- **Persistence**: Successful mutations are written to `orders.json` before the success response is sent. A write failure leaves the previous state intact and returns failure. An exclusive file handle prevents multiple server processes from sharing the same store.
- **Retries / timeouts**: Client serializes the request once and resends the identical JSON (same GUID and payload for placement; same payload + expectedRevision for update). Incomplete or malformed responses are treated as unresolved, not as success.
- **Update concurrency**: Updates require the expected revision. A stale revision returns a conflict plus the current order. Replaying the exact last successful update is idempotent.
- **Cancel race**: If the kitchen starts the order between the client’s decision and the server’s processing, the server returns `ORDER_ALREADY_STARTED` and the current order; the cancellation does not occur.
- **Limits**: 1–100 line items, 1–100 quantity per line, ≤1000 pizzas total, amounts bounded to 1,000,000,000 cents. Requests limited to 1 MiB.
- **What was left open by the requirements** and decided as above: exact tax/tip base, rounding direction and stage, promo stacking order, whether tip is taxed, client-generated vs server-generated order IDs, and how to handle a timeout after a successful mutation.

## Design write-up

### Money and pricing
Money is represented as integer cents. Rates use integer basis points. Pricing logic lives in a single place on the server (`CalculatePrice` and related helpers). The client mirrors the same integer arithmetic so the live total matches the server in the common case, but the server is authoritative: it ignores any client-supplied totals and returns the canonical snapshot. This guarantees the total the customer sees after submission is exactly what will be charged, and it keeps all rounding rules in one implementation that is covered by shared test vectors.

### Extensibility
Sizes, topping categories, individual toppings, tax rate, kitchen parameters, and promo codes are all data in `menu.json`. Adding a new size or topping is a data change only. Promo codes carry their own discount (basis points or cents) and a list of validation rules (`ONE_PROMO_PER_ORDER`, `NOT_VALID_WITH_OTHER_PROMOS`, `TOTAL_OVER_N`, etc.). A new promo with its own conditions or conflicts is added by extending the data and, if necessary, a new validation type; the core pricing and stacking loop does not change. Menu versioning ensures that already-accepted orders keep their original prices even if the menu later changes.

### Order lifecycle and guarantees
Orders move through `NOT_STARTED` → `STARTED` → `READY_FOR_PICK_UP` → `DELIVERED` (or `CANCELLED` while still `NOT_STARTED`). A background tick starts eligible orders when capacity allows and completes them when their absolute deadline passes. All mutations are serialized by a mutex and persisted before acknowledgement.

**Retry after timeout**: The client generates a GUID once, serializes the exact PlaceOrder JSON, and resends that same JSON on any timeout or incomplete response. The server treats an identical (customerId, orderId, payload) as a success and returns the existing order (`ORDER_ALREADY_CREATED`) without creating a duplicate or re-pricing. A changed payload for the same ID is rejected as a conflict. This gives the guarantee that a given placement attempt creates at most one order.

**Cancel at the moment the kitchen starts**: Both the cancel request and the kitchen tick take the same mutex. If the order is already `STARTED` (or later) when the cancel is processed, the server returns `ORDER_ALREADY_STARTED` together with the current order snapshot and does not cancel. The client applies the returned snapshot. The guarantee is that a cancel either fully succeeds while the order is still `NOT_STARTED` or is rejected with the latest state; there is no partial or double application.

Updates are similarly protected by revision checks and the same mutex.

### What was cut or faked
- No authentication, customer accounts, or real payments.
- Kitchen advancement is a simple timer + capacity model, not a real kitchen system.
- No delivery integration (the ready queue is vestigial).
- Persistence is a single JSON file with an exclusive lock, not a database.
- Client does not persist unresolved retry state across process restarts (it warns on exit and reloads on next sign-in).
- Branch coverage was not measured; some edge cases around connection limits, exact shutdown timing, and full UI accessibility are covered by design and smoke tests rather than exhaustive automation.
- `N_PER_CUSTOMER` promo validation exists in the enum but is rejected in configuration (deferred).

(Human written)
- Additionally the server in general is a fake, in a production situataion I would have used cloud services (AWS/Azure), something like AWS Lambdas mixed with DynamoDB/S3, SMS/SQS, etc. This would have also solved for security and attack vectors, plus given some load balancing.

### What would I built next or do if I had more time 

(Human written)
- I intended to add an additonal promo case, 1-per customer, but run into a design problem with when promos are applied. Currently they are applied when the order goes to kitchen, this is when I charge the customer, however for the `N_PER_CUSTOMER` type of promo this would have resulting in valiation issues. The change would be minor with AI help, but I'd rather avoid a last minute change and instead just call it out.
- I would have like to spend more time reviewing and cleaning up the AI's code. I knew there would be a decent amount of code and tried to mitigate review time by putting most of my time into the high level design (I find this a good way to help drive the AI so that it implements code using th patterns I want and not what it wants), but still even with a high level design I would like to of had more time to review and clean up the AI's code and code style.
- More manual testing would have been ideal.
- Finally more polish to the UI. This was called out in the requirements and not judged so I avoided devoting any more time to it then needed, but would have been nice to polish it more.

## AI usage note

(Human written)
- See the `Additional Documents` for my High Level Plan iterations and Copilot chat logs:
- I had AI help review my high level plan, I had it implement the server, I had it implement the client, and I had it implement the README.md (this section excluded).

### Flow
- I first used AI to refresh my memory on ways to have two apps on the same windows device communicate. I've used both TCP and pipes in the past, but needed a quick refersher before making the design descision. I asked Grok for an example of each. Ultimately going with TCP as it more closely matched how a client and server would talk in production.
- The inital high level plan I wrote by hand: `HighLevelPlanIter1_HumanOnly.md`
- I passed that document to Grok, along with the provided requirements doc. Grok provided feedback on where my plan did not meet requirements. That resulted in the following iterations until I was satisfied that Grok had found all the missing cases:
  - `HighLevelPlanIter2_HumanWithGrokSuggestions.md`
  - `HighLevelPlanIter3_HumanWithGrokSuggestions.md`
  - `HighLevelPlanIter4_HumanWithGrokSuggestions.md`
- I passed `HighLevelPlanIter4_HumanWithGrokSuggestions.md` to Copilot using a GPT6-Astra agent along with the requirements doc. Copilot analyzed the plan, called out some issues it saw plus options for me to choose from. Two notable examples:
  1. Using cents for currentcy instead of dollars, this allowed the use of integers instead of floats. I agreed to this suggestion.
  1. Having an API for the client to call to calculate the cost/tips/tax/etc so that we wouldn't have to mirror logic. This was a case I had debated when drafting my design, there are good arguements for both. Ultimately I stayed with my design. I wanted to reduce server calls and as long as the server remained the authority on what would be charged I felt the mirroring was acceptiable. HOWEVER, if we were more concerned with either maintainablity or preventing certain vectors of fraud then using the API call would become work the extra server calls. Regarless, Copilot recommened to add a shared testing vector between the client and server to make the duplication more maintainable, which I agreed with.
- I've included the entire Copilot chat thread for building the server in this file: `ChatLog_Server_Copilot_GPT6Astra.md`
- After the server was built and the tests passed, I had Copilot update the high level plan to include all the changes it had made so that the AI building the client would have the best understanding of what to do: `HighLevelPlanIter5_CopilotModifiedWithServerChanges.md`
- Using a new solution and new AI thread, I passed `HighLevelPlanIter5_CopilotModifiedWithServerChanges.md` to AI and had it build the client. 
- Once complete I also had it update the plan, just for consistency/documentation: `HighLevelPlanIter6_CopilotModifiedWithClientChanges.md`.  
- This is a copy of the Copilot chat thread: `ChatLog_Client_Copilot_GPT6Astra.md`

## Testing

**Invariants chosen for testing**
- Exact pricing and rounding stages (topping, discount, tax, tip) via shared vectors on both sides.
- Idempotent placement (identical retry succeeds and returns the same order; changed payload conflicts).
- Lifecycle transitions and the cancel/start race (order cannot be cancelled once started; current snapshot is returned).
- Revision conflicts on update.
- Persistence, rollback on write failure, and restart recovery of deadlines and queue order.
- Promo conflicts and the SAVE5 threshold.
- Input validation limits and stale menu rejection.

**Not fully covered in the timebox**
- Exhaustive connection-cap, timeout, and shutdown races.
- Full interactive UI and accessibility automation.
- Measured branch coverage.
- Durable client-side retry across process restarts.

Server self-tests and the network smoke script, plus the client MSTest suite (including loopback and server-integration tests), exercise the above invariants.
