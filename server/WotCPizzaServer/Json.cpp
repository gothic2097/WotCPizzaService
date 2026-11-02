#include "Json.h"
#include <Windows.h>
#include <algorithm>
#include <cmath>
#include <fstream>
#include <iterator>

namespace pizza
{
    namespace
    {
        [[noreturn]] void Invalid(std::wstring_view key)
        {
            throw Error(CONNECTION_INVALID_INPUT, "invalid_input", "Missing, invalid, or out-of-range field: " + winrt::to_string(key));
        }

        bool IsGuid(const std::string& value)
        {
            if (value.size() != 36) return false;
            for (std::size_t i = 0; i < value.size(); ++i)
            {
                if (i == 8 || i == 13 || i == 18 || i == 23)
                {
                    if (value[i] != '-') return false;
                }
                else if (!((value[i] >= '0' && value[i] <= '9') || (value[i] >= 'a' && value[i] <= 'f'))) return false;
            }
            return true;
        }
    }

    JsonObject ParseJson(const std::string& text)
    {
        if (text.empty() || text.size() > 1024 * 1024) throw Error(CONNECTION_INVALID_INPUT, "invalid_json", "JSON must be a nonempty object of at most 1 MiB.");
        // Validate UTF-8 before conversion; malformed input must not be silently replaced.
        if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0) == 0)
            throw Error(CONNECTION_INVALID_INPUT, "invalid_json", "Request is not valid UTF-8.");
        JsonObject result;
        if (!JsonObject::TryParse(winrt::to_hstring(text), result)) throw Error(CONNECTION_INVALID_INPUT, "invalid_json", "Expected a valid JSON object.");
        return result;
    }

    std::string JsonText(const JsonObject& object)
    {
        return winrt::to_string(object.Stringify());
    }

    JsonObject ReadJson(const std::filesystem::path& path)
    {
        std::ifstream stream(path, std::ios::binary);
        if (!stream) throw std::runtime_error("Cannot read " + path.string());
        // Storage may grow beyond the per-request bound.
        std::string text((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
        JsonObject result;
        if (!JsonObject::TryParse(winrt::to_hstring(text), result)) throw std::runtime_error("Invalid JSON in " + path.string());
        return result;
    }

    void WriteJsonAtomic(const std::filesystem::path& path, const JsonObject& object)
    {
        auto temporary = path;
        temporary += L".tmp";
        const auto text = JsonText(object);
        HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot create persistence temporary file.");
        std::size_t position = 0;
        bool success = true;
        while (position < text.size())
        {
            DWORD written = 0;
            const DWORD count = static_cast<DWORD>((std::min)(text.size() - position, std::size_t(1024 * 1024)));
            if (!WriteFile(file, text.data() + position, count, &written, nullptr) || written == 0)
            {
                success = false;
                break;
            }
            position += written;
        }
        if (success) success = FlushFileBuffers(file) != 0;
        CloseHandle(file);
        if (!success || !MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        {
            DeleteFileW(temporary.c_str());
            throw std::runtime_error("Cannot commit order storage; operation was not acknowledged.");
        }
    }

    IJsonValue Required(const JsonObject& object, std::wstring_view key, JsonValueType type)
    {
        if (!object.HasKey(key)) Invalid(key);
        auto value = object.Lookup(key);
        if (value.ValueType() != type) Invalid(key);
        return value;
    }

    std::string String(const JsonObject& object, std::wstring_view key)
    {
        auto value = winrt::to_string(Required(object, key, JsonValueType::String).GetString());
        if (value.empty() || value.size() > 4096) Invalid(key);
        return value;
    }

    std::int64_t Integer(const JsonObject& object, std::wstring_view key, std::int64_t min, std::int64_t max)
    {
        const double value = Required(object, key, JsonValueType::Number).GetNumber();
        if (!std::isfinite(value) || value < static_cast<double>(min) || value > static_cast<double>(max) || std::floor(value) != value) Invalid(key);
        return static_cast<std::int64_t>(value);
    }

    JsonArray Array(const JsonObject& object, std::wstring_view key, std::uint32_t max)
    {
        auto value = Required(object, key, JsonValueType::Array).GetArray();
        if (value.Size() > max) Invalid(key);
        return value;
    }

    JsonObject Object(const JsonObject& object, std::wstring_view key)
    {
        return Required(object, key, JsonValueType::Object).GetObject();
    }

    bool Boolean(const JsonObject& object, std::wstring_view key)
    {
        return Required(object, key, JsonValueType::Boolean).GetBoolean();
    }

    void Put(JsonObject& object, std::wstring_view key, const std::string& value)
    {
        object.Insert(key, JsonValue::CreateStringValue(winrt::to_hstring(value)));
    }

    void Put(JsonObject& object, std::wstring_view key, std::int64_t value)
    {
        object.Insert(key, JsonValue::CreateNumberValue(static_cast<double>(value)));
    }

    void Put(JsonObject& object, std::wstring_view key, bool value)
    {
        object.Insert(key, JsonValue::CreateBooleanValue(value));
    }

    RequestedCustomerOrder RequestFromJson(const JsonObject& object)
    {
        RequestedCustomerOrder request;
        request.orderId = String(object, L"orderId");
        std::transform(request.orderId.begin(), request.orderId.end(), request.orderId.begin(), [](unsigned char ch)
        {
            return static_cast<char>(std::tolower(ch));
        });
        if (!IsGuid(request.orderId)) Invalid(L"orderId (GUID)");
        request.menuVersion = String(object, L"menuVersion");
        request.tipCents = Integer(object, L"tipCents");
        request.tipBasisPoints = Integer(object, L"tipBasisPoints", 0, RateScale);
        if (request.tipBasisPoints > 0 && request.tipCents != 0) Invalid(L"tipCents (must be zero in percentage mode)");
        for (const auto& value : Array(object, L"pizzas"))
        {
            auto item = value.GetObject();
            PizzaOrder pizza;
            pizza.pizzaCode = static_cast<std::uint32_t>(Integer(item, L"pizzaCode"));
            pizza.count = static_cast<std::uint32_t>(Integer(item, L"count", 1, 100));
            for (const auto& categoryValue : Array(item, L"toppings"))
            {
                auto categoryObject = categoryValue.GetObject();
                ToppingCategoryOrder category;
                category.toppingBaseCode = static_cast<std::uint32_t>(Integer(categoryObject, L"toppingBaseCode"));
                for (const auto& topping : Array(categoryObject, L"toppings"))
                {
                    JsonObject wrapper;
                    wrapper.Insert(L"code", topping);
                    category.toppings.push_back(static_cast<std::uint32_t>(Integer(wrapper, L"code")));
                }
                std::sort(category.toppings.begin(), category.toppings.end());
                pizza.toppings.push_back(std::move(category));
            }
            std::sort(pizza.toppings.begin(), pizza.toppings.end(), [](const auto& a, const auto& b)
            {
                return a.toppingBaseCode < b.toppingBaseCode;
            });
            request.pizzas.push_back(std::move(pizza));
        }
        for (const auto& code : Array(object, L"promoCodes", 20))
        {
            JsonObject wrapper;
            wrapper.Insert(L"code", code);
            request.promoCodes.push_back(String(wrapper, L"code"));
        }
        std::sort(request.promoCodes.begin(), request.promoCodes.end());
        return request;
    }

    JsonObject ToJson(const RequestedCustomerOrder& request)
    {
        JsonObject object;
        Put(object, L"orderId", request.orderId);
        Put(object, L"menuVersion", request.menuVersion);
        Put(object, L"tipCents", request.tipCents);
        Put(object, L"tipBasisPoints", request.tipBasisPoints);
        JsonArray pizzas;
        for (const auto& pizza : request.pizzas)
        {
            JsonObject item;
            Put(item, L"pizzaCode", std::int64_t(pizza.pizzaCode));
            Put(item, L"count", std::int64_t(pizza.count));
            JsonArray categories;
            for (const auto& category : pizza.toppings)
            {
                JsonObject entry;
                Put(entry, L"toppingBaseCode", std::int64_t(category.toppingBaseCode));
                JsonArray toppings;
                for (auto code : category.toppings) toppings.Append(JsonValue::CreateNumberValue(code));
                entry.Insert(L"toppings", toppings);
                categories.Append(entry);
            }
            item.Insert(L"toppings", categories);
            pizzas.Append(item);
        }
        object.Insert(L"pizzas", pizzas);
        JsonArray promos;
        for (const auto& code : request.promoCodes) promos.Append(JsonValue::CreateStringValue(winrt::to_hstring(code)));
        object.Insert(L"promoCodes", promos);
        return object;
    }

    JsonObject ToJson(const Totals& totals)
    {
        JsonObject object;
        Put(object, L"subtotalCents", totals.subtotalCents);
        Put(object, L"discountCents", totals.discountCents);
        Put(object, L"totalBeforeTaxAndTipCents", totals.totalBeforeTaxAndTipCents);
        Put(object, L"taxCents", totals.taxCents);
        Put(object, L"tipCents", totals.tipCents);
        Put(object, L"totalAfterTaxAndTipCents", totals.totalAfterTaxAndTipCents);
        return object;
    }

    Totals TotalsFromJson(const JsonObject& object)
    {
        return { Integer(object, L"subtotalCents"), Integer(object, L"discountCents"), Integer(object, L"totalBeforeTaxAndTipCents"),
            Integer(object, L"taxCents"), Integer(object, L"tipCents"), Integer(object, L"totalAfterTaxAndTipCents") };
    }

    JsonObject ToJson(const CustomerOrder& order, std::int64_t now)
    {
        auto object = ToJson(order.request);
        Put(object, L"customerId", std::int64_t(order.customerId));
        Put(object, L"status", std::int64_t(order.status));
        Put(object, L"revision", std::int64_t(order.revision));
        Put(object, L"description", order.description);
        Put(object, L"timeTillReady", order.status == STARTED ? (std::max)(std::int64_t(0), order.readyAtUnixSeconds - now) : std::int64_t(order.status == NOT_STARTED ? order.cookingSeconds : 0));
        object.Insert(L"totals", ToJson(order.totals));
        return object;
    }

    JsonObject Response(ResultCode code)
    {
        JsonObject object;
        Put(object, L"resultCode", std::int64_t(code));
        return object;
    }

    JsonObject Failure(ResultCode code, const std::string& kind, const std::string& message)
    {
        auto object = Response(code);
        Put(object, L"error", kind);
        Put(object, L"message", message);
        return object;
    }
}
