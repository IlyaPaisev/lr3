#define TRANSPORT_EXPORTS
#include "SRMapPaisev.h"
#include "pch.h"

#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif

#include <windows.h>
#include <boost/asio.hpp>
#include <vector>

using boost::asio::ip::tcp;

namespace
{
    std::string WideToUtf8(const std::wstring& value)
    {
        if (value.empty())
            return std::string();
        std::wstring_convert<std::codecvt_utf8_utf16<wchar_t>> converter;
        return converter.to_bytes(value);
    }

    bool ReadExact(tcp::socket& socket, void* data, std::size_t size)
    {
        boost::system::error_code ec;
        boost::asio::read(socket, boost::asio::buffer(data, size), ec);
        return !ec;
    }

    bool WriteExact(tcp::socket& socket, const void* data, std::size_t size)
    {
        boost::system::error_code ec;
        boost::asio::write(socket, boost::asio::buffer(data, size), ec);
        return !ec;
    }
}

SRMapPaisev::SRMapPaisev(
    const wchar_t* hostName,
    const wchar_t* portName,
    const wchar_t*,
    const wchar_t*)
    : host(hostName ? hostName : L"127.0.0.1"),
      port(portName ? _wtoi(portName) : 54000),
      running(true)
{
    if (port <= 0)
        port = 54000;

    ioContext = std::make_unique<boost::asio::io_context>();

    socket = std::make_shared<tcp::socket>(*ioContext);
    tcp::resolver resolver(*ioContext);

    std::string hostNarrow = WideToUtf8(host);
    if (hostNarrow.empty())
        hostNarrow = "127.0.0.1";

    auto endpoints = resolver.resolve(hostNarrow, std::to_string(port));
    boost::asio::connect(*socket, endpoints);

    readerThread = std::make_unique<std::thread>(&SRMapPaisev::readerLoop, this);
}

SRMapPaisev::~SRMapPaisev()
{
    running.store(false);

    if (socket)
    {
        boost::system::error_code ignored;
        socket->shutdown(tcp::socket::shutdown_both, ignored);
        socket->close(ignored);
    }

    queueCv.notify_all();

    if (readerThread && readerThread->joinable())
        readerThread->join();
}

void SRMapPaisev::readerLoop()
{
    if (!socket)
        return;

    while (running.load())
    {
        MessageHeaderPaisev header{};
        if (!ReadExact(*socket, &header, sizeof(header)))
            break;

        std::wstring text;
        if (header.size > 0)
        {
            text.resize(header.size / static_cast<int>(sizeof(wchar_t)));
            if (!text.empty() && !ReadExact(*socket, &text[0], static_cast<std::size_t>(header.size)))
                break;
        }

        MessagePaisev msg;
        msg.header = header;
        msg.data = text;

        {
            std::lock_guard<std::mutex> lock(queueMutex);
            incoming.push(msg);
        }
        queueCv.notify_all();
    }

    running.store(false);
    queueCv.notify_all();
}

void SRMapPaisev::send(MessagePaisev& msg) const
{
    msg.refreshSize();

    if (!socket)
        return;

    std::lock_guard<std::mutex> lock(sendMutex);
    WriteExact(*socket, &msg.header, sizeof(msg.header));
    if (msg.header.size > 0)
        WriteExact(*socket, msg.data.data(), static_cast<std::size_t>(msg.header.size));
}

void SRMapPaisev::sendConfirmation(MessagePaisev& msg) const
{
    send(msg);
}

void SRMapPaisev::receive(MessagePaisev& msg) const
{
    std::unique_lock<std::mutex> lock(queueMutex);
    queueCv.wait(lock, [this]() { return !incoming.empty() || !running.load(); });

    if (incoming.empty())
    {
        msg = MessagePaisev();
        return;
    }

    msg = incoming.front();
    incoming.pop();
}

void SRMapPaisev::waitForMessage() const
{
    std::unique_lock<std::mutex> lock(queueMutex);
    queueCv.wait(lock, [this]() { return !incoming.empty() || !running.load(); });
}

void SRMapPaisev::waitForProcessed() const
{
    waitForMessage();
}

extern "C"
{
    SRMAP_API void* __cdecl CreateSRMapPaisev(
        const wchar_t* mapName,
        const wchar_t* mutexName,
        const wchar_t* messageEventName,
        const wchar_t* processedEventName)
    {
        try
        {
            return new SRMapPaisev(mapName, mutexName, messageEventName, processedEventName);
        }
        catch (...)
        {
            return nullptr;
        }
    }

    SRMAP_API void __cdecl DestroySRMapPaisev(void* map)
    {
        delete static_cast<SRMapPaisev*>(map);
    }

    SRMAP_API int __cdecl SRMapSendCommandW(
        void* map,
        int to,
        int messageType,
        const wchar_t* data,
        int status,
        int auxId)
    {
        auto* sr = static_cast<SRMapPaisev*>(map);
        if (!sr)
            return 0;

        MessagePaisev msg(to, static_cast<MessageTypesPaisev>(messageType), data ? data : L"", status, auxId);
        sr->send(msg);
        return 1;
    }

    SRMAP_API int __cdecl SRMapSendConfirmationW(
        void* map,
        int to,
        int messageType,
        const wchar_t* data,
        int status,
        int auxId)
    {
        return SRMapSendCommandW(map, to, messageType, data, status, auxId);
    }

    SRMAP_API int __cdecl SRMapReceiveW(
        void* map,
        int* messageType,
        int* sizeBytes,
        int* to,
        int* status,
        int* auxId,
        wchar_t* buffer,
        int bufferChars)
    {
        auto* sr = static_cast<SRMapPaisev*>(map);
        if (!sr)
            return 0;

        MessagePaisev msg;
        sr->receive(msg);

        if (messageType) *messageType = msg.header.messageType;
        if (sizeBytes) *sizeBytes = msg.header.size;
        if (to) *to = msg.header.to;
        if (status) *status = msg.header.status;
        if (auxId) *auxId = msg.header.auxId;

        int charsCount = msg.header.size / static_cast<int>(sizeof(wchar_t));
        if (buffer && bufferChars > 0)
        {
            int copyChars = (charsCount < (bufferChars - 1)) ? charsCount : (bufferChars - 1);
            if (copyChars > 0)
                wmemcpy(buffer, msg.data.c_str(), copyChars);
            buffer[copyChars] = L'\0';
            return copyChars;
        }

        return charsCount;
    }

    SRMAP_API void __cdecl SRMapWaitForMessage(void* map)
    {
        auto* sr = static_cast<SRMapPaisev*>(map);
        if (sr)
            sr->waitForMessage();
    }

    SRMAP_API void __cdecl SRMapWaitForProcessed(void* map)
    {
        auto* sr = static_cast<SRMapPaisev*>(map);
        if (sr)
            sr->waitForProcessed();
    }
}
