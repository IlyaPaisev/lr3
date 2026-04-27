#include "Session.h"

Session::Session(int id)
    : running(true), sessionID(id)
{
}

void Session::addMessage(const MessagePaisev& msg)
{
    {
        std::lock_guard<std::mutex> lock(mtx);
        messages.push_back(msg);
    }
    cv.notify_one();
}

bool Session::getMessage(MessagePaisev& msg)
{
    std::unique_lock<std::mutex> lock(mtx);

    cv.wait(lock, [this]()
        {
            return !messages.empty() || !running.load();
        });

    if (!running.load() && messages.empty())
        return false;

    msg = messages.front();
    messages.pop_front();
    return true;
}

void Session::stop()
{
    running.store(false);
    cv.notify_all();
}

bool Session::isRunning() const
{
    return running.load();
}