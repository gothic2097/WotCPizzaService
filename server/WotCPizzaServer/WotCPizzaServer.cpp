#include "ConnectionHandler.h"
#include <Windows.h>
#include <atomic>
#include <cstdint>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <string>
#include <winrt/base.h>

int RunSelfTests(const std::filesystem::path& executableDirectory);

namespace
{
    std::atomic<bool> stopRequested{ false };

    BOOL WINAPI Stop(DWORD event)
    {
        if (event != CTRL_C_EVENT && event != CTRL_BREAK_EVENT)
        {
            return FALSE;
        }
        stopRequested.store(true);
        return TRUE;
    }

    struct StoreLock
    {
        HANDLE file{ INVALID_HANDLE_VALUE };
        ~StoreLock()
        {
            if (file != INVALID_HANDLE_VALUE)
            {
                CloseHandle(file);
            }
        }
    };
}

int wmain(int argc, wchar_t* argv[])
{
    try
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        struct Apartment
        {
            ~Apartment()
            {
                winrt::uninit_apartment();
            }
        } apartment;

        std::wstring executable(32768, L'\0');
        const DWORD length = GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size()));
        if (length == 0 || length == executable.size())
        {
            throw std::runtime_error("Cannot determine executable directory.");
        }

        executable.resize(length);
        const auto executableDirectory = std::filesystem::path(executable).parent_path();
        auto directory = executableDirectory;
        std::uint16_t port = 5050;
        bool selfTest = false;

        for (int i = 1; i < argc; ++i)
        {
            const std::wstring argument = argv[i];
            if (argument == L"--self-test")
            {
                selfTest = true;
            }
            else if (argument == L"--data" && i + 1 < argc)
            {
                directory = std::filesystem::absolute(argv[++i]);
            }
            else if (argument == L"--port" && i + 1 < argc)
            {
                const std::wstring value = argv[++i];
                std::size_t consumed = 0;
                const int number = std::stoi(value, &consumed);
                if (consumed != value.size() || number < 1 || number > 65535)
                {
                    throw std::runtime_error("Port must be an integer from 1 to 65535.");
                }
                port = static_cast<std::uint16_t>(number);
            }
            else
            {
                throw std::runtime_error("Usage: WotCPizzaServer.exe [--port 5050] [--data directory] [--self-test]");
            }
        }

        if (selfTest)
        {
            return RunSelfTests(executableDirectory);
        }

        StoreLock lock;
        lock.file = CreateFileW((directory / L"orders.lock").c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (lock.file == INVALID_HANDLE_VALUE)
        {
            throw std::runtime_error("Cannot lock data directory. Check permissions and that no other server is using it.");
        }

        pizza::PizzaServer server(directory);
        if (!SetConsoleCtrlHandler(Stop, TRUE))
        {
            throw std::runtime_error("Cannot register shutdown handler.");
        }

        pizza::ConnectionHandler{}.Start(server, port, stopRequested);
        SetConsoleCtrlHandler(Stop, FALSE);
        return 0;
    }
    catch (const winrt::hresult_error& error)
    {
        std::cerr << "Windows JSON/API error: " << winrt::to_string(error.message()) << '\n';
    }
    catch (const std::exception& error)
    {
        std::cerr << "Error: " << error.what() << '\n';
    }
    return 1;
}
