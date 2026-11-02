#include <winsock2.h>
#include <ws2tcpip.h>
#include "ConnectionHandler.h"
#include <algorithm>
#include <chrono>
#include <iostream>
#include <memory>
#include <thread>

namespace pizza
{
    namespace
    {
        struct Socket
        {
            SOCKET value{ INVALID_SOCKET };
            ~Socket() { if (value != INVALID_SOCKET) closesocket(value); }
        };

        struct Winsock
        {
            Winsock()
            {
                WSADATA data{};
                if (WSAStartup(MAKEWORD(2, 2), &data) != 0) throw std::runtime_error("WSAStartup failed.");
            }

            ~Winsock() { WSACleanup(); }
        };

        using SteadyClock = std::chrono::steady_clock;
        void Timeout(SOCKET socket, int option, SteadyClock::time_point deadline)
        {
            const auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - SteadyClock::now()).count();
            if (remaining <= 0) throw Error(CONNECTION_ERROR, "timeout", "Connection timed out.");
            const DWORD timeout = static_cast<DWORD>(remaining);
            if (setsockopt(socket, SOL_SOCKET, option, reinterpret_cast<const char*>(&timeout), sizeof(timeout)) == SOCKET_ERROR)
                throw Error(CONNECTION_ERROR, "socket_error", "Unable to configure socket timeout.");
        }

        void Send(SOCKET socket, const std::string& text)
        {
            std::size_t offset = 0;
            const auto deadline = SteadyClock::now() + std::chrono::seconds(5);
            while (offset < text.size())
            {
                Timeout(socket, SO_SNDTIMEO, deadline);
                const int count = send(socket, text.data() + offset, static_cast<int>((std::min)(text.size() - offset, std::size_t(65536))), 0);
                if (count <= 0) throw Error(CONNECTION_ERROR, "connection_error", "Client disconnected while receiving the response.");
                offset += count;
            }
        }

        std::string Receive(SOCKET socket)
        {
            const auto deadline = SteadyClock::now() + std::chrono::seconds(5);
            std::string text;
            char buffer[4096];
            while (true)
            {
                Timeout(socket, SO_RCVTIMEO, deadline);
                const int count = recv(socket, buffer, sizeof(buffer), 0);
                if (count <= 0) throw Error(CONNECTION_ERROR, "incomplete_request", "Expected a complete JSON request followed by newline within five seconds.");
                text.append(buffer, count);
                if (text.size() > 1024 * 1024) throw Error(CONNECTION_INVALID_INPUT, "request_too_large", "Request exceeds 1 MiB.");
                const auto newline = text.find('\n');
                if (newline != std::string::npos)
                {
                    if (text.find_first_not_of(" \t\r\n", newline + 1) != std::string::npos)
                        throw Error(CONNECTION_INVALID_INPUT, "invalid_frame", "Only one request is allowed per connection.");
                    text.resize(newline);
                    return text;
                }
            }
        }

        void HandleClient(SOCKET value, PizzaServer& server) noexcept
        {
            Socket socket{ value };
            try
            {
                winrt::init_apartment(winrt::apartment_type::multi_threaded);
                struct Apartment { ~Apartment() { winrt::uninit_apartment(); } } apartment;
                JsonObject response;
                try { response = server.Handle(ParseJson(Receive(value))); }
                catch (const Error& error) { response = Failure(error.code, error.kind, error.what()); }
                catch (const winrt::hresult_error&) { response = Failure(CONNECTION_INVALID_INPUT, "invalid_json", "Invalid JSON request."); }
                Send(value, JsonText(response) + '\n');
                shutdown(value, SD_SEND);
            }
            catch (const std::exception& error) { std::cerr << "Client connection: " << error.what() << '\n'; }
            catch (...) { std::cerr << "Client connection failed.\n"; }
        }
        struct Worker
        {
            std::jthread thread;
            std::shared_ptr<std::atomic<bool>> done;
        };
    }

    void ConnectionHandler::Start(PizzaServer& server, std::uint16_t port, const std::atomic<bool>& stop)
    {
        Winsock winsock;
        Socket listener{ socket(AF_INET, SOCK_STREAM, IPPROTO_TCP) };
        if (listener.value == INVALID_SOCKET) throw std::runtime_error("Cannot create listener socket.");
        const BOOL exclusive = TRUE;
        if (setsockopt(listener.value, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive)) == SOCKET_ERROR)
            throw std::runtime_error("Cannot configure exclusive listener.");
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = htons(port);
        if (bind(listener.value, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR || listen(listener.value, SOMAXCONN) == SOCKET_ERROR)
            throw std::runtime_error("Cannot bind/listen on 127.0.0.1:" + std::to_string(port));
        std::vector<Worker> workers;
        // Reserve before starting threads so allocation cannot strand an accepted socket.
        workers.reserve(64);
        std::cout << "Pizza server listening on 127.0.0.1:" << port << ". Ctrl+C to stop.\n" << std::flush;
        auto nextKitchenTick = SteadyClock::now();
        while (!stop.load())
        {
            std::erase_if(workers, [](Worker& worker) { return worker.done->load(); });
            if (SteadyClock::now() >= nextKitchenTick)
            {
                try { server.UpdateServer(); }
                catch (const std::exception& error) { std::cerr << "Kitchen update not committed: " << error.what() << '\n'; }
                nextKitchenTick = SteadyClock::now() + std::chrono::milliseconds(500);
            }
            fd_set readable;
            FD_ZERO(&readable);
            FD_SET(listener.value, &readable);
            timeval timeout{ 0, 200000 };
            const int ready = select(0, &readable, nullptr, nullptr, &timeout);
            if (ready == SOCKET_ERROR) throw std::runtime_error("Listener select failed.");
            if (ready == 0) continue;
            SOCKET client = accept(listener.value, nullptr, nullptr);
            if (client == INVALID_SOCKET) continue;
            if (workers.size() >= 64) { closesocket(client); continue; }
            try
            {
                auto done = std::make_shared<std::atomic<bool>>(false);
                std::jthread thread([client, &server, done] { HandleClient(client, server); done->store(true); });
                workers.push_back({ std::move(thread), std::move(done) });
            }
            catch (...) { closesocket(client); throw; }
        }
        // jthread joins all in-flight requests before server or Winsock can be destroyed.
    }
}
