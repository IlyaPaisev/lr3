#pragma once
#include <string>

enum MessageTypesPaisev
{
    MT_SEND_TEXT = 1,
    MT_CREATE_THREAD = 2,
    MT_STOP_THREAD = 3,
    MT_SHUTDOWN = 4,
    MT_CONFIRM = 5,
    MT_DISCONNECT = 6
};

enum TargetIdsPaisev
{
    TARGET_ALL_THREADS = 0,
    TARGET_MAIN_THREAD = -1
};

struct MessageHeaderPaisev
{
    int messageType = 0;
    int size = 0;
    int to = 0;
    int status = 0;
    int auxId = 0;
};

class ISenderPaisev;
class IReceiverPaisev;

struct MessagePaisev
{
    MessageHeaderPaisev header{};
    std::wstring data;

    MessagePaisev() = default;
    MessagePaisev(MessageTypesPaisev messageType, const std::wstring& text = L"");
    MessagePaisev(int toValue, MessageTypesPaisev messageType, const std::wstring& text, int statusValue = 0, int auxIdValue = 0);

    void refreshSize();

    void send(const ISenderPaisev& sender);
    void sendConfirmation(const ISenderPaisev& sender);
    void receive(const IReceiverPaisev& receiver);
};

class ISenderPaisev
{
public:
    virtual ~ISenderPaisev() = default;
    virtual void send(MessagePaisev& msg) const = 0;
    virtual void sendConfirmation(MessagePaisev& msg) const = 0;
};

class IReceiverPaisev
{
public:
    virtual ~IReceiverPaisev() = default;
    virtual void receive(MessagePaisev& msg) const = 0;
};
