#pragma once
#include "Json.h"

namespace pizza
{
    Menu MenuFromJson(const JsonObject& object);
    Totals CalculatePrice(const Menu& menu, const RequestedCustomerOrder& request);
    std::string DescribeOrder(const Menu& menu, const RequestedCustomerOrder& request, const Totals& totals);
    std::uint32_t PizzaCount(const RequestedCustomerOrder& request);
}
