#pragma once

#include "Models.h"
#include <filesystem>
#include <Windows.h>
#ifdef GetObject
#undef GetObject
#endif
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Data.Json.h>

namespace pizza
{
    using winrt::Windows::Data::Json::JsonObject;
    using winrt::Windows::Data::Json::JsonArray;
    using winrt::Windows::Data::Json::JsonValue;
    using winrt::Windows::Data::Json::JsonValueType;
    using winrt::Windows::Data::Json::IJsonValue;

    JsonObject ParseJson(const std::string& text);
    std::string JsonText(const JsonObject& object);
    JsonObject ReadJson(const std::filesystem::path& path);
    void WriteJsonAtomic(const std::filesystem::path& path, const JsonObject& object);
    IJsonValue Required(const JsonObject& object, std::wstring_view key, JsonValueType type);
    std::string String(const JsonObject& object, std::wstring_view key);
    std::int64_t Integer(const JsonObject& object, std::wstring_view key, std::int64_t min = 0, std::int64_t max = MaxMoney);
    JsonArray Array(const JsonObject& object, std::wstring_view key, std::uint32_t max = 100);
    JsonObject Object(const JsonObject& object, std::wstring_view key);
    bool Boolean(const JsonObject& object, std::wstring_view key);
    void Put(JsonObject& object, std::wstring_view key, const std::string& value);
    void Put(JsonObject& object, std::wstring_view key, std::int64_t value);
    void Put(JsonObject& object, std::wstring_view key, bool value);
    RequestedCustomerOrder RequestFromJson(const JsonObject& object);
    JsonObject ToJson(const RequestedCustomerOrder& request);
    JsonObject ToJson(const Totals& totals);
    Totals TotalsFromJson(const JsonObject& object);
    JsonObject ToJson(const CustomerOrder& order, std::int64_t now);
    JsonObject Failure(ResultCode code, const std::string& kind, const std::string& message);
    JsonObject Response(ResultCode code = SUCCESS);
}
