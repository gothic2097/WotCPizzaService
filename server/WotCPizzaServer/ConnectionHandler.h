#pragma once
#include "PizzaServer.h"
#include <atomic>

namespace pizza
{
    class ConnectionHandler
    {
    public:
        void Start(PizzaServer& server, std::uint16_t port, const std::atomic<bool>& stop);
    };
}
