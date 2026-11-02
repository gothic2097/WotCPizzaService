#include "PizzaServer.h"
#include <Windows.h>
#include <atomic>
#include <barrier>
#include <iostream>
#include <sstream>
#include <thread>

namespace
{
    using namespace pizza;

    struct ConsoleCapture
    {
        std::ostringstream output;
        std::streambuf* previous{ std::cout.rdbuf(output.rdbuf()) };
        ~ConsoleCapture()
        {
            std::cout.rdbuf(previous);
        }
    };

    void Check(bool condition, const std::string& message)
    {
        if (!condition)
        {
            throw std::runtime_error(message);
        }
    }

    template<class Action> void ExpectError(Action action, ResultCode code)
    {
        try
        {
            action();
        }
        catch (const Error& error)
        {
            Check(error.code == code, "Unexpected error code: " + std::to_string(error.code));
            return;
        }

        throw std::runtime_error("Expected an error, but operation succeeded.");
    }

    struct Suite
    {
        int passed{};
        int failed{};

        template<class Action> void Run(const std::string& name, Action action)
        {
            try
            {
                action();
                ++passed;
                std::cout << "PASS " << name << '\n';
            }
            catch (const winrt::hresult_error& error)
            {
                ++failed;
                std::cerr << "FAIL " << name << ": " << winrt::to_string(error.message()) << '\n';
            }
            catch (const std::exception& error)
            {
                ++failed;
                std::cerr << "FAIL " << name << ": " << error.what() << '\n';
            }
        }
    };

    struct Fixture
    {
        std::filesystem::path directory;

        Fixture(const JsonObject& menu)
        {
            static std::atomic<unsigned> sequence{};
            directory = std::filesystem::temp_directory_path() / (L"WotCPizzaTests-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()) + L"-" + std::to_wstring(sequence++));
            if (!std::filesystem::create_directory(directory))
            {
                throw std::runtime_error("Cannot create isolated test directory.");
            }
            
            WriteJsonAtomic(directory / L"menu.json", menu);
        }

        ~Fixture()
        {
            std::error_code error;
            std::filesystem::remove_all(directory, error);
        }
    };

    RequestedCustomerOrder Request(unsigned number = 1)
    {
        RequestedCustomerOrder request;
        request.orderId = "00000000-0000-0000-0000-" + std::string(12 - std::to_string(number).size(), '0') + std::to_string(number);
        request.menuVersion = "1";
        request.pizzas.push_back({ 0, 1, {} });
        return request;
    }

    std::int64_t Code(const JsonObject& response)
    {
        return Integer(response, L"resultCode", -1, 7);
    }

    JsonObject Order(const JsonObject& response)
    {
        return Object(response, L"customerOrder");
    }

    OrderStatus Status(const JsonObject& response)
    {
        return static_cast<OrderStatus>(Integer(Order(response), L"status", 0, 4));
    }

    JsonObject Message(FunctionId function, const RequestedCustomerOrder& request, std::uint32_t customer = 0)
    {
        JsonObject input;
        Put(input, L"customerId", std::int64_t(customer));
        Put(input, L"orderId", request.orderId);
        input.Insert(L"customerOrder", ToJson(request));
        JsonObject message;
        Put(message, L"FunctionId", std::int64_t(function));
        message.Insert(L"Input", input);
        return message;
    }
}

int RunSelfTests(const std::filesystem::path& executableDirectory)
{
    using namespace pizza;
    Suite suite;
    const auto config = ReadJson(executableDirectory / L"menu.json");
    const auto menu = MenuFromJson(config);
    for (auto value : Array(ReadJson(executableDirectory / L"pricing-vectors.json"), L"cases"))
    {
        const auto test = value.GetObject();
        suite.Run(String(test, L"name"), [&]
        {
            const auto request = RequestFromJson(Object(test, L"request"));
            if (test.HasKey(L"expectedResultCode"))
            {
                ExpectError([&]
                {
                    CalculatePrice(menu, request);
                }, static_cast<ResultCode>(Integer(test, L"expectedResultCode", -1, 7)));
            }
            else
            {
                Check(CalculatePrice(menu, request) == TotalsFromJson(Object(test, L"expected")), "Pricing vector mismatch.");
            }
        });
    }
    suite.Run("Data-driven new size/category/promo; per-topping rounding; discount clamp", [&]
    {
        auto extended = menu;
        extended.pizzas.emplace(9, PizzaDisplayItem{ 9, "Test size", 501 });
        extended.toppings.emplace(9, ToppingCategoryDisplay{ 9, "Test category", 333, {{9, "Test topping"}} });
        extended.promos.emplace("NEW", Promo{ "NEW", false, 99999, {} });
        auto request = Request();
        request.pizzas = {{9, 3, {{9, {9}}}}};
        auto totals = CalculatePrice(extended, request);
        Check(totals.subtotalCents == 1551, "Round each topping before multiplying quantity.");
        request.promoCodes = { "NEW" };
        Check(CalculatePrice(extended, request).totalAfterTaxAndTipCents == 0, "Discount must not make the order negative.");
    });
    suite.Run("SAVE5 threshold is strict at the one-cent boundary", [&]
    {
        auto extended = menu;
        extended.pizzas.at(0).costCents = 2001;
        auto request = Request();
        request.promoCodes = { "SAVE5" };
        Check(CalculatePrice(extended, request).totalBeforeTaxAndTipCents == 1501, "2001 cents should qualify.");
        extended.pizzas.at(0).costCents = 2000;
        ExpectError([&]
        {
            CalculatePrice(extended, request);
        }, PROMO_INVALID);
    });
    suite.Run("Invalid quantities, codes, duplicate toppings, tips, overflow, and stale menu", [&]
    {
        auto request = Request(); request.pizzas.clear();
        ExpectError([&]
        {
            CalculatePrice(menu, request);
        }, CONNECTION_INVALID_INPUT);
        request = Request(); request.pizzas[0].count = 0;
        ExpectError([&]
        {
            CalculatePrice(menu, request);
        }, CONNECTION_INVALID_INPUT);
        request = Request(); request.pizzas[0].pizzaCode = 999;
        ExpectError([&]
        {
            CalculatePrice(menu, request);
        }, CONNECTION_INVALID_INPUT);
        request = Request(); request.pizzas[0].toppings = {{0, {0, 0}}};
        ExpectError([&]
        {
            CalculatePrice(menu, request);
        }, CONNECTION_INVALID_INPUT);
        request.pizzas[0].toppings = {{0, {99}}};
        ExpectError([&]
        {
            CalculatePrice(menu, request);
        }, CONNECTION_INVALID_INPUT);
        request.pizzas[0].toppings = {{0, {0}}, {0, {1}}};
        ExpectError([&]
        {
            CalculatePrice(menu, request);
        }, CONNECTION_INVALID_INPUT);
        request = Request(); request.tipCents = -1;
        ExpectError([&]
        {
            CalculatePrice(menu, request);
        }, CONNECTION_INVALID_INPUT);
        request.tipCents = 1; request.tipBasisPoints = 1500;
        ExpectError([&]
        {
            CalculatePrice(menu, request);
        }, CONNECTION_INVALID_INPUT);
        request = Request(); request.tipCents = MaxMoney;
        ExpectError([&]
        {
            CalculatePrice(menu, request);
        }, CONNECTION_INVALID_INPUT);
        request = Request(); request.menuVersion = "stale";
        ExpectError([&]
        {
            CalculatePrice(menu, request);
        }, FAIL);
    });
    suite.Run("Malformed JSON, fractional/negative quantities, and invalid GUIDs rejected", [&]
    {
        ExpectError([&]
        {
            ParseJson("{");
        }, CONNECTION_INVALID_INPUT);
        ExpectError([&]
        {
            ParseJson(std::string("{\"x\":\"") + char(0xff) + "\"}");
        }, CONNECTION_INVALID_INPUT);
        auto json = ToJson(Request());
        auto item = Array(json, L"pizzas").GetObjectAt(0);
        item.Insert(L"count", JsonValue::CreateNumberValue(1.5));
        ExpectError([&]
        {
            RequestFromJson(json);
        }, CONNECTION_INVALID_INPUT);
        item.Insert(L"count", JsonValue::CreateNumberValue(-1));
        ExpectError([&]
        {
            RequestFromJson(json);
        }, CONNECTION_INVALID_INPUT);
        json = ToJson(Request()); Put(json, L"orderId", std::string("not-a-guid"));
        ExpectError([&]
        {
            RequestFromJson(json);
        }, CONNECTION_INVALID_INPUT);
    });
    suite.Run("Configuration rejects unsupported customer rules and duplicate menu codes", [&]
    {
        auto json = ParseJson(JsonText(config));
        auto promos = Array(json, L"promos");
        auto promo = promos.GetObjectAt(0);
        auto rules = Array(promo, L"validations");
        JsonObject rule; Put(rule, L"type", std::int64_t(N_PER_CUSTOMER)); rules.Append(rule);
        ExpectError([&]
        {
            MenuFromJson(json);
        }, CONNECTION_INVALID_INPUT);
        json = ParseJson(JsonText(config));
        auto pizzas = Array(json, L"pizzas"); pizzas.Append(pizzas.GetAt(0));
        ExpectError([&]
        {
            MenuFromJson(json);
        }, CONNECTION_INVALID_INPUT);
    });
    suite.Run("API dispatch, validation, menu, description, and unknown customer", [&]
    {
        Fixture fixture(config); PizzaServer server(fixture.directory);
        auto request = Request();
        Check(Code(server.Handle(Message(FunctionId::GetMenu, request))) == SUCCESS, "GetMenu failed.");
        Check(Code(server.Handle(Message(FunctionId::GetAllOrders, request))) == FAIL, "Unknown customer should fail.");
        auto invalid = Message(FunctionId::GetMenu, request); Put(invalid, L"FunctionId", std::int64_t(99));
        Check(Code(server.Handle(invalid)) == CONNECTION_INVALID_CALL, "Unknown function accepted.");
        Check(Code(server.Handle(JsonObject{})) == CONNECTION_INVALID_INPUT, "Missing input accepted.");
        auto placed = server.Handle(Message(FunctionId::PlaceOrder, request));
        Check(Code(placed) == SUCCESS && !String(Order(placed), L"description").empty(), "Place/description failed.");
        Check(Code(server.Handle(Message(FunctionId::GetOrder, request))) == SUCCESS, "GetOrder failed.");
        Check(Code(server.Handle(Message(FunctionId::GetOrderStatus, request))) == SUCCESS, "GetOrderStatus failed.");
        Check(Array(server.Handle(Message(FunctionId::GetAllOrders, request)), L"customerOrders").Size() == 1, "GetAllOrders wrong.");
        Check(Code(server.Handle(Message(FunctionId::GetOrder, request, 1))) == FAIL, "Wrong customer can fetch order.");
        Check(Code(server.Handle(Message(FunctionId::CompleteOrder, request))) == FAIL, "Completed pending order.");
        auto promo = Message(FunctionId::IsPromoValid, request); auto input = Object(promo, L"Input"); Put(input, L"promoCode", std::string("ILOVEPIZZA"));
        Check(Code(server.Handle(promo)) == PROMO_VALID, "Promo validation failed.");
        Check(Array(Order(server.GetOrder(0, request.orderId)), L"promoCodes").Size() == 0, "Promo check mutated order.");
    });
    suite.Run("Submission retries, changed-payload conflict, update retries/revisions, restart", [&]
    {
        ConsoleCapture console;
        Fixture fixture(config); auto original = Request(); auto changed = original; changed.pizzas[0].count = 2;
        const auto expectedPlacementMessage = "Status Change - NOT_STARTED - CustomerId: 0, OrderId: " + original.orderId + "\n\n";
        {
            PizzaServer server(fixture.directory);
            Check(Code(server.PlaceOrder(0, original)) == SUCCESS, "Place failed.");
            Check(console.output.str() == expectedPlacementMessage, "Placement status message format does not match.");
            Check(Code(server.PlaceOrder(0, original)) == ORDER_ALREADY_CREATED, "Retry duplicated order.");
            ExpectError([&]
            {
                server.PlaceOrder(0, changed);
            }, FAIL);
            Check(Code(server.UpdateOrder(0, changed, 1)) == SUCCESS, "Update failed.");
            Check(Integer(Order(server.UpdateOrder(0, changed, 1)), L"revision") == 2, "Update retry applied twice.");
            ExpectError([&]
            {
                server.UpdateOrder(0, original, 1);
            }, FAIL);
            Check(Integer(Order(server.PlaceOrder(0, original)), L"revision") == 2, "Original retry did not return current order.");
            server.Save();
        }
        PizzaServer restored(fixture.directory);
        Check(Code(restored.PlaceOrder(0, original)) == ORDER_ALREADY_CREATED, "Deduplication lost on restart.");
        Check(Integer(Order(restored.UpdateOrder(0, changed, 1)), L"revision") == 2, "Update replay lost on restart.");
        Check(console.output.str() == expectedPlacementMessage, "Retry, update, or reload logged another status change.");
        Check(Code(restored.CancelOrder(0, original.orderId)) == SUCCESS, "Cancel failed.");
        const auto expectedCancelledMessage = expectedPlacementMessage + "Status Change - CANCELLED - CustomerId: 0, OrderId: " + original.orderId + "\n\n";
        Check(console.output.str() == expectedCancelledMessage, "Cancelled status message format does not match.");
        Check(Status(restored.CancelOrder(0, original.orderId)) == CANCELLED, "Cancel retry failed.");
        Check(Code(restored.UpdateOrder(0, original, 3)) == ORDER_ALREADY_STARTED, "Cancelled order was updated.");
        Check(Status(restored.PlaceOrder(0, original)) == CANCELLED, "Cancelled ID was reused.");
        Check(console.output.str() == expectedCancelledMessage, "Cancelled order logged another status change.");
    });
    suite.Run("FIFO, unchanged queue position, capacity, lifecycle, and persisted deadlines", [&]
    {
        auto configuration = ParseJson(JsonText(config));
        auto settings = Object(configuration, L"settings");
        Put(settings, L"kitchenHeapCapacity", std::int64_t(1)); Put(settings, L"timePerPizzaInSeconds", std::int64_t(2)); Put(settings, L"startDelaySeconds", std::int64_t(1));
        Fixture fixture(configuration); std::int64_t now = 1000;
        auto first = Request(1); auto second = Request(2);
        {
            PizzaServer server(fixture.directory, [&]
            {
                return now;
            });
            server.PlaceOrder(0, first); server.PlaceOrder(0, second);
            first.pizzas[0].count = 2; server.UpdateOrder(0, first, 1);
            server.UpdateServer(); Check(Status(server.GetOrder(0, first.orderId)) == NOT_STARTED, "Started before grace period.");
            now = 1001; server.UpdateServer();
            Check(Status(server.GetOrder(0, first.orderId)) == STARTED, "FIFO order changed after update.");
            Check(Status(server.GetOrder(0, second.orderId)) == NOT_STARTED, "Kitchen exceeded capacity.");
            Check(Code(server.CancelOrder(0, first.orderId)) == ORDER_ALREADY_STARTED, "Cancelled cooking order.");
            Check(Code(server.UpdateOrder(0, first, 3)) == ORDER_ALREADY_STARTED, "Updated cooking order.");
        }
        PizzaServer restored(fixture.directory, [&]
        {
            return now;
        });
        now = 1005; restored.UpdateServer();
        Check(Status(restored.GetOrder(0, first.orderId)) == READY_FOR_PICK_UP, "Cooking deadline not restored.");
        Check(Status(restored.GetOrder(0, second.orderId)) == STARTED, "Queue not restored.");
        Check(Status(restored.CompleteOrder(0, first.orderId)) == DELIVERED, "Complete failed.");
        Check(Status(restored.CompleteOrder(0, first.orderId)) == DELIVERED, "Complete retry failed.");
        Check(Code(restored.CancelOrder(0, first.orderId)) == ORDER_ALREADY_STARTED, "Cancelled delivered order.");
    });
    suite.Run("Storage failure rolls back mutation and allows safe retry", [&]
    {
        ConsoleCapture console;
        Fixture fixture(config); std::int64_t now = 1000;
        PizzaServer server(fixture.directory, [&]
        {
            return now;
        });
        auto request = Request();
        const auto temporary = fixture.directory / L"orders.json.tmp";
        std::filesystem::create_directory(temporary);
        ExpectError([&]
        {
            server.PlaceOrder(0, request);
        }, FAIL);
        ExpectError([&]
        {
            server.GetOrder(0, request.orderId);
        }, FAIL);
        Check(console.output.str().empty(), "Failed placement logged a status change.");
        std::filesystem::remove(temporary);
        Check(Code(server.PlaceOrder(0, request)) == SUCCESS, "Retry after storage recovery failed.");
        const auto expectedPlacementMessage = "Status Change - NOT_STARTED - CustomerId: 0, OrderId: " + request.orderId + "\n\n";
        Check(console.output.str() == expectedPlacementMessage, "Placement status message format does not match.");
        std::filesystem::create_directory(temporary);
        auto changed = request; changed.pizzas[0].count = 2;
        ExpectError([&]
        {
            server.UpdateOrder(0, changed, 1);
        }, FAIL);
        ExpectError([&]
        {
            server.CancelOrder(0, request.orderId);
        }, FAIL);
        now = 1010;
        ExpectError([&]
        {
            server.UpdateServer();
        }, FAIL);
        auto order = Order(server.GetOrder(0, request.orderId));
        Check(Integer(order, L"revision") == 1 && Integer(order, L"status") == NOT_STARTED, "Failed mutation changed state.");
        Check(Integer(Object(order, L"totals"), L"subtotalCents") == 500, "Failed update changed the total.");
        Check(console.output.str() == expectedPlacementMessage, "Failed mutation logged a status change.");
        std::filesystem::remove(temporary);
        server.UpdateServer();
        const auto expectedMessage = expectedPlacementMessage + "Status Change - STARTED - CustomerId: 0, OrderId: " + request.orderId + ", Note: simulated charge 532 cents\n\n";
        Check(console.output.str() == expectedMessage, "Started status message format does not match.");
        server.UpdateServer();
        Check(console.output.str() == expectedMessage, "Unchanged order logged another status change.");
        Check(Status(server.GetOrder(0, request.orderId)) == STARTED, "Kitchen could not retry after storage recovery.");
        now = 1070;
        std::filesystem::create_directory(temporary);
        ExpectError([&]
        {
            server.UpdateServer();
        }, FAIL);
        Check(Status(server.GetOrder(0, request.orderId)) == STARTED, "Failed ready transition changed state.");
        Check(console.output.str() == expectedMessage, "Failed ready transition logged a status change.");
        std::filesystem::remove(temporary);
        server.UpdateServer();
        const auto expectedReadyMessage = expectedMessage + "Status Change - READY_FOR_PICK_UP - CustomerId: 0, OrderId: " + request.orderId + "\n\n";
        Check(Status(server.GetOrder(0, request.orderId)) == READY_FOR_PICK_UP, "Kitchen did not finish the order.");
        Check(console.output.str() == expectedReadyMessage, "Ready status message format does not match.");
        server.UpdateServer();
        Check(console.output.str() == expectedReadyMessage, "Ready order logged another status change.");
        std::filesystem::create_directory(temporary);
        ExpectError([&]
        {
            server.CompleteOrder(0, request.orderId);
        }, FAIL);
        Check(Status(server.GetOrder(0, request.orderId)) == READY_FOR_PICK_UP, "Failed completion changed state.");
        Check(console.output.str() == expectedReadyMessage, "Failed completion logged a status change.");
        std::filesystem::remove(temporary);
        Check(Status(server.CompleteOrder(0, request.orderId)) == DELIVERED, "Completion retry failed.");
        const auto expectedDeliveredMessage = expectedReadyMessage + "Status Change - DELIVERED - CustomerId: 0, OrderId: " + request.orderId + "\n\n";
        Check(console.output.str() == expectedDeliveredMessage, "Delivered status message format does not match.");
        Check(Status(server.CompleteOrder(0, request.orderId)) == DELIVERED, "Repeated completion failed.");
        Check(console.output.str() == expectedDeliveredMessage, "Delivered order logged another status change.");
    });
    suite.Run("Revision limit reserves all kitchen transitions and remains restartable", [&]
    {
        Fixture fixture(config); std::int64_t now = 1000; auto request = Request();
        {
            PizzaServer server(fixture.directory, [&]
            {
                return now;
            });
            server.PlaceOrder(0, request);
        }
        auto storage = ReadJson(fixture.directory / L"orders.json");
        auto storedOrder = Array(storage, L"orders").GetObjectAt(0);
        Put(storedOrder, L"revision", MaxMoney - 4);
        WriteJsonAtomic(fixture.directory / L"orders.json", storage);
        {
            PizzaServer server(fixture.directory, [&]
            {
                return now;
            });
            request.tipCents = 1;
            Check(Code(server.UpdateOrder(0, request, MaxMoney - 4)) == SUCCESS, "Last allowed update failed.");
            ExpectError([&]
            {
                server.UpdateOrder(0, request, MaxMoney - 3);
            }, FAIL);
            now = 1010; server.UpdateServer();
            now = 1070; server.UpdateServer();
            Check(Status(server.CompleteOrder(0, request.orderId)) == DELIVERED, "Revision limit blocked delivery.");
        }
        PizzaServer restored(fixture.directory, [&]
        {
            return now;
        });
        Check(Integer(Order(restored.GetOrder(0, request.orderId)), L"revision") == MaxMoney, "Terminal revision did not survive restart.");
    });
    suite.Run("Corrupt storage fails startup instead of discarding orders", [&]
    {
        Fixture fixture(config); WriteJsonAtomic(fixture.directory / L"orders.json", JsonObject{});
        bool failed = false;
        try
        {
            PizzaServer server(fixture.directory);
        }
        catch (const std::exception&)
        {
            failed = true;
        }
        Check(failed, "Corrupt storage was silently ignored.");
    });
    suite.Run("Menu changes require a new version; historical totals stay fixed", [&]
    {
        Fixture fixture(config); auto request = Request();
        {
            PizzaServer server(fixture.directory);
            server.PlaceOrder(0, request);
        }
        auto updated = ParseJson(JsonText(config));
        auto size = Array(updated, L"pizzas").GetObjectAt(0); Put(size, L"costCents", std::int64_t(999));
        WriteJsonAtomic(fixture.directory / L"menu.json", updated);
        bool failed = false;
        try
        {
            PizzaServer server(fixture.directory);
        }
        catch (const std::exception&)
        {
            failed = true;
        }
        Check(failed, "Changed menu accepted with reused version.");
        Put(updated, L"menuVersion", std::string("2")); WriteJsonAtomic(fixture.directory / L"menu.json", updated);
        PizzaServer server(fixture.directory);
        Check(Integer(Object(Order(server.GetOrder(0, request.orderId)), L"totals"), L"subtotalCents") == 500, "Historical total changed.");
        Check(Code(server.PlaceOrder(0, request)) == ORDER_ALREADY_CREATED, "Old-menu retry failed.");
        request.orderId = Request(2).orderId; ExpectError([&]
        {
            server.PlaceOrder(0, request);
        }, FAIL);
    });
    suite.Run("Concurrent identical submissions create exactly one order", [&]
    {
        Fixture fixture(config); PizzaServer server(fixture.directory); auto request = Request();
        std::barrier start(9); std::atomic<int> created{}, duplicates{}, errors{};
        std::vector<std::jthread> threads;
        for (int i = 0; i < 8; ++i)
        {
            threads.emplace_back([&]
            {
                winrt::init_apartment(winrt::apartment_type::multi_threaded);
                start.arrive_and_wait();
                try
                {
                    const auto code = Code(server.PlaceOrder(0, request));
                    if (code == SUCCESS)
                    {
                        ++created;
                    }
                    else if (code == ORDER_ALREADY_CREATED)
                    {
                        ++duplicates;
                    }
                    else
                    {
                        ++errors;
                    }
                }
                catch (...)
                {
                    ++errors;
                }
                winrt::uninit_apartment();
            });
        }
        start.arrive_and_wait(); threads.clear();
        Check(created == 1 && duplicates == 7 && errors == 0, "Concurrent retry invariant violated.");
    });
    suite.Run("Cancel racing kitchen start has exactly one winning transition", [&]
    {
        for (unsigned iteration = 0; iteration < 20; ++iteration)
        {
            Fixture fixture(config); std::int64_t now = 1000; PizzaServer server(fixture.directory, [&]
            {
                return now;
            });
            const auto request = Request(iteration + 1); server.PlaceOrder(0, request); now = 2000;
            std::barrier start(3); std::int64_t cancelCode = -99; std::atomic<int> errors{};
            std::jthread cancel([&]
            {
                winrt::init_apartment(winrt::apartment_type::multi_threaded); start.arrive_and_wait();
                try
                {
                    cancelCode = Code(server.CancelOrder(0, request.orderId));
                }
                catch (...)
                {
                    ++errors;
                }
                winrt::uninit_apartment();
            });
            std::jthread kitchen([&]
            {
                winrt::init_apartment(winrt::apartment_type::multi_threaded); start.arrive_and_wait();
                try
                {
                    server.UpdateServer();
                }
                catch (...)
                {
                    ++errors;
                }
                winrt::uninit_apartment();
            });
            start.arrive_and_wait(); cancel.join(); kitchen.join();
            const auto status = Status(server.GetOrder(0, request.orderId));
            Check(errors == 0 && ((cancelCode == SUCCESS && status == CANCELLED) || (cancelCode == ORDER_ALREADY_STARTED && status == STARTED)), "Cancel/start race violated state invariant.");
        }
    });
    std::cout << suite.passed << " passed, " << suite.failed << " failed.\n";
    return suite.failed ? 1 : 0;
}
