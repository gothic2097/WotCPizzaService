#include "Pricing.h"
#include <algorithm>
#include <iomanip>
#include <set>
#include <sstream>

namespace pizza
{
    namespace
    {
        [[noreturn]] void Invalid(const std::string& message) { throw Error(CONNECTION_INVALID_INPUT, "invalid_order", message); }

        [[noreturn]] void InvalidPromo(const std::string& code, const std::string& message) { throw Error(PROMO_INVALID, "invalid_promo", code + ": " + message); }

        Money Bounded(Money value)
        {
            if (value < 0 || value > MaxMoney) Invalid("Order amount exceeds the supported limit.");
            return value;
        }

        std::string Dollars(Money value)
        {
            std::ostringstream stream;
            stream << '$' << value / 100 << '.' << std::setw(2) << std::setfill('0') << value % 100;
            return stream.str();
        }

        std::string Name(const JsonObject& object)
        {
            auto value = String(object, L"name");
            if (value.size() > 80 || value.find_first_of("\r\n") != std::string::npos) Invalid("Menu names must be at most 80 bytes without newlines.");
            return value;
        }
    }

    Menu MenuFromJson(const JsonObject& object)
    {
        Menu menu;
        menu.version = String(object, L"menuVersion");
        if (menu.version.size() > 80) Invalid("Menu version is too long.");
        menu.taxBasisPoints = Integer(object, L"taxBasisPoints", 0, RateScale);
        auto settings = Object(object, L"settings");
        menu.kitchenCapacity = static_cast<std::uint32_t>(Integer(settings, L"kitchenHeapCapacity", 1, 100));
        menu.timePerPizzaSeconds = static_cast<std::uint32_t>(Integer(settings, L"timePerPizzaInSeconds", 1, 3600));
        menu.startDelaySeconds = static_cast<std::uint32_t>(Integer(settings, L"startDelaySeconds", 1, 3600));
        for (auto value : Array(object, L"pizzas"))
        {
            auto item = value.GetObject();
            PizzaDisplayItem pizza;
            pizza.code = static_cast<std::uint32_t>(Integer(item, L"code"));
            pizza.name = Name(item);
            pizza.costCents = Integer(item, L"costCents", 1, MaxMoney);
            if (!menu.pizzas.emplace(pizza.code, pizza).second) Invalid("Duplicate pizza code in menu.");
        }
        if (menu.pizzas.empty()) Invalid("Menu requires at least one pizza size.");
        for (auto value : Array(object, L"toppings"))
        {
            auto item = value.GetObject();
            ToppingCategoryDisplay category;
            category.code = static_cast<std::uint32_t>(Integer(item, L"code"));
            category.name = Name(item);
            category.costBasisPoints = Integer(item, L"costBasisPoints", 0, RateScale);
            for (auto entry : Array(item, L"toppings"))
            {
                auto topping = entry.GetObject();
                const auto code = static_cast<std::uint32_t>(Integer(topping, L"code"));
                if (!category.toppings.emplace(code, Name(topping)).second) Invalid("Duplicate topping code in category.");
            }
            if (!menu.toppings.emplace(category.code, category).second) Invalid("Duplicate topping category.");
        }
        for (auto value : Array(object, L"promos", 20))
        {
            auto item = value.GetObject();
            Promo promo;
            promo.code = String(item, L"code");
            promo.isPercentOff = Boolean(item, L"isPercentOff");
            promo.discount = Integer(item, promo.isPercentOff ? L"discountBasisPoints" : L"discountCents", 0, promo.isPercentOff ? RateScale : MaxMoney);
            for (auto ruleValue : Array(item, L"validations", 20))
            {
                auto ruleObject = ruleValue.GetObject();
                PromoValidation rule;
                rule.type = static_cast<PromoValidationType>(Integer(ruleObject, L"type", 0, 3));
                if (rule.type == N_PER_CUSTOMER) Invalid("N_PER_CUSTOMER is deliberately unsupported; remove this rule.");
                if (rule.type == TOTAL_OVER_N) rule.thresholdCents = Integer(ruleObject, L"thresholdCents");
                if (rule.type == NOT_VALID_WITH_OTHER_PROMOS)
                {
                    for (auto conflict : Array(ruleObject, L"arrayOfStrings", 20))
                    {
                        JsonObject wrapper;
                        wrapper.Insert(L"code", conflict);
                        rule.arrayOfStrings.push_back(String(wrapper, L"code"));
                    }
                }
                promo.validations.push_back(std::move(rule));
            }
            if (!menu.promos.emplace(promo.code, promo).second) Invalid("Duplicate promo code.");
        }
        for (const auto& [code, promo] : menu.promos)
            for (const auto& rule : promo.validations)
                for (const auto& conflict : rule.arrayOfStrings)
                    if (!menu.promos.contains(conflict) || conflict == code) Invalid("Promo conflict references an unknown code or itself.");
        return menu;
    }

    std::uint32_t PizzaCount(const RequestedCustomerOrder& request)
    {
        std::uint32_t count = 0;
        if (request.pizzas.empty() || request.pizzas.size() > 100) Invalid("An order requires 1 to 100 line items.");
        for (const auto& pizza : request.pizzas)
        {
            if (pizza.count == 0 || pizza.count > 100) Invalid("Each quantity must be between 1 and 100.");
            count += pizza.count;
        }
        if (count > 1000) Invalid("At most 1000 pizzas are allowed per order.");
        return count;
    }

    Totals CalculatePrice(const Menu& menu, const RequestedCustomerOrder& request)
    {
        if (request.menuVersion != menu.version) throw Error(FAIL, "stale_menu", "Menu changed. Fetch GetMenu, recalculate, and confirm the new price before retrying.");
        PizzaCount(request);
        if (request.tipCents < 0 || request.tipCents > MaxMoney || request.tipBasisPoints < 0 || request.tipBasisPoints > RateScale || (request.tipBasisPoints && request.tipCents))
            Invalid("Choose one nonnegative tip mode; percentage tips cannot exceed 100%.");
        Totals totals;
        for (const auto& pizza : request.pizzas)
        {
            auto base = menu.pizzas.find(pizza.pizzaCode);
            if (base == menu.pizzas.end()) Invalid("Unknown pizza code: " + std::to_string(pizza.pizzaCode));
            Money unit = base->second.costCents;
            std::set<std::uint32_t> categories;
            for (const auto& selection : pizza.toppings)
            {
                auto category = menu.toppings.find(selection.toppingBaseCode);
                if (category == menu.toppings.end() || !categories.insert(selection.toppingBaseCode).second) Invalid("Unknown or duplicate topping category.");
                std::set<std::uint32_t> toppings;
                for (auto code : selection.toppings)
                {
                    if (!category->second.toppings.contains(code) || !toppings.insert(code).second) Invalid("Unknown or duplicate topping in category " + category->second.name + '.');
                    unit = Bounded(unit + base->second.costCents * category->second.costBasisPoints / RateScale);
                }
            }
            totals.subtotalCents = Bounded(totals.subtotalCents + unit * pizza.count);
        }
        std::set<std::string> codes;
        for (const auto& code : request.promoCodes)
        {
            if (!codes.insert(code).second) InvalidPromo(code, "Each promo may be used only once per order.");
            if (!menu.promos.contains(code)) InvalidPromo(code, "Unknown promo code.");
        }
        for (const auto& code : codes)
        {
            for (const auto& rule : menu.promos.at(code).validations)
            {
                if (rule.type == TOTAL_OVER_N && totals.subtotalCents <= rule.thresholdCents) InvalidPromo(code, "Subtotal must be strictly greater than " + Dollars(rule.thresholdCents) + '.');
                if (rule.type == NOT_VALID_WITH_OTHER_PROMOS)
                    for (const auto& other : rule.arrayOfStrings)
                        if (codes.contains(other)) InvalidPromo(code, "Cannot be combined with " + other + '.');
            }
        }
        Money discounted = totals.subtotalCents;
        // Lexical order makes combinations independent of the order codes arrived in.
        for (const auto& code : codes)
        {
            const auto& promo = menu.promos.at(code);
            const Money discount = promo.isPercentOff ? discounted * promo.discount / RateScale : (std::min)(discounted, promo.discount);
            discounted -= discount;
        }
        totals.discountCents = totals.subtotalCents - discounted;
        totals.totalBeforeTaxAndTipCents = discounted;
        totals.taxCents = discounted * menu.taxBasisPoints / RateScale;
        totals.tipCents = request.tipBasisPoints ? (discounted + totals.taxCents) * request.tipBasisPoints / RateScale : request.tipCents;
        totals.totalAfterTaxAndTipCents = Bounded(discounted + totals.taxCents + totals.tipCents);
        return totals;
    }

    std::string DescribeOrder(const Menu& menu, const RequestedCustomerOrder& request, const Totals& totals)
    {
        std::ostringstream text;
        for (const auto& pizza : request.pizzas)
        {
            const auto& base = menu.pizzas.at(pizza.pizzaCode);
            Money unit = base.costCents;
            text << pizza.count << " x " << base.name << " (base " << Dollars(base.costCents) << ")";
            bool any = false;
            for (const auto& selection : pizza.toppings)
                for (auto code : selection.toppings)
                {
                    const auto& category = menu.toppings.at(selection.toppingBaseCode);
                    const Money toppingCost = base.costCents * category.costBasisPoints / RateScale;
                    text << (any ? ", " : ": ") << category.toppings.at(code) << " (" << Dollars(toppingCost) << ')';
                    unit += toppingCost;
                    any = true;
                }
            if (!any) text << ": no toppings";
            text << "\n  " << Dollars(unit) << " each; line " << Dollars(unit * pizza.count) << '\n';
        }
        text << "Subtotal: " << Dollars(totals.subtotalCents) << "\nDiscount: " << Dollars(totals.discountCents);
        for (const auto& code : request.promoCodes) text << " [" << code << ']';
        text << "\nBefore tax and tip: " << Dollars(totals.totalBeforeTaxAndTipCents)
            << "\nTax: " << Dollars(totals.taxCents) << "\nTip: " << Dollars(totals.tipCents)
            << "\nTotal: " << Dollars(totals.totalAfterTaxAndTipCents) << '\n';
        return text.str();
    }
}
