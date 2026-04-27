#include "pch.h"
#include "MessagePaisev.h"

MessagePaisev::MessagePaisev(MessageTypesPaisev messageType, const std::wstring& text)
    : data(text)
{
    header.messageType = static_cast<int>(messageType);
    header.size = static_cast<int>(data.length() * sizeof(wchar_t));
}

MessagePaisev::MessagePaisev(int toValue, MessageTypesPaisev messageType, const std::wstring& text, int statusValue, int auxIdValue)
    : data(text)
{
    header.messageType = static_cast<int>(messageType);
    header.size = static_cast<int>(data.length() * sizeof(wchar_t));
    header.to = toValue;
    header.status = statusValue;
    header.auxId = auxIdValue;
}

void MessagePaisev::refreshSize()
{
    header.size = static_cast<int>(data.length() * sizeof(wchar_t));
}

void MessagePaisev::send(const ISenderPaisev& sender)
{
    refreshSize();
    sender.send(*this);
}

void MessagePaisev::sendConfirmation(const ISenderPaisev& sender)
{
    refreshSize();
    sender.sendConfirmation(*this);
}

void MessagePaisev::receive(const IReceiverPaisev& receiver)
{
    receiver.receive(*this);
}
