#include "PizzaServer.h"
#include <algorithm>
#include <chrono>
#include <iostream>
#include <set>

namespace pizza
{
    namespace
    {
        void LogStatusChange(const CustomerOrder& order, const std::string& note = {})
        {
            const char* statusName = "UNKNOWN";
            switch (order.status)
            {
            case NOT_STARTED: statusName = "NOT_STARTED"; break;
            case STARTED: statusName = "STARTED"; break;
            case READY_FOR_PICK_UP: statusName = "READY_FOR_PICK_UP"; break;
            case DELIVERED: statusName = "DELIVERED"; break;
            case CANCELLED: statusName = "CANCELLED"; break;
            }
            std::cout << "Status Change - " << statusName
                << " - CustomerId: " << order.customerId << ", OrderId: " << order.request.orderId;
            if (!note.empty()) std::cout << ", Note: " << note;
            std::cout << "\n\n";
        }
    }

    std::int64_t UnixSeconds()
    {
        return std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count();
    }

    PizzaServer::PizzaServer(const std::filesystem::path& directory, std::function<std::int64_t()> timeSource)
        : storagePath(directory / L"orders.json"), clock(std::move(timeSource))
    {
        const auto config = ReadJson(directory / L"menu.json");
        menu = MenuFromJson(config);
        menuJson = JsonText(config);
        Load();
    }

    void PizzaServer::Load()
    {
        if (!std::filesystem::exists(storagePath)) return;
        auto root = ReadJson(storagePath);
        if (Integer(root, L"schemaVersion", 1, 1) != 1) throw std::runtime_error("Unsupported storage schema.");
        const auto savedMenu = Object(root, L"menu");
        if (String(savedMenu, L"menuVersion") == menu.version && JsonText(savedMenu) != menuJson)
            throw std::runtime_error("Menu changed without a new menuVersion. Bump menuVersion before starting.");
        State loaded;
        for (auto value : Array(root, L"orders", 100000))
        {
            auto object = value.GetObject();
            Entry entry;
            auto& order = entry.order;
            order.customerId = static_cast<std::uint32_t>(Integer(object, L"customerId", 0, UINT32_MAX));
            order.request = RequestFromJson(Object(object, L"request"));
            order.originalRequest = RequestFromJson(Object(object, L"originalRequest"));
            order.status = static_cast<OrderStatus>(Integer(object, L"status", NOT_STARTED, CANCELLED));
            const auto remainingTransitions = order.status == NOT_STARTED ? 3 : order.status == STARTED ? 2 : order.status == READY_FOR_PICK_UP ? 1 : 0;
            order.revision = Integer(object, L"revision", 1, MaxMoney - remainingTransitions);
            order.lastUpdateRevision = Integer(object, L"lastUpdateRevision", 0, MaxMoney);
            order.readyAtUnixSeconds = Integer(object, L"readyAtUnixSeconds", 0, 10'000'000'000LL);
            order.cookingSeconds = static_cast<std::uint32_t>(Integer(object, L"cookingSeconds", 1, 3'600'000));
            order.totals = TotalsFromJson(Object(object, L"totals"));
            order.description = winrt::to_string(Required(object, L"description", JsonValueType::String).GetString());
            entry.eligibleAt = Integer(object, L"eligibleAt", 0, 10'000'000'000LL);
            const auto& totals = order.totals;
            if (order.request.orderId != order.originalRequest.orderId || order.lastUpdateRevision >= order.revision ||
                totals.discountCents > totals.subtotalCents || totals.totalBeforeTaxAndTipCents != totals.subtotalCents - totals.discountCents ||
                totals.totalAfterTaxAndTipCents != totals.totalBeforeTaxAndTipCents + totals.taxCents + totals.tipCents ||
                order.description.empty() || (order.status == STARTED && order.readyAtUnixSeconds == 0))
                throw std::runtime_error("Order storage invariant failed. Refusing to discard or repair orders silently.");
            PizzaCount(order.request);
            PizzaCount(order.originalRequest);
            if (!loaded.orders.emplace(order.request.orderId, std::move(entry)).second) throw std::runtime_error("Duplicate stored order ID.");
        }
        std::set<std::string> queued;
        for (auto value : Array(root, L"queue", 100000))
        {
            auto id = winrt::to_string(value.GetString());
            if (!loaded.orders.contains(id) || loaded.orders.at(id).order.status != NOT_STARTED || !queued.insert(id).second)
                throw std::runtime_error("Invalid stored kitchen queue.");
            loaded.queue.push_back(id);
        }
        for (const auto& [id, entry] : loaded.orders)
            if (entry.order.status == NOT_STARTED && !queued.contains(id)) throw std::runtime_error("Pending order missing from stored queue.");
        state = std::move(loaded);
    }

    JsonObject PizzaServer::Serialize(const State& source) const
    {
        JsonObject root;
        Put(root, L"schemaVersion", std::int64_t(1));
        root.Insert(L"menu", ParseJson(menuJson));
        JsonArray orders;
        for (const auto& [id, entry] : source.orders)
        {
            const auto& order = entry.order;
            JsonObject object;
            Put(object, L"customerId", std::int64_t(order.customerId));
            object.Insert(L"request", ToJson(order.request));
            object.Insert(L"originalRequest", ToJson(order.originalRequest));
            object.Insert(L"totals", ToJson(order.totals));
            Put(object, L"status", std::int64_t(order.status));
            Put(object, L"revision", std::int64_t(order.revision));
            Put(object, L"lastUpdateRevision", std::int64_t(order.lastUpdateRevision));
            Put(object, L"readyAtUnixSeconds", order.readyAtUnixSeconds);
            Put(object, L"cookingSeconds", std::int64_t(order.cookingSeconds));
            Put(object, L"description", order.description);
            Put(object, L"eligibleAt", entry.eligibleAt);
            orders.Append(object);
        }
        root.Insert(L"orders", orders);
        JsonArray queue;
        for (const auto& id : source.queue) queue.Append(JsonValue::CreateStringValue(winrt::to_hstring(id)));
        root.Insert(L"queue", queue);
        return root;
    }

    void PizzaServer::Commit(State&& next)
    {
        try { WriteJsonAtomic(storagePath, Serialize(next)); }
        catch (const std::exception& error) { throw Error(FAIL, "storage_error", error.what()); }
        state = std::move(next);
    }

    void PizzaServer::Save()
    {
        std::lock_guard lock(mutex);
        Commit(State(state));
    }

    const CustomerOrder& PizzaServer::Find(std::uint32_t customerId, const std::string& id) const
    {
        auto found = state.orders.find(id);
        if (found == state.orders.end() || found->second.order.customerId != customerId) throw Error(FAIL, "not_found", "Order not found for this customer.");
        return found->second.order;
    }

    JsonObject PizzaServer::OrderResponse(const CustomerOrder& order, ResultCode code) const
    {
        auto response = Response(code);
        response.Insert(L"customerOrder", ToJson(order, clock()));
        return response;
    }

    CustomerOrder PizzaServer::Price(std::uint32_t customerId, const RequestedCustomerOrder& request) const
    {
        CustomerOrder order;
        order.customerId = customerId;
        order.request = request;
        order.originalRequest = request;
        order.totals = CalculatePrice(menu, request);
        order.description = DescribeOrder(menu, request, order.totals);
        order.cookingSeconds = PizzaCount(request) * menu.timePerPizzaSeconds;
        return order;
    }

    JsonObject PizzaServer::GetMenu() const
    {
        auto response = Response();
        response.Insert(L"menu", ParseJson(menuJson));
        return response;
    }

    JsonObject PizzaServer::GetOrder(std::uint32_t customerId, const std::string& id)
    {
        std::lock_guard lock(mutex);
        return OrderResponse(Find(customerId, id));
    }

    JsonObject PizzaServer::GetOrderStatus(std::uint32_t customerId, const std::string& id)
    {
        std::lock_guard lock(mutex);
        const auto& order = Find(customerId, id);
        auto response = Response();
        Put(response, L"status", std::int64_t(order.status));
        Put(response, L"revision", std::int64_t(order.revision));
        Put(response, L"timeTillReady", order.status == STARTED ? (std::max)(std::int64_t(0), order.readyAtUnixSeconds - clock()) : std::int64_t(order.status == NOT_STARTED ? order.cookingSeconds : 0));
        return response;
    }

    JsonObject PizzaServer::GetAllOrders(std::uint32_t customerId)
    {
        std::lock_guard lock(mutex);
        JsonArray orders;
        for (const auto& [id, entry] : state.orders)
            if (entry.order.customerId == customerId) orders.Append(ToJson(entry.order, clock()));
        if (orders.Size() == 0) throw Error(FAIL, "not_found", "No orders found for this customer.");
        auto response = Response();
        response.Insert(L"customerOrders", orders);
        return response;
    }

    JsonObject PizzaServer::PlaceOrder(std::uint32_t customerId, const RequestedCustomerOrder& request)
    {
        std::lock_guard lock(mutex);
        auto found = state.orders.find(request.orderId);
        if (found != state.orders.end())
        {
            const auto& existing = Find(customerId, request.orderId);
            if (existing.originalRequest != request) throw Error(FAIL, "idempotency_conflict", "Order ID already belongs to a different submission. Fetch the existing order; use UpdateOrder to change it.");
            return OrderResponse(existing, ORDER_ALREADY_CREATED);
        }
        if (state.orders.size() >= 100000) throw Error(FAIL, "capacity", "Order storage limit reached.");
        Entry entry{ Price(customerId, request), clock() + menu.startDelaySeconds };
        auto response = OrderResponse(entry.order);
        State next = state;
        next.orders.emplace(request.orderId, std::move(entry));
        next.queue.push_back(request.orderId);
        Commit(std::move(next));
        LogStatusChange(state.orders.at(request.orderId).order);
        return response;
    }

    JsonObject PizzaServer::UpdateOrder(std::uint32_t customerId, const RequestedCustomerOrder& request, std::uint64_t expectedRevision)
    {
        std::lock_guard lock(mutex);
        const auto& old = Find(customerId, request.orderId);
        if (old.lastUpdateRevision == expectedRevision && old.request == request) return OrderResponse(old);
        if (old.status != NOT_STARTED)
        {
            auto response = OrderResponse(old, ORDER_ALREADY_STARTED);
            Put(response, L"message", std::string("Only Not Started orders can be updated."));
            return response;
        }
        if (old.revision != expectedRevision) throw Error(FAIL, "revision_conflict", "Order changed. Fetch the current order before editing again.");
        if (old.revision >= MaxMoney - 3) throw Error(FAIL, "revision_limit", "Order update limit reached; remaining revisions are reserved for kitchen transitions.");
        auto changed = Price(customerId, request);
        changed.originalRequest = old.originalRequest;
        changed.revision = old.revision + 1;
        changed.lastUpdateRevision = expectedRevision;
        auto response = OrderResponse(changed);
        State next = state;
        next.orders.at(request.orderId).order = std::move(changed);
        Commit(std::move(next));
        return response;
    }

    JsonObject PizzaServer::CancelOrder(std::uint32_t customerId, const std::string& id)
    {
        std::lock_guard lock(mutex);
        const auto& old = Find(customerId, id);
        if (old.status == CANCELLED) return OrderResponse(old);
        if (old.status != NOT_STARTED)
        {
            auto response = OrderResponse(old, ORDER_ALREADY_STARTED);
            Put(response, L"message", std::string("The kitchen has started this order; cancellation is not allowed."));
            return response;
        }
        State next = state;
        auto& changed = next.orders.at(id).order;
        changed.status = CANCELLED;
        ++changed.revision;
        std::erase(next.queue, id);
        auto response = OrderResponse(changed);
        Commit(std::move(next));
        LogStatusChange(state.orders.at(id).order);
        return response;
    }

    JsonObject PizzaServer::CompleteOrder(std::uint32_t customerId, const std::string& id)
    {
        std::lock_guard lock(mutex);
        const auto& old = Find(customerId, id);
        if (old.status == DELIVERED) return OrderResponse(old);
        if (old.status != READY_FOR_PICK_UP) throw Error(FAIL, "invalid_state", "Only a Ready for Pickup order can be completed.");
        State next = state;
        auto& changed = next.orders.at(id).order;
        changed.status = DELIVERED;
        ++changed.revision;
        auto response = OrderResponse(changed);
        Commit(std::move(next));
        LogStatusChange(state.orders.at(id).order);
        return response;
    }

    JsonObject PizzaServer::IsPromoValid(const RequestedCustomerOrder& request, const std::string& code) const
    {
        auto candidate = request;
        if (std::find(candidate.promoCodes.begin(), candidate.promoCodes.end(), code) == candidate.promoCodes.end()) candidate.promoCodes.push_back(code);
        const auto totals = CalculatePrice(menu, candidate);
        auto response = Response(PROMO_VALID);
        response.Insert(L"totals", ToJson(totals));
        return response;
    }

    void PizzaServer::UpdateServer()
    {
        std::lock_guard lock(mutex);
        const auto now = clock();
        State next = state;
        bool changed = false;
        std::uint32_t cooking = 0;
        std::vector<std::string> ready;
        std::vector<std::string> started;
        for (auto& [id, entry] : next.orders)
        {
            auto& order = entry.order;
            if (order.status != STARTED) continue;
            if (order.readyAtUnixSeconds <= now)
            {
                order.status = READY_FOR_PICK_UP;
                ++order.revision;
                ready.push_back(id);
                changed = true;
            }
            else ++cooking;
        }
        while (cooking < menu.kitchenCapacity && !next.queue.empty())
        {
            const auto id = next.queue.front();
            auto& entry = next.orders.at(id);
            if (entry.eligibleAt > now) break;
            next.queue.pop_front();
            entry.order.status = STARTED;
            entry.order.readyAtUnixSeconds = now + entry.order.cookingSeconds;
            ++entry.order.revision;
            started.push_back(id);
            ++cooking;
            changed = true;
        }
        if (!changed) return;
        Commit(std::move(next));
        for (const auto& id : ready)
        {
            LogStatusChange(state.orders.at(id).order);
        }
        for (const auto& id : started)
        {
            const auto& order = state.orders.at(id).order;
            LogStatusChange(order, "simulated charge " + std::to_string(order.totals.totalAfterTaxAndTipCents) + " cents");
        }
    }

    JsonObject PizzaServer::Handle(const JsonObject& request)
    {
        try
        {
            const auto number = Integer(request, L"FunctionId", -1, 1000000);
            if (number < 0 || number > 8) return Failure(CONNECTION_INVALID_CALL, "invalid_call", "FunctionId must be between 0 and 8.");
            const auto function = static_cast<FunctionId>(number);
            if (function == FunctionId::GetMenu) return GetMenu();
            const auto input = Object(request, L"Input");
            const auto customerId = static_cast<std::uint32_t>(Integer(input, L"customerId", 0, UINT32_MAX));
            if (function == FunctionId::GetAllOrders) return GetAllOrders(customerId);
            if (function == FunctionId::PlaceOrder || function == FunctionId::UpdateOrder || function == FunctionId::IsPromoValid)
            {
                auto order = RequestFromJson(Object(input, L"customerOrder"));
                if (function == FunctionId::PlaceOrder) return PlaceOrder(customerId, order);
                if (function == FunctionId::UpdateOrder) return UpdateOrder(customerId, order, Integer(input, L"expectedRevision", 1, MaxMoney));
                return IsPromoValid(order, String(input, L"promoCode"));
            }
            auto id = String(input, L"orderId");
            std::transform(id.begin(), id.end(), id.begin(), [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
            switch (function)
            {
            case FunctionId::GetOrder: return GetOrder(customerId, id);
            case FunctionId::GetOrderStatus: return GetOrderStatus(customerId, id);
            case FunctionId::CancelOrder: return CancelOrder(customerId, id);
            case FunctionId::CompleteOrder: return CompleteOrder(customerId, id);
            default: return Failure(CONNECTION_INVALID_CALL, "invalid_call", "Unknown function.");
            }
        }
        catch (const Error& error) { return Failure(error.code, error.kind, error.what()); }
        catch (const winrt::hresult_error&) { return Failure(CONNECTION_INVALID_INPUT, "invalid_input", "JSON value has the wrong type."); }
        catch (const std::exception&) { return Failure(FAIL, "internal_error", "The operation could not be completed."); }
    }
}
