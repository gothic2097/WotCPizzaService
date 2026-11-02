# Server

- server test harness for pizza orders app
- C++20, Windows only; standard library, Winsock, and Windows SDK JSON, no third-party dependencies
- console app
- simple TCP connection for interfacing with clients
- simple JSON data storage for both order data and configurations
- orderIds are random GUIDs (saved as strings), created on the client and sent in when placing an order (this is to help prevent accedental double orders)
- customerIds are sent from the client, they can be any integer from 0 to 4,294,967,295; these group anonymous orders, not accounts or authentication
- money uses Money = int64_t in cents; rates use integer basis points (10,000 = 100%, 650 = 6.5%), not floating point
- order inputs allow 1-100 line items, count 1-100 per line, and at most 1000 pizzas total; supported amounts are bounded to 1,000,000,000 cents

## General Result Enum - must be same on server and client
```
enum ResultCode
{
	FAIL = -1,
	SUCCESS = 0,

	CONNECTION_ERROR,
	CONNECTION_INVALID_CALL,
	CONNECTION_INVALID_INPUT,

	ORDER_ALREADY_CREATED,
	ORDER_ALREADY_STARTED,

	PROMO_INVALID,
	PROMO_VALID
}
```

## Order Status - must be same on server and client
```
enum OrderStatus
{
	NOT_STARTED = 0,
	STARTED,
	READY_FOR_PICK_UP,
	DELIVERED,
	CANCELLED
}
```

## Promo Validation Checks - must be same on server and client
```
enum PromoValidationType
{
	ONE_PROMO_PER_ORDER,
	NOT_VALID_WITH_OTHER_PROMOS,
	N_PER_CUSTOMER,
	TOTAL_OVER_N
}
```

- ONE_PROMO_PER_ORDER means each code can appear only once; this is enforced for every promo. N_PER_CUSTOMER keeps its enum value but is deferred and rejected in configuration. TOTAL_OVER_N uses the undiscounted subtotal and a strict greater-than check.

## Promo Object
```
struct PromoValidation
{
	PromoValidationType type;
	
	// if the validation requires an array, for example an array of other promo codes that make this promo invalid if present
	vector<string> arrayOfStrings;

	// minimum undiscounted subtotal in cents for TOTAL_OVER_N; the subtotal must be strictly greater
	Money thresholdCents;
}

struct Promo
{
	string code;
	vector<PromoValidation> validations;
	
	// integer basis points if isPercentOff, otherwise cents; JSON uses discountBasisPoints or discountCents
	int64_t discount;
	
	// if true, discount is in basis points, not cents
	bool isPercentOff;
}
```

## Topping Object
```
struct ToppingCategoryDisplay
{
	// SKU of the topping category from the data
	uint32_t code;

	// name from the data
	string name;

	// related topping codes and names, from data; codes are unique within each category
	map<uint32_t, string> toppings;

	// cost per topping in basis points of the pizza base cost, from data
	int64_t costBasisPoints;
}

struct ToppingCategoryOrder
{
	uint32_t toppingBaseCode;
	vector<uint32_t> toppings;
}
```

## Pizza Object
```
struct PizzaDisplayItem
{
	// SKU for the base pizza size/type will corrispond to Small, Medium, Large, etc in the data
	uint32_t code;

	// name from the data
	string name;

	// cost in cents from data
	Money costCents;
}

struct PizzaOrder
{
	uint32_t pizzaCode;

	// array of topping categories, each containing selected topping codes
	vector<ToppingCategoryOrder> toppings;

	// count of identical pizzas for this line item
	uint32_t count;
}
```

## Customer Order
```
struct RequestedCustomerOrder
{
	// This is a GUID and will be generated on the client
	string orderId;

	// version from GetMenu; new placements, updates, and promo previews reject stale versions
	string menuVersion;

	// array of pizzas ordered
	vector<PizzaOrder> pizzas;

	// array of requested promo codes
	vector<string> promoCodes;

	// entered manually on the client in dollar mode; must be zero in percentage mode
	Money tipCents;

	// 0-10,000 basis points; applied to the discounted subtotal plus tax, excluding tip
	// must be zero in dollar mode; both fields zero means no tip
	int64_t tipBasisPoints;
}

struct Totals
{
	Money subtotalCents;
	Money discountCents;
	Money totalBeforeTaxAndTipCents;
	Money taxCents;
	Money tipCents;
	Money totalAfterTaxAndTipCents;
}

struct CustomerOrder
{
	uint32_t customerId;
	RequestedCustomerOrder request;
	RequestedCustomerOrder originalRequest;
	OrderStatus status;
	Totals totals;
	string description;
	uint64_t revision = 1;
	uint64_t lastUpdateRevision = 0;
	int64_t readyAtUnixSeconds;
	uint32_t cookingSeconds;
}
```

- CustomerOrder above is the internal stored model. JSON responses flatten request fields into customerOrder and add customerId, status, revision, description, timeTillReady, and a nested totals object. originalRequest, lastUpdateRevision, readyAtUnixSeconds, and cookingSeconds are not returned.
- timeTillReady is cooking duration for NOT_STARTED orders (not a queue wait estimate), remaining seconds for STARTED orders, and zero otherwise. The calculated tip is totals.tipCents; the request's tipCents stays zero in percentage mode.

## Menu
```
struct Menu
{
	string version;
	map<uint32_t, PizzaDisplayItem> pizzas;
	map<uint32_t, ToppingCategoryDisplay> toppings;
	map<string, Promo> promos;
	int64_t taxBasisPoints;
	uint32_t kitchenCapacity;
	uint32_t timePerPizzaSeconds;
	uint32_t startDelaySeconds;
}
```

- GetMenu returns the JSON configuration under menu, using menuVersion, arrays for pizzas/toppings/promos, and a settings object as shown below, rather than serializing the internal maps directly.

## Order handling

- functions called by HandleClient must be thread safe
- assume multiple clients ordering, updating, and checking orders in parallel
- one mutex serializes order changes, kitchen transitions, and persistence; callers receive independent JSON snapshots, not pointers into unlocked state
- successful mutations are saved before acknowledgement; a write failure leaves the old state in place and returns FAIL/storage_error
- orderIds are globally unique; retry the identical original submission with the same customerId and GUID to receive ORDER_ALREADY_CREATED and the current order, without re-enqueueing or repricing. A changed payload returns FAIL/idempotency_conflict; another customer's ID returns FAIL/not_found.
- GUIDs are normalized to lowercase; promo codes and topping selections are sorted for retry comparison, but pizza line order remains significant
- Handle converts validation errors to resultCode, error, and message fields. Invalid order input uses CONNECTION_INVALID_INPUT, invalid promos use PROMO_INVALID, and stale menus/revisions use FAIL with stale_menu/revision_conflict. A rejected lifecycle update/cancel returns ORDER_ALREADY_STARTED, message, and the current order.

```
// 1:1 mapping of int to interface method - must be same on server and client
// Defined outside PizzaServer in Models.h.
enum class FunctionId
{
	GetMenu = 0,
	GetOrder,
	GetAllOrders,
	PlaceOrder,
	GetOrderStatus,
	UpdateOrder,
	CancelOrder,
	IsPromoValid,
	CompleteOrder
}

class PizzaServer
{
public:
	// loads menu.json and any orders.json from directory; main defaults to the executable folder
	// --data can select another directory; the injected clock supports deterministic tests
	explicit PizzaServer(const filesystem::path& directory, function<int64_t()> clock = UnixSeconds);

	// dispatches FunctionId and validates Input; wraps results and error messages as JSON
	JsonObject Handle(const JsonObject& request);

	// saves the current state to orders.json, thread safe; mutations also save automatically
	void Save();

	// returns a copy of the menu generated from data, always assume SUCCESS
	JsonObject GetMenu() const;

	// returns SUCCESS and the current order, or FAIL if not found
	JsonObject GetOrder(uint32_t customerId, const string& orderId);

	// returns SUCCESS, status, revision, and timeTillReady, or FAIL if not found
	JsonObject GetOrderStatus(uint32_t customerId, const string& orderId);

    // returns SUCCESS and the array of orders, or FAIL if customer not found
	JsonObject GetAllOrders(uint32_t customerId);

	// identical original submissions return ORDER_ALREADY_CREATED and the current order; changed submissions conflict
	// validates menuVersion, pizzas, toppings, quantities, tips, and promo codes
	// computes totals from server data; ignores client-supplied totals/status
	// saves before returning SUCCESS and the new order snapshot
	JsonObject PlaceOrder(uint32_t customerId, const RequestedCustomerOrder& request);

	// iff the order is still in NOT_STARTED status and expectedRevision matches, revalidate and reprice
	// does not remove the order from the queue or change its place in the queue.
	// preserves the original start eligibility time; increments revision after a successful update
	// replaying the latest successful update with its original expectedRevision/payload returns the current order without another change
	// otherwise a non-editable order (including CANCELLED) returns ORDER_ALREADY_STARTED and the current order
	// stale revisions return FAIL/revision_conflict; orderId is taken from request, not a second parameter
	JsonObject UpdateOrder(uint32_t customerId, const RequestedCustomerOrder& request, uint64_t expectedRevision);

	// iff the order is still in NOT_STARTED status, this will remove the order from the queue.
	// mark the order as CANCELLED status.
	// retains the order and its GUID; repeating a successful cancellation returns SUCCESS
	// if the order has already started then return ORDER_ALREADY_STARTED, return SUCCESS otherwise; includes current order
	JsonObject CancelOrder(uint32_t customerId, const string& orderId);

	// validates the draft request with code included once; no order is saved and there is no per-customer usage state
	// possible checks based on PromoValidationType data; may be multiple per promo. Each promo can be applied only once per order.
	// must check reverse case too, ie if promoCode == "ILOVEPIZZA" and it is valid with other promos, but SAVE5 is already on the order and it is not valid with other promos, then ILOVEPIZZA is invalid.  
	// returns PROMO_VALID and hypothetical totals; promo errors return PROMO_INVALID with error/message, not a validation-type integer
	// invalid draft fields or stale menuVersion can instead return CONNECTION_INVALID_INPUT or FAIL
	// the network Input still requires customerId, plus customerOrder and promoCode
	JsonObject IsPromoValid(const RequestedCustomerOrder& request, const string& code) const;

	// Debug method to simulate physically picking up an order. READY_FOR_PICK_UP changes to DELIVERED; the order is retained.
	// repeating completion of a DELIVERED order succeeds; any other state returns FAIL/invalid_state
	JsonObject CompleteOrder(uint32_t customerId, const string& orderId);

	// Testing/Simulation method
	// called about every 500 ms by ConnectionHandler::Start, even without client traffic
	// finish STARTED orders whose persisted readyAtUnixSeconds deadline has passed
	// start eligible FIFO orders while below capacity; defaults: 10-second pending window, capacity 3, 60 seconds per pizza
	// persist transitions before publishing them; log accepted final cents to simulate a charge, without repricing
	void UpdateServer();

private:
	struct Entry { CustomerOrder order; int64_t eligibleAt; };
	struct State
	{
		// globally keyed by orderId; customerId is stored on the order
		map<string, Entry> orders;
		deque<string> queue;
	};
	filesystem::path storagePath;
	Menu menu;
	string menuJson;
	State state;
	mutex mutex;
	function<int64_t()> clock;

	// restores original submissions, snapshots, revisions, deadlines, and FIFO queue; corrupt storage stops startup
	void Load();
	// writes and flushes temporary JSON, replaces orders.json, then publishes in-memory state
	void Commit(State&& next);
	JsonObject Serialize(const State& source) const;
	const CustomerOrder& Find(uint32_t customerId, const string& orderId) const;
	JsonObject OrderResponse(const CustomerOrder& order, ResultCode code = SUCCESS) const;
	CustomerOrder Price(uint32_t customerId, const RequestedCustomerOrder& request) const;
}

// Pricing.cpp free functions replace the separate float-based price/tip/tax helpers.
Totals CalculatePrice(const Menu& menu, const RequestedCustomerOrder& request);
string DescribeOrder(const Menu& menu, const RequestedCustomerOrder& request, const Totals& totals);
uint32_t PizzaCount(const RequestedCustomerOrder& request);
```

- CalculatePrice rounds each topping down in cents before adding it to the base price, then multiplies by quantity and sums line items. Duplicate toppings/categories are rejected.
- Check promos against the undiscounted subtotal; apply compatible codes in lexical order to the running discounted subtotal. Round each percentage discount down and cap flat discounts at the remaining subtotal.
- Tax is discounted subtotal * taxBasisPoints / 10,000, rounded down. Percentage tip is (discounted subtotal + tax) * tipBasisPoints / 10,000, rounded down. Total is discounted subtotal + tax + tip; tips are not taxed.
- Cooking/ready membership is derived from status; there is no separate kitchenHeap, readyQueue, or customerPromosMap. Absolute deadlines survive restart; pending orders wait for the restarted kitchen. An exclusive file handle prevents multiple server processes writing the same store.

## Communication with client

- Simple TCP, default 127.0.0.1:5050; --port changes the port
- No security or load balancing (etc)
- Must handle multiple clients: thread per connected client, up to 64 active workers; excess connections are closed
- Process: Server listens > Client connects and provides an ID mapped to interface and required inputs > Server calls requested interface and returns result to client > Server closes connection to that client and frees the related thread
- Input and output are handled with UTF-8 JSON strings using Windows SDK JSON helpers; one complete JSON object followed by LF per connection, with partial reads/writes handled
- Requests are bounded to 1 MiB; receive/send deadlines are five seconds each, excluding server processing time. Examples below are formatted for reading; minify them onto a single line before sending.
- GetMenu accepts just FunctionId; all other calls require Input.customerId. IDs 1/4/6/8 also require orderId; 3/5/7 require customerOrder; 5 adds expectedRevision; 7 adds promoCode.

```
class ConnectionHandler
{
public:
	// PizzaServer's constructor has already loaded data
	// listens until stop is set by the console control handler, while also ticking the kitchen
	// creates a worker for each accepted connection; joins completed workers and all remaining workers on shutdown
	// mutations save inside PizzaServer before success is returned, not when the connection finishes
	void Start(PizzaServer& server, uint16_t port, const atomic<bool>& stop);
}

// private implementation helper in ConnectionHandler.cpp, not a class member
// reads one request, calls server.Handle, sends one result line, and closes the connection
// malformed input uses CONNECTION_INVALID_INPUT; unknown calls use CONNECTION_INVALID_CALL
// transport failures use CONNECTION_ERROR when a reply is possible; domain/storage failures retain their result codes
void HandleClient(SOCKET clientSock, PizzaServer& server) noexcept;
```

## JSON string input example - PlaceOrder
```json
{
	"FunctionId": 3,
	"Input":
	{
		"customerId": 42,
		"customerOrder":
		{
			"orderId": "00000000-0000-0000-0000-000000000001",
			"menuVersion": "1",
			"pizzas":
			[
				{
					"pizzaCode": 1,
					"toppings":
					[
						{
							"toppingBaseCode": 0,
							"toppings":
							[
								2,
								5
							]
						},
						{
							"toppingBaseCode": 1,
							"toppings":
							[
								1
							]
						}
					],
					"count": 2
				}
			],
			"promoCodes":
			[
				"ILOVEPIZZA"
			],
			"tipCents": 0,
			"tipBasisPoints": 1500
		}
	}
}
```

## JSON string output example - GetAllOrders
```json
{
	"resultCode": 0,
	"customerOrders":
	[
		{
			"orderId": "00000000-0000-0000-0000-000000000001",
			"menuVersion": "1",
			"customerId": 42,
			"status": 3,
			"revision": 4,
			"timeTillReady": 0,
			"pizzas":
			[
				{
					"pizzaCode": 1,
					"toppings":
					[
						{
							"toppingBaseCode": 0,
							"toppings":
							[
								2,
								5
							]
						},
						{
							"toppingBaseCode": 1,
							"toppings":
							[
								1
							]
						}
					],
					"count": 2
				}
			],
			"promoCodes":
			[
				"ILOVEPIZZA"
			],
			"tipCents": 0,
			"tipBasisPoints": 1500,
			"totals":
			{
				"subtotalCents": 1778,
				"discountCents": 177,
				"totalBeforeTaxAndTipCents": 1601,
				"taxCents": 104,
				"tipCents": 255,
				"totalAfterTaxAndTipCents": 1960
			},
			"description": "2 x Medium pizza (base $7.00): extra cheese ($0.56), green peppers ($0.56), chicken ($0.77)\n  $8.89 each; line $17.78\nSubtotal: $17.78\nDiscount: $1.77 [ILOVEPIZZA]\nBefore tax and tip: $16.01\nTax: $1.04\nTip: $2.55\nTotal: $19.60\n"
		},
		{
			"orderId": "00000000-0000-0000-0000-000000000002",
			"menuVersion": "1",
			"customerId": 42,
			"status": 0,
			"revision": 1,
			"timeTillReady": 60,
			"pizzas":
			[
				{
					"pizzaCode": 1,
					"toppings":
					[
						{
							"toppingBaseCode": 0,
							"toppings":
							[
								2,
								5
							]
						},
						{
							"toppingBaseCode": 1,
							"toppings":
							[
								1
							]
						}
					],
					"count": 1
				}
			],
			"promoCodes":
			[
				"ILOVEPIZZA"
			],
			"tipCents": 0,
			"tipBasisPoints": 1500,
			"totals":
			{
				"subtotalCents": 889,
				"discountCents": 88,
				"totalBeforeTaxAndTipCents": 801,
				"taxCents": 52,
				"tipCents": 127,
				"totalAfterTaxAndTipCents": 980
			},
			"description": "1 x Medium pizza (base $7.00): extra cheese ($0.56), green peppers ($0.56), chicken ($0.77)\n  $8.89 each; line $8.89\nSubtotal: $8.89\nDiscount: $0.88 [ILOVEPIZZA]\nBefore tax and tip: $8.01\nTax: $0.52\nTip: $1.27\nTotal: $9.80\n"
		}
	]
}
```

## data

- JSON format, safed next to executable
- VS project needs to automatically copy this data to the same location as the executable when building
- Configuration is menu.json; the build also copies pricing-vectors.json. orders.json is server-generated state and is not overwritten by a build.
- GetMenu returns this configuration under its menu field. Use a new, never-reused menuVersion when changing configuration, then restart; accepted orders retain their saved totals and descriptions.

```json
{
	"menuVersion": "1",
	"taxBasisPoints": 650,
	"pizzas":
	[
		{
			"code": 0,
			"name": "Small pizza",
			"costCents": 500
		},
		{
			"code": 1,
			"name": "Medium pizza",
			"costCents": 700
		},
		{
			"code": 2,
			"name": "Large pizza",
			"costCents": 1200
		}
	],

	"toppings":
	[
		{
			"code": 0,
			"name": "Basic topping",
			"costBasisPoints": 800,
			"toppings":
			[
				{ "code": 0, "name": "pepperoni" },
				{ "code": 1, "name": "sausage" },
				{ "code": 2, "name": "extra cheese" },
				{ "code": 3, "name": "onions" },
				{ "code": 4, "name": "black olives" },
				{ "code": 5, "name": "green peppers" }
			]
		},
		{
			"code": 1,
			"name": "Premium topping",
			"costBasisPoints": 1100,
			"toppings":
			[
				{ "code": 0, "name": "bacon" },
				{ "code": 1, "name": "chicken" },
				{ "code": 2, "name": "garlic" },
				{ "code": 3, "name": "artichoke hearts" },
				{ "code": 4, "name": "sun-dried tomatoes" },
				{ "code": 5, "name": "pineapple" }
			]
		}
	],
	"promos":
	[
		{
			"code": "ILOVEPIZZA",
			"validations": [],
			"discountBasisPoints": 1000,
			"isPercentOff": true
		},
		{
			"code": "SAVE5",
			"validations":
			[
				{
					"type": 1,
					"arrayOfStrings":
					[
						"ILOVEPIZZA"
					]
				},
				{
					"type": 3,
					"thresholdCents": 2000
				}
			],
			"discountCents": 500,
			"isPercentOff": false
		}
	],

	"settings":
	{
		"kitchenHeapCapacity": 3,
		"timePerPizzaInSeconds": 60,
		"startDelaySeconds": 10
	}
}
```

## Server testing

- Dependency-free self-tests run with WotCPizzaServer.exe --self-test, including money-related helper tests and shared pricing-vectors.json cases
	- cover pricing boundaries, validation, retries, lifecycle transitions, persistence/rollback/restart, and concurrent duplicate submission and cancel/start races
	- 25 self-tests pass in Debug and Release x64; branch coverage has not been measured and not every branch is covered
- Integration testing for server/client functions uses tests/NetworkSmoke.ps1 against a separate server process
	- 20 checks pass, including fragmented requests, ignored client totals/status, lost-response retries, and process restart; exhaustive timeout/connection-cap/shutdown tests remain uncovered

# Client

- C# WPF on .NET 10; no third-party application dependencies. MSTest packages are used only by the test project.
- Proper MVVM pattern
  - MainViewModel coordinates orders; PizzaEditorViewModel owns a detached pizza editor. ObservableObject, RelayCommand, and AsyncCommand provide notifications and commands without an MVVM library.
  - Pricing handles draft calculations; PizzaService handles TCP/JSON. Only the external network and dialog boundaries use injected interfaces (IPizzaService and IClientDialogs).
- Proper bindings to data
  - UI messages are stored in Resources/Strings.resx and resolved by Text/LocExtension; window code handles presentation events and the refresh timer.
- Data driven based on the what is returned from GetMenu, data should be saved to the app only, not to file.
- Server authoritative, all data should come from the server
- enums will need to match the ones on the server 
- UI should be polished and visually pleasing, but simple
- Models/Contracts.cs defines C# wire DTOs and enums matching the server JSON contract. OrderEntry keeps the accepted CustomerOrder snapshot separate from its editable OrderRequest, expected revision, and conflict state.
- helper functions for calculating tip, tax, etc must be mirrored on the client to keep prices identical (note: server has final authority on price conflicts)
	- calculated dollar amounts always round down to the nearest cent; user-entered amounts with fractions of a cent are rejected rather than rounded.
	- use C# long cents and integer basis points, applying the server's per-topping, discount, tax, and tip rounding stages exactly; format displayed amounts as dollars with two decimal places
	- run the shared pricing-vectors.json cases in client tests; there is no QuoteOrder endpoint
	- use menuVersion, taxBasisPoints, promo definitions, and menu items from GetMenu; retain accepted totals/descriptions instead of repricing stored orders with a new menu

## Communication with server

- connects only when it needs
- ApiCalls builds requests matching each function in FunctionId; PizzaService sends the prepared JSON through one asynchronous SendAsync operation.
- converts objects into single-line UTF-8 JSON strings, appends LF, and sends one request per connection to 127.0.0.1:5050 by default; handles partial reads/writes and reads the response through LF
  - the interactive client uses this fixed endpoint; host/port are injectable in code and tests, not exposed in the UI.
  - requests are bounded to 1 MiB and responses to 16 MiB; a cancellable 15-second client deadline covers connecting, sending, waiting for processing, and receiving the response.
- converts the returned value back into a JSON object
- returned json strings contain a result code and a json struction with the expect return from the function.
	- functiosn such as GetAllOrders, GetOrder, PlaceOrder, UpdateOrder, CancelOrder, and CompleteOrder return full order snapshot(s) under customerOrder/customerOrders; replace the corresponding order on the client and update all related UI, especially totals, status, and revision
	- amounts come from the nested totals object; totals.tipCents is the calculated tip, distinct from the request's tipCents
	- GetOrderStatus returns only status, revision, and timeTillReady; fetch GetOrder when the revision changes and full details are needed
- new placements/updates/previews include menuVersion; UpdateOrder sends expectedRevision alongside customerOrder and keeps orderId inside customerOrder
- after a timeout, retry the same placement GUID and original payload, or the same update payload and expectedRevision; serialize once before the first attempt and resend that exact JSON. Do not generate a new GUID or silently change an uncertain request. Resolve its outcome before replacing/abandoning that draft while the app remains open.
  - requests remain in memory only. Warn before exiting with an unresolved mutation: it may already have been accepted, and closing loses its retry data. On the next sign in, reload the same customer's orders and check them before placing a replacement; no durable restart recovery is provided.
  - incomplete/malformed responses, including a successful mutation response missing its order snapshot, use the same Retry/Exit handling rather than being treated as acceptance.
- show error/message for invalid input or promos, rather than retrying endlessly. On stale_menu fetch GetMenu and recalculate for confirmation; on revision_conflict fetch GetOrder and reconcile edits. Treat ORDER_ALREADY_CREATED as a successful placement retry; apply the current snapshot on ORDER_ALREADY_STARTED.
- check the plan for the server to understand the input/output and purpose of each function.
- allow one asynchronous call at a time without blocking the UI, show a spinner if the call time is taking longer then 1 second; disable submission/edit actions while a request's outcome is unresolved

## UI Flow

- UI flows top to bottom, single column (some exceptions on some lines).
- After the app opens, all UI is disabled except the customerId textbox and sign in button (left aligned)
	- the user must enter a customer ID (integer 0-4,294,967,295, defaults to 0) then click "sign in"; this only selects anonymous order grouping, not authentication
	- Clicking sign in calls GetMenu to get all the initial data needed
		- then call GetAllOrders to populate previous/current orders; FAIL/not_found means the customer has no stored orders, not a connection failure
	- If there is a failure to connect to the server, show a popup message that blocks any other UI interaction. Message should explain the server couldn't be reached. Should give the option to retry or exit the app. Retry reattempts the call, a fail results in the message popup again.
		- This is the same way we will handle transport failures for other calls; retries preserve the original request identifiers and payload. Domain errors such as stale menus/revisions, invalid promos, and already-started orders use the error handling above instead.
	- After the successful call, the customerId textbox will disable with the ID locked in, and the sign in button disableds. All other UI enables.
- To the far right (right aligned) of the sign in button is a Label that says "Auto Refresh (mins)" then a textbox with the default value of 5, then to the right of that a button that says "Refresh Now".
	- validate the interval as a whole number from 1 to 1,440 minutes; an invalid setting shows a message and disables the normal automatic schedule.
	- Every x mins the app should automatically call GetOrderStatus on placed orders that are neither DELIVERED nor CANCELLED; if the revision changed, fetch GetOrder to refresh the full snapshot without silently overwriting unsaved edits
	- Clicking Refresh Now does the same updating and restarts the timer.
	- after a successful PlaceOrder or UpdateOrder (including ORDER_ALREADY_CREATED), dismissing the success dialog resets the next refresh to 15 seconds. After that refresh completes, the configured interval resumes; the minutes textbox remains unchanged. Refresh Now also consumes this one-time interval.
- Under the customerId/sign in, a large orange button that says start new order
	- if the user clicks it, it will start a new order or, after confirmation, replace the current new order (clear/default its data)
	- this will also generate the orderId GUID and assign it to the new order, this orderId will not be editable after this point
- Next, the user will see a dropdrown that shows all previous and current orders
	- line items in the drop down will show the order Id then the current status (human readable), ie "Not Started", "Started", "Ready for pick up", etc.
	- There can be only one new order at a time, so it will be last in the list and the line item will show the newly created orderId and the status will be "New".
	- Selecting a placed order will call GetOrder to refresh its full snapshot and reset the auto refresh timer; selecting the local "New" draft must not query an order that is not stored yet
	- Selecting the order will change what is displayed below in the pizza list
	- retain unsaved edits, including invalid tip text, when switching orders or refreshing snapshots. If the revision changes, disable editing/submission and show "Reconcile changes". For a still-pending order, the user may keep the edits against the latest revision or discard them; edits to a started/closed order cannot be resubmitted.
	- for an order using an older menu version, show its saved receipt and accepted totals rather than repricing historical details. A pending order requires explicit "Review with current menu" confirmation before editing; unavailable items must be replaced/removed and Update Order must be clicked to submit the reviewed price.
- Under the drop down is the selected order from the dropdown, this takes up the majority of the window and has scroll bars when needed.
	- An ItemsControl in a ScrollViewer displays ObservableCollection<PizzaLine>, rebuilt from the selected OrderRequest and current menu. Each line contains its index, a formatted quantity/size/unit/line-total title, and base/topping price details.
		```
		record PizzaLine(int Index, string Title, string Details);
		record PizzaOrder(uint PizzaCode, uint Count, ToppingSelection[] Toppings);
		record ToppingSelection(uint ToppingBaseCode, uint[] Toppings);
		```
	- ObservableCollection<PizzaLine> is repopulated when the selected order changes, a new order starts, or its draft is recalculated; the historical receipt replaces this list for an older menu version.
	- editing controls are also disabled while busy, while a revision conflict is unresolved, or until an older-menu pending order is explicitly repriced for review.
	- Next to each top level entry (Pizza) iff the order status is new or NOT_STARTED then add a button that says Edit
		- Clicking on edit opens the Add/Edit Pizza Popup window (see further below) 
			- This should send a draft copy of the pizza object to the Add/Edit Pizza Popup window so that it can be modified without changing the original
			- Only after Add/Update is confirmed should the original Pizza object receive the new values; closing with x discards the draft
	- Next to each top level entry (Pizza) iff the order status is new or NOT_STARTED also add a Remove button; removing a line recalculates totals, and an empty draft cannot be submitted
- Next is a button that says "Add Pizza", clicking this will open the Add/Edit Pizza Popup window (see further below)
	- If the user selects an order from the drop down that is neither new nor in the status of NOT_STARTED, this button should be disabled with a tooltip that says "Unable to update this order"
- Next under the Add Pizza Button will be set of labels showing the discounted total before tax and tip
- Under that is the tip options
	- default to Custom $ with zero tip; dollar/percentage text uses a decimal point regardless of the host currency setting.
	- 5 radio buttons
		1. 15%, selecting this clears the textbox for custom dollar amount and textbox for custom percent
		2. 18%, selecting this clears the textbox for custom dollar amount and textbox for custom percent
		3. 20%, selecting this clears the textbox for custom dollar amount and textbox for custom percent
		4. Custom % with a textbox next to it for the custom percent, selecting this clears the textbox for custom dollar amount
		5. Custom $ with a textbox next to it for the custom dollar amount, selecting this clears the textbox for custom percent
	- to the right of the radio buttons is a label showing the final tip, calculated against the discounted total plus tax (excluding tip), rounded down to the nearest cent
		- Note: when placing or updating an order: 
			- if a percent is selected then assign integer basis points to tipBasisPoints (15% = 1500) and assign 0 to tipCents; supported percentages are 0-100% in 0.01 percentage-point increments
			- if a custom dollar is selected then convert it to whole cents for tipCents and assign 0 to tipBasisPoints; reject negative values or amounts with fractions of a cent
- Next is UI for the promos
	- A textbox to enter a promo code and a button to "Apply"
		- this will need to call the IsPromoValid function with Input.customerId, the full draft customerOrder (including GUID/menuVersion), and promoCode; handle PROMO_VALID/PROMO_INVALID and general input/stale-menu failures
		- trim surrounding whitespace but keep promo codes case-sensitive. If valid, compare the server's hypothetical totals with the local calculation before adding the promo; a mismatch shows an error and blocks submission for that draft state.
		- revalidate selected promos whenever the draft changes; SAVE5 requires undiscounted subtotal strictly over $20, and conflicts must be checked regardless of entry order. Invalid selections show a clear message and block submission.
	- under the textbox is an ItemsControl with all the current "applied" promos
		- next to each should be an X button
			- clicking the X button removes the promo from the requested order
- Under the promos is a label showing the tax. The taxBasisPoints rate comes from GetMenu data and is applied to the discounted total before tax and tip, rounded down to cents
- Under the tax line should be the final total with both tax and tips summed
- Below the final total is an orange button that will say "Place Order" for a new order, or "Update Order" for an order that has a status of NOT_STARTED
	- If the user selects an order from the drop down that is neither new nor in the status of NOT_STARTED, this button should be disabled with a tooltip that says "Unable to update this order"
	- For a new order call PlaceOrder (FunctionId 3), including the client-generated orderId and menuVersion. For a NOT_STARTED order call UpdateOrder (FunctionId 5) with the edited request and expectedRevision from the last accepted snapshot. Update the UI from the response.
	- If the result is ORDER_ALREADY_CREATED, this can be considered a success case, replace the old order data with the new CustomerOrder in the response, then update the UI to make sure it reflects the status change
	- After a success, should a popup dialog that says "Order Created!" for placement or "Order Updated!" for an update, and has an Ok button.
- If the status is NOT_STARTED, then also show a link style button that says "Cancel Order" aligned right and under the orange button, else hide this.
	- clicking this popups a confirmation dialog (yes/no), if yes then call the CancelOrder function and apply its returned snapshot; ORDER_ALREADY_STARTED means the kitchen won the race and the cancellation did not happen
- Show a link style button aligned left (same row as the cancel link) that says "See order" for a local draft or "See receipt" for a placed order, including NOT_STARTED orders
	- clicking this should popup window filled with an uneditable textbox (scroll bars if needed). The text box should have an easy to read and copy of the order that clearly shows each pizza, its toppings, the cost of each item, count, totals, tip, tax, etc. Generate this locally for a draft, and use the server's saved description for an accepted order.
	- popup just has an x button for closing.
- Show a "Simulate pickup" link for READY_FOR_PICK_UP orders; this restaurant/debug action calls CompleteOrder and applies the returned DELIVERED snapshot.
- Closing the app warns before discarding unsaved edits or losing an unresolved mutation's in-memory retry data.

### Add/Edit Pizza Popup window

- PizzaEditorWindow uses PizzaEditorViewModel to edit a detached PizzaOrder. SizeChoice, CategoryChoice, and ToppingChoice hold the bound selections; Build returns a fresh PizzaOrder only when Add/Update is confirmed.
- Shows a dropdown with all the pizza types, the user select the one they want, they can only select one
	- prices shown to the right of the name
	- changes the pizza base in the pizza object
- Shows a dropdown to filter which toppings (category) to show
	- shows the dollar price (calculate based on the cost for the category against the pizza base selected) to the right of the category name
- Shows a list of the toppings in the selected category
	- left of each is a checkbox, clicking it adds it to the pizza object
	- switching category filters preserves checked toppings; changing pizza size recalculates the category topping prices and line totals.
- Shows a textbox for the multiplier (number of pizzas of this configuration we want); validate integer 1-100 and the server's order-wide limits
- Shows the total before the multiplier and after
- Shows an orange button that says Add/Update Pizza
	- ideally it says Add if this is new, Update if we are editing a pizza
	- clicking this adds the new pizza or updates the selected pizza
- The window can also be closed with an x button
	- clicking x does not update or add the pizza

## Client testing

- WotCPizzaClient.Tests uses MSTest and test-only NuGet packages; the application remains dependency-free.
- Embed copies of the server's menu.json and pricing-vectors.json as test fixtures, not as an application fallback menu. Cover shared pricing results/rejections, rounding, tip precision, quantity/amount limits, promo conflicts, and thresholds.
- Test detached editor state, running totals, frozen placement/update retries, unresolved-request action blocking, malformed success responses, revision reconciliation, historical receipts, and preservation of invalid tip text.
- Test the one-time 15-second refresh after successful placement/update, restoration of the configured interval, and unchanged scheduling after rejected placement.
- Loopback TCP tests cover fragmented UTF-8 responses, incomplete frames, timeouts, cancellation, request bounds, and wire names.
- ServerIntegration tests launch separate C++ server processes on temporary ports with isolated data directories; use the neighboring Debug executable or PIZZA_SERVER_EXE, and skip when unavailable. Check pricing parity, idempotent placement/update/cancel, promo preview, and cooking/pickup transitions without modifying existing orders.
- Run with dotnet test or Visual Studio Test Explorer; collect coverage with dotnet-coverage. Full interactive UI/accessibility checks and exhaustive network/lifecycle races remain outside automated coverage.
