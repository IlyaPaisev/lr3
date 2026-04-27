#pragma once
#include <deque>
#include <mutex>
#include <condition_variable>
#include <atomic>
#include "MessagePaisev.h"

class Session
{
private:
    std::deque<MessagePaisev> messages;
    mutable std::mutex mtx;
    std::condition_variable cv;
    std::atomic<bool> running;

public:
    int sessionID;

    explicit Session(int id);
    ~Session() = default;

    void addMessage(const MessagePaisev& msg);
    bool getMessage(MessagePaisev& msg);
    void stop();
    bool isRunning() const;

    Session(const Session&) = delete;
    Session& operator=(const Session&) = delete;
};