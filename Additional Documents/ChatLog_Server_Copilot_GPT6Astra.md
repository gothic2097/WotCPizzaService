## :bust_in_silhouette:  (10/31/2026 5:22 PM)
giving these requirements ("C:\Users\gothi\Downloads\WotC TakeHome\Senior_Software_Developer_—_Take-Home_(Pizza-Driven_Development) (1).docx"):
```
Senior Software Developer — Take-Home Exercise
Pizza-Driven Development
Timebox: 3 hours. We don't expect polish everywhere — we expect good judgment about where to spend your time. AI tools (Claude, Copilot, ChatGPT, etc.) are allowed and expected; see the AI usage note below.
This is a standalone exercise. It is not related to our products, and nothing you build will be used by us. You own your repository and may keep it private or public.
Scenario
A pizza restaurant wants an order builder. A customer assembles an order, watches the total update as they change it, and sends it to the restaurant's order service. After that, they can check on the order and, until the kitchen starts it, change or cancel it.
Pricing rules
These are the restaurant's rules. Implement them as written. Where they leave something open, make a decision, and record it (see Deliverables).
Item	Price
Small pizza	$5
Medium pizza	$7
Large pizza	$12
Basic topping	8% of the base pizza price
Premium topping	11% of the base pizza price
Tax	6.5% of the total order
•	Basic toppings: pepperoni, sausage, extra cheese, onions, black olives, green peppers
•	Premium toppings: bacon, chicken, garlic, artichoke hearts, sun-dried tomatoes, pineapple
•	Tip: the customer enters a tip either as a dollar amount or as a percentage of the total bill.
•	Promo codes:
o	ILOVEPIZZA gives a 10% discount on the total order before tax.
o	SAVE5 takes $5 off the order if the order total before tax is over $20.
o	SAVE5 cannot be combined with ILOVEPIZZA.
All amounts shown to the customer are in dollars and whole cents.
Requirements
1. Order builder An order contains one or more line items. Each line item has a size, a quantity, and zero or more toppings. The customer can add, remove, and modify line items and toppings.
2. Running total The total updates automatically whenever the customer adds, removes, or otherwise modifies the order, including changes to the tip or the promo code. The total the customer sees while building the order must match the total the restaurant charges for that order.
3. Order description On request, the program produces a written description of the order including quantity, size, and the list of toppings for each line item.
4. Tip and promo code Support both tip modes (dollars and percentage) and both promo codes. Invalid input must be handled with a clear message to the customer.
5. Order service and lifecycle When the order is complete, the customer can send it to a remote order service. The order service is part of this exercise: build a small service that runs as a separate process from the order builder and communicates with it over the network. Through the service, the customer can:
•	send the order and receive an order ID,
•	query the order's status: Not Started, Started, Ready for Pickup, or Delivered,
•	update the order, or cancel it, but only while it is Not Started.
The restaurant side advances order status. Simulate this however you like (a timer, an endpoint, a CLI command) and document how. Customers use unreliable mobile connections and may see timeouts and retry.
6. Extensibility Adding a new size, topping, topping category, or promo code should not require changing the core pricing logic. Explain in your write-up how your design achieves this.
Deliverables
1.	Use GitHub, GitLab, BitBucket or some other git repository host to host your code. Do not send us a zip/tar bundle of your project.
2.	A README.md containing:
o	How to run both the order builder and the order service locally, and how to run the tests (one or two commands each; docker compose up or a seed script is a plus).
o	A short description of the order service's API (endpoints or messages, with example requests).
o	Assumptions: every place the requirements were ambiguous and the decision you made. We read this section closely.
o	Design write-up (~1 page) answering:
	How do you represent money and calculate the order total? Where is pricing logic located, and does it run in the order builder, the order service, or both? Why?
	How does your design handle adding a new size, topping category, or promo code, including a promo that has its own conditions and conflicts with other promos?
	How does your order lifecycle work? Walk through a customer retrying a submission after a timeout, and a customer trying to cancel at the moment the kitchen starts the order. What guarantees do you make, and how does your implementation provide them?
	What did you deliberately cut or fake to stay in the timebox, and what would you build next?
o	AI usage note (a few sentences): which tools you used and for what, one example of AI output you rejected or had to fix, and one behavior or invariant you verified yourself rather than trusting the AI, and how.
3.	Automated tests for the invariants you consider most important. In the README, say which invariants you chose to test and why, and which you did not have time to cover.
Constraints & guidance
•	Any language/framework. Any storage (in-memory, SQLite, etc. — no hosted DB required).
•	The order builder can be a web UI, a command-line program, or a desktop/mobile UI. We do not evaluate visual design.
•	No auth required. Assume one restaurant and anonymous customers.
•	Don't build: authentication or customer accounts, real payments, real kitchen or delivery integration, a menu administration UI, multiple locations.
•	Prioritize: correct pricing and a defensible order lifecycle over breadth of features.
•	If you run out of time, ship what works and say so in the README. An honest cut list beats a broken feature.
What we evaluate
Area	What we look for
Data modeling	Menu, order, line item, and order status as distinct concepts; pricing derived from data rather than scattered constants
Pricing	Exact arithmetic, consistent rounding, one source of truth for the total
Service design	Clear API, server-side validation, safe handling of retries, explicit order state transitions
Correctness	Edge cases anticipated without being told; assumptions documented and implemented consistently
Testing	Choice of invariants to test; tests that target those invariants rather than only the happy path
Extensibility	New menu items and promo codes added without touching core logic
Judgment	Scope cuts, library choices, commit history, proportionate design for a 3-hour exercise
Communication	README clarity, quality of the assumptions list and the design write-up
AI leverage	Effective use of AI without accepting broken or bloated output; evidence the candidate verified what the AI produced
We do not evaluate: CSS polish, deployment.
```

And my high level plan, the HighLevelPlanInter4.md, I need you to implement the server:
- evaluate the requirements
- evaluate my high level plan for the server
- evaluate my high level plan for the client
- following my plan, implement the server
- if there are any issues with my plan report them to me along with options to resolve the issue so we can continue.
- keep code simple
- avoid using external dependencies unless no choice
## :speech_balloon: GitHub Copilot

1. Memory Detected :white_check_mark:
1. Read HighLevelPlanInter4.md, lines 1-721 :white_check_mark:
1. Read 1 project :white_check_mark:
1. Read 4 files in WotCPizzaServer\WotCPizzaServer.vcxproj :white_check_mark:
1. Read HighLevelPlanInter4.md, lines 240-420 :white_check_mark:
1. Read HighLevelPlanInter4.md, lines 421-580 :white_check_mark:
1. Read HighLevelPlanInter4.md, lines 581-721 :white_check_mark:
1. Read WotCPizzaServer.cpp, lines 1-180 :white_check_mark:
1. Read WotCPizzaServer.vcxproj, lines 1-220 :white_check_mark:
1. Read HighLevelPlanInter4.md, lines 332-420 :white_check_mark:
1. Read HighLevelPlanInter4.md, lines 669-721 :white_check_mark:
Reviewed the supplied requirements, both sections of `HighLevelPlanInter4.md`, and the workspace. The project is currently a C++20 console application containing only “Hello World.”

**The overall design is workable, but several decisions need resolving before implementation.** No files have been changed.

### Server plan

| Issue | Recommended resolution |
|---|---|
| `float` money conflicts with the evaluation’s requirement for exact arithmetic. | Use signed 64-bit integer cents and integer basis points for percentages (`650` = 6.5%). Define rounding at each calculation stage. Your round-down policy is acceptable if consistently documented. |
| Mirrored C++/C# pricing can diverge; server authority alone does not guarantee that the displayed total matches the charged total. | Add a non-mutating `QuoteOrder` operation using the same pricing function as placement/update. Alternatively, keep local pricing with identical specifications and shared test vectors. |
| Percentage tips use the pre-tax subtotal in your plan, while the exercise says “total bill.” | Prefer the discounted total **including tax, excluding tip**, or explicitly document your pre-tax interpretation. |
| Returning pointers to mutable orders is unsafe after releasing a lock. | Return value snapshots. Use one mutex around validation and state changes, including kitchen transitions, to resolve cancel/start races atomically. |
| Saving when a connection finishes can acknowledge an order before it is persisted. Kitchen transitions also need persistence. | Persist successful mutations before replying, using a temporary file and replacement. Persist queue order and lifecycle state, not just `ordersMap`; report storage failures. |
| GUIDs prevent duplicate creation, but changed-payload retries are unspecified. | Same ID and same submission returns the existing order; same ID with different submission returns a conflict. Cancel retries should succeed if already cancelled. Never reuse cancelled IDs. |
| `N_PER_CUSTOMER` can be exceeded by multiple queued orders because usage is recorded only when cooking starts. | Either defer this optional rule or reserve uses atomically on acceptance and release them on cancellation. |
| TCP has no message boundaries. | Specify UTF-8 newline-delimited JSON, bounded request sizes, socket timeouts, and complete send/receive loops. Preserve one request per connection. |

Additional corrections:
- `PlaceOrder` is function ID **3**, not **2** as shown in the example.
- The example documents are not valid JSON, and every premium topping currently has code `0`.
- `GetMenu` does not expose the tax/promo information the planned client expects.
- `IsPromoValid` needs structured error details; its current signature cannot return the promised reason.
- Prices must be fixed on successful placement/update—not silently recalculated when cooking starts.
- Data-driven discounts and existing validation types support new promo combinations; genuinely new condition types still require an extension point or a new validator.

### Client plan

The WPF/MVVM approach fits, but these gaps should be corrected when implementing the client:

- Add a **remove line item** action.
- Make the written order description available before submission, not only after cooking starts.
- Make the Update button call `UpdateOrder`; the current instructions say `PlaceOrder` for both.
- Edit a draft copy so closing the popup without saving does not mutate the bound order.
- Exclude cancelled orders from status polling.
- Refresh the complete order where necessary; status-only responses cannot refresh changed prices or contents.
- Keep the client-generated GUID unchanged during submission retries.
- Treat customer IDs as anonymous grouping, not authentication. UI polish and account-like sign-in are lower priority than pricing and lifecycle tests.

### Scope and dependencies

The server can use the standard library and Windows Winsock without a networking dependency. C++20 has no standard JSON parser. A handwritten parser is possible but adds substantial correctness work; Windows provides a JSON API, or a small third-party JSON library would simplify implementation.

Tests should prioritize exact totals, SAVE5’s strict **over $20** boundary, promo conflicts in both directions, retry behavior, rejected late updates/cancellations, concurrent transitions, and restart recovery. An 80% branch-coverage claim would require actual measurement.

The following choices affect the implementation and client contract.
1. Asked 5 questions :white_check_mark:
1.  :white_check_mark:
Starting with the models and JSON helpers. The wire format will use explicit `...Cents` and `...BasisPoints` fields to prevent unit confusion. Client requests will include the menu version so stale pricing is rejected rather than charged silently.
1. Command executed: Get-ChildItem -Force; git status --short; Test-Path 'C:\Program Files (x86)\Windows Kits\10\Include' :white_check_mark:
 1. Modifying Models.h
 1. Modifying Json.h
 1. Modifying Json.cpp
Next is pricing and menu data. Calculations will round down per topping, per discount, tax, and percentage tip. SAVE5 will require the undiscounted subtotal to be strictly greater than $20, and promo conflicts will be checked in both directions.
 1. Modifying Pricing.h
 1. Modifying Pricing.cpp
 1. Modifying menu.json
Now implementing lifecycle and persistence. A single mutex will serialize order changes and kitchen transitions. Changes will be saved before success is returned; failed saves will leave in-memory state unchanged. Submission retries will be checked against the original request, and updates will require a revision to prevent stale writes.
 1. Modifying PizzaServer.h
 1. Modifying PizzaServer.cpp
Next is the TCP listener and executable. The service will bind to loopback by default, accept one newline-delimited JSON request per connection, enforce size and time limits, and join client threads on shutdown. A file lock will prevent two server processes from writing the same order store.
 1. Modifying ConnectionHandler.h
 1. Modifying ConnectionHandler.cpp
`WotCPizzaServer.cpp` will become the entry point, with executable-relative data loading, port/data-directory options, shutdown handling, and a self-test option.
 1. Modifying WotCPizzaServer.cpp

1. Couldn't run get_tests :negative_squared_cross_mark:
1. No tests found matching filters: Project=WotCPizzaServer :white_check_mark:
Now adding automated tests and shared pricing vectors for the future C# client. No existing tests were found in Test Explorer, so the server will have a dependency-free `--self-test` suite. A separate PowerShell smoke test will exercise TCP framing, validation, retries, and restart recovery.
 1. Modifying pricing-vectors.json
 1. Modifying Tests.cpp
 1. Modifying NetworkSmoke.ps1

1. Read WotCPizzaServer.vcxproj.filters, lines 1-200 :white_check_mark:
1. Read WotCPizzaServer.vcxproj, lines 115-230 :white_check_mark:
Configuring the build next. The project already includes the new source files. I’ll add Windows SDK library links and copy the menu and shared pricing vectors beside the executable without touching saved orders.
 1. Modifying WotCPizzaServer.vcxproj
 1. Modifying WotCPizzaServer.vcxproj
 1. Modifying WotCPizzaServer.vcxproj
1. Command executed: $path = Join-Path (Get-Location) 'WotCPizzaServer\WotCPizzaServer.vcxproj'; $text = [IO.File]::ReadAllText($path); $settings = '<ItemDefinitionGroup><ClCompile><PreprocessorDefinitions>NOMINMAX;WIN32_LEAN_AND_MEAN;%(PreprocessorDefinitions)</PreprocessorDefinitions><AdditionalOptions>/utf-8 %(AdditionalOptions)</AdditionalOptions></ClCompile><Link><AdditionalDependencies>Ws2_32.lib;WindowsApp.lib;%(AdditionalDependencies)</AdditionalDependencies></Link></ItemDefinitionGroup>'; $target = '<Target Name="CopyPizzaData" AfterTargets="Build"><Copy SourceFiles="$(ProjectDir)menu.json;$(ProjectDir)pricing-vectors.json" DestinationFolder="$(OutDir)" SkipUnchangedFiles="true" /></Target>'; $marker = '<Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />'; if (-not $text.Contains($marker)) { throw 'Expected MSBuild import not found' }; $text = $text.Replace($marker, $settings + [Environment]::NewLine + '  ' + $marker + [Environment]::NewLine + '  ' + $target); [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false)) :white_check_mark:
Starting validation: build the server, run the invariant suite, then run the TCP smoke test. Failures will be checked and fixed before documenting the results.
1. Build failed :negative_squared_cross_mark:
Reached the tool-call limit for this turn. **Implementation is substantially written, but it does not build yet and is not ready to use.**

### Added
- C++ models with integer cents/basis points and round-down pricing.
- Data-driven menu, toppings, tax, and promo validation.
- Synchronized order lifecycle, submission deduplication, revision-checked updates, and JSON persistence.
- TCP listener, request validation, timeouts, and shutdown handling.
- Shared pricing vectors, self-tests, and a PowerShell network smoke test.
- Project settings for Windows SDK libraries and data-file copying.

The implementation follows your selected decisions: mirrored client pricing, tips on the discounted after-tax bill, Windows SDK JSON, and deferred per-customer promo limits.

### Validation findings
The first build failed. The primary issues identified are:
- Missing C++/WinRT collection definitions required by JSON objects and arrays.
- A Windows `GetObject` macro collision with JSON’s `GetObject()` method.

Many reported errors are cascading from these issues. **Tests have not run**, and the README is still unfinished. The workspace also is not currently a Git repository.

### Remaining
Fix the build, run and repair the tests, review correctness, and finish the API/design/run documentation.

Send **“Continue implementation and validation”** to resume from the active validation step.

___

## :bust_in_silhouette:
Continue implementation and validation
## :speech_balloon: GitHub Copilot
1. Read Json.h, lines 1-100 :white_check_mark:
Resuming validation. I’ll fix the Windows JSON header issues, rebuild, and run the automated tests before finishing the documentation.
 1. Modifying Json.h

1. Build successful :white_check_mark:
1. Command executed: & '.\x64\Debug\WotCPizzaServer.exe' --self-test; exit $LASTEXITCODE :negative_squared_cross_mark:
1. Listing background terminal sessions :white_check_mark:
The build now passes. The test command ended without returning results; I’ll run it with captured output so completion and failures are visible.

1. Command executed: & '.\x64\Debug\WotCPizzaServer.exe' --self-test; if ($LASTEXITCODE -ne 0) { throw "Self-tests failed with exit code $LASTEXITCODE" } :white_check_mark:
1. Reading first 40 and last 30 lines of background terminal :white_check_mark:
1. Command executed: & '.\tests\NetworkSmoke.ps1' :white_check_mark:
1. Reading first 30 and last 30 lines of background terminal :white_check_mark:
1. Review server correctness :white_check_mark:
The initial checks pass: **24 self-tests and 17 network checks**.

Review found one edge case to fix: an order near the revision limit could save a kitchen transition that would fail validation on restart. I’ll reserve room for those transitions and add regression coverage. I’ll also test a lost placement response followed by a retry.

1. Read PizzaServer.cpp, lines 25-70 :white_check_mark:
1. Read PizzaServer.cpp, lines 180-210 :white_check_mark:
 1. Modifying PizzaServer.cpp
1. Read Tests.cpp, lines 208-295 :white_check_mark:
I’ll add the revision-limit regression to `Tests.cpp` and extend its storage-failure checks to cover updates, cancellations, and kitchen transitions.
 1. Modifying Tests.cpp
1. Read NetworkSmoke.ps1, lines 52-110 :white_check_mark:
I’ll extend `NetworkSmoke.ps1` to verify that client-supplied totals are ignored and that retrying after discarding a placement response does not create another order.
 1. Modifying NetworkSmoke.ps1

1. Build successful :white_check_mark:
1. Command executed: & '.\x64\Debug\WotCPizzaServer.exe' --self-test; if ($LASTEXITCODE -ne 0) { throw "Self-tests failed: $LASTEXITCODE" }; & '.\tests\NetworkSmoke.ps1' :white_check_mark:
1. Reading first 28 and last 12 lines of background terminal :white_check_mark:

1. Read logs from Output (Build) :white_check_mark:
1. Read 0 files for 'README.md', '.gitignore', 'build.ps1' :white_check_mark:
The expanded checks pass: **25 self-tests and 20 network checks**. I’ll verify the Release build next, then document the API, pricing rules, retry behavior, and remaining client work.

1. Command executed: $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'; $msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1; if (-not $msbuild) { throw 'MSBuild not found' }; & $msbuild '.\WotCPizzaServer.slnx' /m /p:Configuration=Release /p:Platform=x64 /verbosity:minimal; if ($LASTEXITCODE -ne 0) { throw "Release build failed: $LASTEXITCODE" }; & '.\x64\Release\WotCPizzaServer.exe' --self-test; if ($LASTEXITCODE -ne 0) { throw "Release tests failed: $LASTEXITCODE" }; & '.\tests\NetworkSmoke.ps1' -Executable '.\x64\Release\WotCPizzaServer.exe' :white_check_mark:
1. Reading first 15 and last 12 lines of background terminal :white_check_mark:
1. Reading first 15 and last 8 lines of background terminal :white_check_mark:
Debug and Release validation are complete. I’m finishing `README.md` with run instructions, protocol examples, assumptions, and the changes the future WPF client needs to follow. I’ll also add ignore rules for build output and local order data.
 1. Modifying README.md
 1. Modifying .gitignore
1. Build successful :white_check_mark:
Server implementation and validation are complete.

- Fixed Windows JSON build issues and a revision-limit persistence defect.
- Added regression tests for mutation rollback and retries after a lost response.
- Added `README.md` covering setup, API, pricing, lifecycle guarantees, assumptions, and client changes.
- Added `.gitignore` for generated files and local order data.

**Validation:** Debug and Release x64 builds pass. Each passes **25 self-tests and 20 network checks**.

Run: `.\x64\Debug\WotCPizzaServer.exe`  
Default address: `127.0.0.1:5050`

The WPF client and repository hosting remain outside this server implementation. The README identifies the remaining exercise deliverables.

The tool-call limit has been reached for this turn; no further validation was performed beyond the results above.

___

## :bust_in_silhouette:
is there any addtional server work?
## :speech_balloon: GitHub Copilot
**No known blocking server work remains for the agreed scope.** Debug and Release builds pass, along with 25 self-tests and 20 network checks.

Before submission, the most useful additional checks would be:
- **Network edge cases:** oversized requests, slow clients, timeouts, and the connection limit.
- **Graceful shutdown:** confirm Ctrl+C handles active connections cleanly.
- **Client integration:** verify the future C# client against the shared pricing vectors and retry/revision contract.

Documented limitations—not required fixes—include whole-file JSON persistence, Windows-only support, no per-customer promo limits, and no server quote endpoint.

Given the exercise’s priorities, **implementing and validating the client is more valuable now than adding server features**. Also personally verify an invariant and finalize the README’s AI usage note before submission.

___

## :bust_in_silhouette:
Ran the server, hit ctrl-c, got this exception:
```
Exception thrown at 0x00007FFEB16C6484 (KernelBase.dll) in WotCPizzaServer.exe: 0x40010005: Control-C.
```
## :speech_balloon: GitHub Copilot
That is normally a **Visual Studio debugger notification**, not a server crash. Windows raises `0x40010005` (`DBG_CONTROL_C`) when Ctrl+C is pressed while a debugger is attached.

- Press **F5 / Continue** to let execution proceed to the server’s console control handler.
- To test shutdown without debugger interception, launch with **Ctrl+F5** or run the executable from PowerShell, then press Ctrl+C.

The server should stop accepting connections and wait for active client handlers to finish before exiting.

If it still crashes or hangs when run **outside the debugger**, that indicates a separate shutdown issue worth investigating.
