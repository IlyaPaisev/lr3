#pragma once
#include <string>
#include <mutex>
#include <condition_variable>
#include <queue>
#include <thread>
#include <memory>
#include <atomic>
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif
#include <boost/asio.hpp>
#include "MessagePaisev.h"

#ifdef TRANSPORT_EXPORTS
#define SRMAP_API __declspec(dllexport)
#else
#define SRMAP_API __declspec(dllimport)
#endif

class SRMapPaisev : public ISenderPaisev, public IReceiverPaisev
{
private:
    std::wstring host;
    int port;

    mutable std::mutex sendMutex;
    mutable std::mutex queueMutex;
    mutable std::condition_variable queueCv;
    mutable std::queue<MessagePaisev> incoming;

    std::unique_ptr<boost::asio::io_context> ioContext;
    std::shared_ptr<boost::asio::ip::tcp::socket> socket;
    std::unique_ptr<std::thread> readerThread;

    std::atomic<bool> running;

    void readerLoop();

public:
    SRMapPaisev(const wchar_t* hostName, const wchar_t* portName, const wchar_t* unused1, const wchar_t* unused2);
    ~SRMapPaisev();

    void send(MessagePaisev& msg) const override;
    void sendConfirmation(MessagePaisev& msg) const override;
    void receive(MessagePaisev& msg) const override;

    void waitForMessage() const;
    void waitForProcessed() const;
};

extern "C"
{
    SRMAP_API void* __cdecl CreateSRMapPaisev(const wchar_t* mapName, const wchar_t* mutexName, const wchar_t* messageEventName, const wchar_t* processedEventName);
    SRMAP_API void __cdecl DestroySRMapPaisev(void* map);
    SRMAP_API int __cdecl SRMapSendCommandW(void* map, int to, int messageType, const wchar_t* data, int status, int auxId);
    SRMAP_API int __cdecl SRMapSendConfirmationW(void* map, int to, int messageType, const wchar_t* data, int status, int auxId);
    SRMAP_API int __cdecl SRMapReceiveW(void* map, int* messageType, int* sizeBytes, int* to, int* status, int* auxId, wchar_t* buffer, int bufferChars);
    SRMAP_API void __cdecl SRMapWaitForMessage(void* map);
    SRMAP_API void __cdecl SRMapWaitForProcessed(void* map);
}