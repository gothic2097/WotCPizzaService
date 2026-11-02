# Server

- server test harness for pizza orders app
- C++
- console app
- simple TCP connection for interfacing with clients
- simple JSON data storage for both order data and configurations
- orderIds are random GUIDs (saved as strings), created on the client and sent in when placing an order (this is to help prevent accedental double orders)
- customerIds are sent from the client, they can be any positive number >= 0

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
enum PromoValidatationType
{
	ONE_PROMO_PER_ORDER,
	NOT_VALID_WITH_OTHER_PROMOS,
	N_PER_CUSTOMER,
	TOTAL_OVER_N
}
```

## Promo Object
```
struct PromoValidation
{
	PromoValidatationType type;
	
	// if the validation requires an array, for example an array of other promo codes that make this promo invalid if present
	vector<string> arrayOfStrings;

	// if the validation requires a num value, for example the whole number of times a promo can be applied to a customer or the total of the order before tax required.
	float numValue;
}

struct Promo
{
	string code;
	vector<PromoValidation> validations;
	
	float discount;
	
	// if true, discount is a percent off, not a dollar value
	bool isPercentOff;
}
```

## Topping Object
```
struct ToppingBase
{
	// SKU of the topping from the data
	uint toppingCode;
}

struct ToppingDisplayItem : public ToppingBase
{
	// name from the data
	string name;
}

struct ToppingCategoryBase
{
	// SKU of the topping base from the data
	uint toppingBaseCode;	
}

struct ToppingCategoryDisplay : public ToppingCategoryBase
{
	// name from the data
	string name:

	// related toppings, from data
	vector<ToppingDisplayItem> toppings;

	// cost per topping as a percent of the pizza base cost, from data
	float percentCost;
}

struct ToppingCategoryOrder : public ToppingCategoryBase
{
	vector<ToppingBase> toppings;
}
```

## Pizza Object
```
struct PizzaBase
{
	// SKU for the base pizza size/type will corrispond to Small, Medium, Large, etc in the data
	uint pizzaCode;
}

struct PizzaDisplayItem : public PizzaBase
{
	// name from the data
	string name;

	// cost from data
	float cost;
}

struct PizzaOrder : public PizzaBase
{
	// array of basic toppings
	vector<ToppingCategoryOrder> toppings;

	// count of identical pizzas for this line item
	uint count; 
}
```

## Customer Order
```
struct RequestedCustomerOrder
{
	// array of pizzas ordered
	vector<PizzaOrder> pizzas;

	// array of requested promo codes
	vector<string> promoCodes;

	// either entered manually on the client, or assigned by the server when the order placed (based on the tipPercent)
	float tip;

	// if tipPercent > 0, then the server needs to use this to calculate the value for tip. Needs to be set to 0 if the customer enters a dollar tip manually instead of wanting a percent.
	float tipPercent;
}

struct CustomerOrder : public RequestedCustomerOrder
{
	uint id;
	OrderStatus status;

	// seconds remaining in the kitchen until done, total for the order
	uint timeTillReady;

	float totalBeforeTaxAndTip;
	float tax;
	float totalAfterTaxAndTip;
}

struct CustomerOrderReference
{
	uint customerId;
	string orderId;
}
```

## Menu
```
struct Menu
{
	vector<PizzaDisplayItem> pizzas;
	vector<ToppingCategory> toppings;
}
```

## Order handling

- functions called by HandleClient must be thread safe
- assume multiple clients ordering, updating, and checking orders in parallel

```
class PizzaServer
{
public:
	// 1:1 hash of int to interface method - must be same on server and client
	enum FunctionId
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

	// loads ordersMap from file, JSON format
	// loads data from file (used for the menu and various configurations), JSON format
	// load from the same folder as the executable
	void Load();

	// saves ordersMap to file, JSON format
	// save to the same folder as the executable
	// ideally this needs to be thread safe too
	void Save();

	// returns a copy of the menu generated from data, always assume SUCCESS
	const Menu& GetMenu();

	// returns SUCCESS and the current order, or FAIL if not found
	ResultCode GetOrder(uint customerId, string orderId, CustomerOrder*& pOrderOUT);

	// returns SUCCESS and the current OrderStatus, or FAIL if not found
	ResultCode GetOrderStatus(uint customerId, string orderId, OrderStatus& statusOUT);

    // returns SUCCESS and the array of orders, or FAIL if customer not found
	ResultCode GetAllOrders(uint customerId, vector<CustomerOrder*>& ordersOUT);

	// validates the orderId does not already exist, if it does then early exit and ORDER_ALREADY_CREATED is returned.
	// validates order against menu to verify pizzas and toppings are still valid, validates promo code(s) but does not apply them to the account.
	// calculate and assign totalBeforeTaxAndTip, tip, tax, and totalAfterTaxAndTip. Other than tip, do not take any values sent in from the client, all values need to come from server side data.
	// return FAIL if any errors, otherwise return SUCCESS.
	ResultCode PlaceOrder(uint customerId, const RequestedCustomerOrder& order);

	// iff the order is still in NOT_STARTED status, this will revalidate the order against the menu, (re)validates promo codes(s) but does not apply them to the account.
	// does not remove the order from the queue or change its place in the queue.
	// re-calculate and re-assign totalBeforeTaxAndTip, tip, tax, and totalAfterTaxAndTip. Other than tip, do not take any values sent in from the client, all values need to come from server side data.
	// if the order has already started then return ORDER_ALREADY_STARTED, SUCCESS otherwise
	ResultCode UpdateOrder(uint customerId, string orderId, const RequestedCustomerOrder& order);

	// iff the order is still in NOT_STARTED status, this will remove the order from the queue.
	// mark the order as CANCELLED status.
	// does not remove from ordersMap.
	// if the order has already started then return ORDER_ALREADY_STARTED, return SUCCESS otherwise
	ResultCode CancelOrder(uint customerId, string orderId);

	// given settings in our data, will determine if the requested promoCode is valid for the current order and customer (and valid if other promos are requested in the order already).
	// possible checks based on the value in the data for the promo matching the PromoValidatationType. May be multiple per promo. Each promo can be applied only once per order.
	// must check reverse case too, ie if promoCode == "ILOVEPIZZA" and it is valid with other promos, but SAVE5 is already on the order and it is not valid with other promos, then ILOVEPIZZA is invalid.  
	// returns PROMO_VALID or PROMO_INVALID, if invalid also returns the PromoValidatationType int value that invalidated the promo. If invalid due to a specific promo on already on the order, then also return that promoCode.
	ResultCode IsPromoValid(uint customerId, const string& promoCode, const CustomerOrder& order);

	// Debug method to simulate physically picking up an order. Iff the order is in READY_FOR_PICK_UP will change the order to DELIVERED and remove it from the readyQueue. Does not remove from ordersMap.
	void CompleteOrder(uint customerId, string orderId);

	// Testing/Simulation method
	// This should be called in our infinite loop in ConnectionHandler::Start to simulate time passing and the kitching processing the pizza orders
	// If kitchenHeap is not at capacity, capacity set in data, pop the next order from the front of the orderQueue, call StartCooking on that order.
	// Iterate through all orders in kitchenHeap and decrement the timeTillReady.
	// Orders are complete once timeTillReady <= 0.
	// Once an order is complete call FinishCooking on the order
	void UpdateServer(float delta);

private:
	// records the use of the promo by the customer in the customerPromosMap
	void ApplyPromo(uint customerId, const string& promoCode);

	// called by PlaceOrder and UpdateOrder
	// sum the cost of each pizza and its toppings, also remember to multiply the pizza total if its count is greater then 1. 	
	float CalculateTotalBeforeTaxAndTip(uint customerId, string orderId);

	// called by PlaceOrder and UpdateOrder
	// if the order's tipPercent > 0 then calculate the tip using tipPercent * totalBeforeTaxAndTip.
	// else if tipPercent <= 0 then change nothing.
	// round down to nearest cent.
	float CalculateTip(uint customerId, string orderId);

	// called by PlaceOrder and UpdateOrder
	// calculate the tax (from data) on the totalBeforeTaxAndTip.
	// round down to nearest cent.
	float CalculateTax(uint customerId, string orderId);

	// called by PlaceOrder and UpdateOrder
	// returns the sum of totalBeforeTaxAndTip + tip + tax;
	float CalculateTotalAfterTaxAndTip(uint customerId, string orderId);

	// Calculated based on the status of the order,
	// Orders will take n time per pizza in their order, n is from our data, for example an order with 3 total pizzas will take 3 * n seconds to complete.
	uint CalculateTimeTillReady(uint customerId, string orderId)

	// moves the order to the kitchenHeap, changes the status to STARTED, calls ApplyPromo on all promos in the order,
	// Update timeTillReady by calling CalculateTimeTillReady
	// prints the final total (including tax) to the console to imply the customer was charged.
	void StartCooking(uint customerId, string orderId);
	
	// removes order from kitchenHeap, then moves the order to the end of the readyQueue, changes the status to READY_FOR_PICK_UP.
	void FinishCooking(uint customerId, string orderId);

	// map of customerIds to a map of orderIds to orders
	unordered_map<uint, unordered_map<string, CustomerOrder>> ordersMap;

	// Orders recieved, add to the end of the queue
	list<CustomerOrderReference> orderQueue;

	// Orders being worked on by the kitchen, not sorted. Orders pulled form the front of orderQueue, removed from orderQueue after pull. 
	vector<CustomerOrderReference> kitchenHeap;

	// Once the kitchen completes an order, it will be moved here, add to the end of the queue. Orders removed from kitchenHeap after adding here.
	// Note: This is not actually currently needed, this is here in the event we want to add delievery as an option, ie this is the queue that door dash grabs from.
	list<CustomerOrderReference> readyQueue;

	// map of the customerId to a map of the promo code to the number of times it has been applied to that customer
	unordered_map<uint, unordered_map<string, uint>> customerPromosMap; 
}
```

## Communication with client

- Simple TCP
- No security or load balancing (etc)
- Must handle multiple clients: thread per connected client 
- Process: Server listens > Client connects and provides an ID mapped to interface and required inputs > Server calls requested interface and returns result to client > Server closes connection to that client and frees the related thread
- Input and output are handled with JSON strings - add a simple set of helpers for converting/parsing objects to and from these json strings

```
class ConnectionHandler
{
public:
	// call PizzaServer::Load() at the start of the method
	// for testing this will just create an infinite loop that continuously listens for client connections, 
	// it will create a new thread for each connection and use the HandleClient() method for the connection,
	// once HandleClient returns, the connection is done and the thread should close
	// call PizzaServer::Save() each time a thread finishes, if more than one finish as the same time then just call once
	void Start(PizzaServer& server);
	
private:
	// handles a single client and a single PizzaServer action,
	// client will provide a json string containing required values, will need to parse that string for expected values,
	// client should provide a FunctionId and we will use a switch to then call the correct PizzaServer method,
	// if FunctionId isn't valid then sent CONNECTION_INVALID_CALL,
	// if an input is missing then send CONNECTION_INVALID_INPUT,
	// any other error should send CONNECTION_ERROR
	// the PizzaServer method will return a value, that value should be converted to a json string, then sent to the client,
	// close the connection 
	void HandleClient(SOCKET clientSock, PizzaServer& server);
}

## JSON string input example - PlaceOrder
```
{
	FunctionId: 2
	Input: 
	{
		customerId: 42,
		customerOrder:
		{
			pizzas:
			[
				{
					pizzaCode: 1,
					toppings:
					[
						{
							toppingBaseCode: 0,
							[
								2,
								5
							],	
						},
						{
							toppingBaseCode: 1,
							[
								1
							]
						}
					],
					count: 2
				}
			],
			promoCodes:
			[
				"ILOVEPIZZA",
			],
			tip: 3.14,
			tipPercent: 0.15
		}
	}
}
```

## JSON string output example - GetAllOrders
```
{
	resultCode: 0
	customerOrders: 
	[
		{
			id: 0,
			status: 3,
			pizzas
			[
				{
					pizzaCode: 1,
					toppings:
					[
						{
							toppingBaseCode: 0,
							[
								2,
								5
							],	
						},
						{
							toppingBaseCode: 1,
							[
								1
							]
						}
					],
					count: 2
				}
			],
			promoCodes
			[
				"ILOVEPIZZA",
			],
			tip: 18.51,
			tipPercent: 0.15
			totalBeforeTaxAndTip: 123.45
			tax: 8.02
			totalAfterTaxAndTip: 149.98
		},
		{
			id: 1,
			status: 0,
			pizzas
			[
				{
					pizzaCode: 1,
					toppings:
					[
						{
							toppingBaseCode: 0,
							[
								2,
								5
							],	
						},
						{
							toppingBaseCode: 1,
							[
								1
							]
						}
					],
					count: 1
				}
			],
			promoCodes
			[
				"ILOVEPIZZA",
			],
			tip: 9.25,
			tipPercent: 0.15
			totalBeforeTaxAndTip: 61.72
			tax: 4.01
			totalAfterTaxAndTip: 74.98
		}
	]
}
```

## data

- JSON format, safed next to executable
- VS project needs to automatically copy this data to the same location as the executable when building

```
{
	menu:
	{
		pizza:
		[
			{
				code: 0,
				name: "Small pizza",
				cost: 5.00
			},
			{
				code: 1,
				name: "Medium pizza",
				cost: 7.00
			},
			{
				code: 2,
				name: "Large pizza",
				cost: 12.00
			}
		],

		toppings:
		[
			{
				code: 0,
				name: "Basic topping",
				cost: 0.08,
				toppings:
				[
					{ code: 0, name: "pepperoni" },
					{ code: 1, name: "sausage" },
					{ code: 2, name: "extra cheese" },
					{ code: 3, name: "onions" },
					{ code: 4, name: "black olives" },
					{ code: 5, name: "green peppers" }
				]
			},
			{
				code: 1,
				name: "Premium topping",
				cost: 0.11,
				toppings:
				[
					{ code: 0, name: "bacon" },
					{ code: 0, name: "chicken" },
					{ code: 0, name: "garlic" },
					{ code: 0, name: "artichoke hearts" },
					{ code: 0, name: "sun-dried tomatoes" },
					{ code: 0, name: "pineapple" }
				]
			}
		],
		promos:
		[
			{
				code: "ILOVEPIZZA",
				validations: []
				discount: 0.1,
				isPercentOff: true
			},
			{
				code: "SAVE5",
				validations: 
				[
					{
						type: 1,
						arrayOfStrings:
						[
							"ILOVEPIZZA"
						]
					},
					{
						type: 3,
						numValue: 20.0
					}
				]
				discount: 5.0,
				isPercentOff: false
			},
		]
	},

	settings:
	{
		kitchenHeapCapacity: 3,
		timePerPizzaInSeconds: 60,
		tax: 0.065
	}
}
```

## Server testing

- Unit tests for all public functions/methods, no unit tests for private methods
	- exception for tip/tax and other money related helper methods, these should have unit tests
	- aim for 80% branch coverage
- Integration testing for server/client functions
	- test for each success/fail case per function

# Client

- C# WPF
- Proper MVVM pattern
- Proper bindings to data
- Data driven based on the what is returned from GetMenu, data should be saved to the app only, not to file.
- Server authoritative, all data should come from the server
- enums will need to match the ones on the server 
- UI should be polished and visually pleasing, but simple
- classes and functions mentioned here reflect/match the ones for server
- helper functions for calculating tip, tax, etc must be mirrored on the client to keep prices identical (note: server has final authority on price conflicts)
	- for dollar amounts always round down to the nearest cent.

## Communication with server

- connects only when it needs
- has functions to match each function in FunctionId 
- converts objects into JSON strings then passes the expected JSON string when making the call to the server
- converts the returned value back into a JSON object
- returned json strings contain a result code and a json struction with the expect return from the function.
- check the plan for the server to understand the input/output and purpose of each function.
- allow one call at a time, show a spinner if the call time is taking longer then 1 second

## UI Flow

- UI flows top to bottom, single column (some exceptions on some lines).
- After the app opens, all UI is disabled except the customerId textbox and sign in button (left aligned)
	- the user must enter a customer ID (defaults to 0) then click "sign in"
	- Clicking sign in calls GetMenu to get all the initial data needed
	- If there is a failure to connect to the server, show a popup message that blocks any other UI interaction. Message should explain the server couldn't be reached. Should give the option to retry or exit the app. Retry reattempts the call, a fail results in the message popup again.
		- This is the same way we will handle any failed server calls that are not user error for the UI too.
	- After the successful call, the customerId textbox will disable with the ID locked in, and the sign in button disableds. All other UI enables.
- To the far right (right aligned) of the sign in button is a Label that says "Auto Refresh (mins)" then a textbox with the default value of 5, then to the right of that a button that says "Refresh Now".
	- Every x mins the app should automatically call GetOrderStatus on the current order (iff the current order has been placed) and the app should call GetOrderStatus on all other orders that do not have a status of DELIVERED 
	- Clicking Refresh Now does the same updating and restarts the timer.
- Under the customerId/sign in, a large orange button that says start new order
	- if the user clicks it, it will start a new order or replace the current new order (clear/default its data)
	- this will also generate the orderId GUID and assign it to the new order, this orderId will not be editable after this point
- Next, the user will see a dropdrown that shows all previous and current orders
	- line items in the drop down will show the order Id then the current status (human readable), ie "Not Started", "Started", "Ready for pick up", etc.
	- There can be only one new order at a time, so it will be last in the list and the line item will show the newly created orderId and the status will be "New".
	- Selecting the order will call GetOrderStatus and reset the auto refresh timer
	- Selecting the order will change what is displayed below in the treeview
- Under the drop down is the selected order from the dropdown, this takes up the majority of the window and has scroll bars when needed.
	- This should be a TreeView or similar element set from ObservableCollection<Pizza> (scrollbars if needed)
		```
		class Topping
		{
			string name;
			string cost; // calculate the dollar cost per topping from the value found in the topping catagory applied to the PizzaBase cost
		}

		class ToppingDisplay
		{
			string name:
			list<Topping> toppings;
			float cost; // calculate from the total dollarCost of all the toppings;
		}

		class PizzaBaseDisplay
		{
			string name;
			float cost;
		}

		class Pizza
		{
			PizzaBaseDisplay pizzaBase;
			list<ToppingDisplay> toppingCatagories;
			int count;
			float costPerPizza; // the total cost of the pizzaBase plus all toppings
			float totalCost; // costPerPizza * count
		}
		```
	- ObservableCollection<Pizza> will need to be assigned when the user selects the order from the above dropdown, or the user clicks the new order button
	- Next to each top level entry (Pizza) iff the order status is new or NOT_STARTED then add a button that says Edit
		- Clicking on edit opens the Add/Edit Pizza Popup window (see further below) 
			- This should send a binding to the pizza object to the Add/Edit Pizza Popup window so that it can be modified
			- Once the Add/Edit Pizza Popup window closes, its expected that the Pizza object will should the new values
- Next is a button that says "Add Pizza", clicking this will open the Add/Edit Pizza Popup window (see further below)
	- If the user selects an order from the drop down that is not new or not in the status of NOT_STARTED, this button should be disabled with a tooltip that says "Unable to update this order" 
- Next under the Add Pizza Button will be set of labels showing the total before tax and tip
- Under that is the tip options
	- 5 radio buttons
		1. 15%, selecting this clears the textbox for custom dollar amount and textbox for custom percent
		2. 18%, selecting this clears the textbox for custom dollar amount and textbox for custom percent
		3. 20%, selecting this clears the textbox for custom dollar amount and textbox for custom percent
		4. Custom % with a textbox next to it for the custom percent, selecting this clears the textbox for custom dollar amount
		5. Custom $ with a textbox next to it for the custom dollar amount, selecting this clears the textbox for custom percent
	- to the right of the radio buttons is a label showing the final tip, calculated against the total before tax and tip, rounded down to the nearest cent
		- Note: when placing or updating an order: 
			- if a percent is selected then assign the percent to the orders tipPercent variable and assign 0 to the tip variable
			- if a custom dollar is selected then assign that to the tip and assign 0 to the tipPercent
- Next is UI for the promos
	- A textbox to enter a promo code and a button to "Apply"
		- this will need to call the IsPromoValid function
		- if valid then add the promo to the requested order
	- under the textbox is a listbox with all the current "applied" promos
		- next to each should be an X button
			- clicking the X button removes the promo from the requested order
- Under the promos is a label showing the tax. The value comes from the GetMenu data and is calculated against the total before tax and tip
- Under the tax line should be the final total with both tax and tips summed
- Below the final total is an orange button that will say "Place Order" for a new order, or "Update Order" for an order that has a status of NOT_STARTED
	- If the user selects an order from the drop down that is not new or not in the status of NOT_STARTED, this button should be disabled with a tooltip that says "Unable to update this order" 
	- This will call the PlaceOrder function and pass the new order to the server, it will need to pass the orderId generated by the client. Update the UI after the method returns successfully.
	- If the result is ORDER_ALREADY_CREATED, this can be considered a success case, update the UI to make sure it reflects the status change 
	- After a success, should a popup dialog that says "Order Created!" and has an Ok button.
- If the status is NOT_STARTED, then also show a link style button that says "Cancel Order" aligned right and under the orange button, else hide this.
	- clicking this popups a confirmation dialog (yes/no), if yes then call the CancelOrder function.
- If the order is not "New" (the order has been placed and has a status of STARTED or greater) show a link style button aligned left (same row as the cancel link) that says "See receipt"
	- clicking this should popup window filled with an uneditable textbox (scroll bars if needed). The text box should have an easy to read and copy of the order that clearly shows each pizza, its toppings, the cost of each item, count, totals, tip, tax, etc.
	- popup just has an x button for closing.

### Add/Edit Pizza Popup window

- This window is used for editing the Pizza object sent in, this can be a new object or a pizza object already in the order
- Shows a dropdown with all the pizza types, the user select the one they want, they can only select one
	- prices shown to the right of the name
	- changes the pizza base in the pizza object
- Shows a dropdown to filter which toppings (category) to show
	- shows the dollar price (calculate based on the cost for the category against the pizza base selected) to the right of the category name
- Shows a list of the toppings in the selected category
	- left of each is a checkbox, clicking it adds it to the pizza object
- Shows a textbox for the multiplier (number of pizzas of this configuration we want)
- Shows the total before the multiplier and after
- Shows an orange button that says Add/Update Pizza
	- ideally it says Add if this is new, Update if we are editing a pizza
	- clicking this adds the new pizza or updates the selected pizza
- The window can also be closed with an x button
	- clicking x does not update or add the pizza

## Client testing

- Minimal unit tests, tests for any helper functions, especially functions that related to money calculation