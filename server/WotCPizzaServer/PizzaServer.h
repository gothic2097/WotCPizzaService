#pragma once
#include "Pricing.h"
#include <deque>
#include <functional>
#include <mutex>

namespace pizza
{
    std::int64_t UnixSeconds();

    class PizzaServer
    {
    public:
        explicit PizzaServer(const std::filesystem::path& directory, std::function<std::int64_t()> clock = UnixSeconds);
        JsonObject Handle(const JsonObject& request);
        JsonObject GetMenu() const;
        JsonObject GetOrder(std::uint32_t customerId, const std::string& orderId);
        JsonObject GetOrderStatus(std::uint32_t customerId, const std::string& orderId);
        JsonObject GetAllOrders(std::uint32_t customerId);
        JsonObject PlaceOrder(std::uint32_t customerId, const RequestedCustomerOrder& request);
        JsonObject UpdateOrder(std::uint32_t customerId, const RequestedCustomerOrder& request, std::uint64_t expectedRevision);
        JsonObject CancelOrder(std::uint32_t customerId, const std::string& orderId);
        JsonObject CompleteOrder(std::uint32_t customerId, const std::string& orderId);
        JsonObject IsPromoValid(const RequestedCustomerOrder& request, const std::string& code) const;
        void UpdateServer();
        void Save();

    private:
        struct Entry { CustomerOrder order; std::int64_t eligibleAt{}; };

        struct State
        {
            std::map<std::string, Entry> orders;
            std::deque<std::string> queue;
        };

        std::filesystem::path storagePath;
        Menu menu;
        std::string menuJson;
        State state;
        std::mutex mutex;
        std::function<std::int64_t()> clock;

        void Load();
        void Commit(State&& next);
        JsonObject Serialize(const State& source) const;
        const CustomerOrder& Find(std::uint32_t customerId, const std::string& orderId) const;
        JsonObject OrderResponse(const CustomerOrder& order, ResultCode code = SUCCESS) const;
        CustomerOrder Price(std::uint32_t customerId, const RequestedCustomerOrder& request) const;
    };
}
