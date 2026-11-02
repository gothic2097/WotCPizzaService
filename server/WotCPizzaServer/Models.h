#pragma once

#include <cstdint>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>

namespace pizza
{
    using Money = std::int64_t;
    constexpr Money MaxMoney = 1'000'000'000;
    constexpr std::int64_t RateScale = 10'000;

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
    };

    enum OrderStatus
    {
        NOT_STARTED,
        STARTED,
        READY_FOR_PICK_UP,
        DELIVERED,
        CANCELLED
    };

    enum PromoValidationType
    {
        ONE_PROMO_PER_ORDER,
        NOT_VALID_WITH_OTHER_PROMOS,
        N_PER_CUSTOMER,
        TOTAL_OVER_N
    };

    enum class FunctionId
    {
        GetMenu,
        GetOrder,
        GetAllOrders,
        PlaceOrder,
        GetOrderStatus,
        UpdateOrder,
        CancelOrder,
        IsPromoValid,
        CompleteOrder
    };

    struct Error : std::runtime_error
    {
        ResultCode code;
        std::string kind;
        Error(ResultCode result, std::string category, const std::string& message)
            : std::runtime_error(message), code(result), kind(std::move(category)) {}
    };

    struct ToppingCategoryOrder
    {
        std::uint32_t toppingBaseCode{};
        std::vector<std::uint32_t> toppings;
        bool operator==(const ToppingCategoryOrder&) const = default;
    };

    struct PizzaOrder
    {
        std::uint32_t pizzaCode{};
        std::uint32_t count{};
        std::vector<ToppingCategoryOrder> toppings;
        bool operator==(const PizzaOrder&) const = default;
    };

    struct RequestedCustomerOrder
    {
        std::string orderId;
        std::string menuVersion;
        std::vector<PizzaOrder> pizzas;
        std::vector<std::string> promoCodes;
        Money tipCents{};
        std::int64_t tipBasisPoints{};
        bool operator==(const RequestedCustomerOrder&) const = default;
    };

    struct Totals
    {
        Money subtotalCents{};
        Money discountCents{};
        Money totalBeforeTaxAndTipCents{};
        Money taxCents{};
        Money tipCents{};
        Money totalAfterTaxAndTipCents{};
        bool operator==(const Totals&) const = default;
    };

    struct CustomerOrder
    {
        std::uint32_t customerId{};
        RequestedCustomerOrder request;
        RequestedCustomerOrder originalRequest;
        OrderStatus status{ NOT_STARTED };
        Totals totals;
        std::string description;
        std::uint64_t revision{ 1 };
        std::uint64_t lastUpdateRevision{};
        std::int64_t readyAtUnixSeconds{};
        std::uint32_t cookingSeconds{};
    };

    struct PizzaDisplayItem
    {
        std::uint32_t code{};
        std::string name;
        Money costCents{};
    };

    struct ToppingCategoryDisplay
    {
        std::uint32_t code{};
        std::string name;
        std::int64_t costBasisPoints{};
        std::map<std::uint32_t, std::string> toppings;
    };

    struct PromoValidation
    {
        PromoValidationType type{};
        std::vector<std::string> arrayOfStrings;
        Money thresholdCents{};
    };

    struct Promo
    {
        std::string code;
        bool isPercentOff{};
        std::int64_t discount{};
        std::vector<PromoValidation> validations;
    };

    struct Menu
    {
        std::string version;
        std::map<std::uint32_t, PizzaDisplayItem> pizzas;
        std::map<std::uint32_t, ToppingCategoryDisplay> toppings;
        std::map<std::string, Promo> promos;
        std::int64_t taxBasisPoints{};
        std::uint32_t kitchenCapacity{};
        std::uint32_t timePerPizzaSeconds{};
        std::uint32_t startDelaySeconds{};
    };
}
