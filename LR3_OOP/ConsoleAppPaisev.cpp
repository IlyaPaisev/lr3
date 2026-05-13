#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iostream>
#include <vector>
#include <thread>
#include <memory>
#include <mutex>
#include <string>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <locale>
#include <stdexcept>

#include <boost/asio.hpp>

#include "MessagePaisev.h"

using boost::asio::ip::tcp;

namespace
{
    const unsigned short SERVER_PORT = 54000;

    struct ClientSession
    {
        int sessionID;
        std::shared_ptr<tcp::socket> socket;
        std::shared_ptr<std::thread> thread;
        std::mutex sendMutex;
        std::atomic<bool> connected;
        std::chrono::steady_clock::time_point lastActivity;

        ClientSession(int id, std::shared_ptr<tcp::socket> clientSocket)
            : sessionID(id), socket(std::move(clientSocket)), connected(true), lastActivity(std::chrono::steady_clock::now())
        {
        }
    };

    std::vector<std::shared_ptr<ClientSession>> g_sessions;
    std::mutex g_sessionsMutex;
    std::atomic<bool> g_running = true;
    std::mutex g_logMutex;

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

    bool ReceiveMessage(tcp::socket& socket, MessagePaisev& msg)
    {
        MessageHeaderPaisev header{};
        if (!ReadExact(socket, &header, sizeof(header)))
            return false;

        if (header.size < 0 || (header.size % static_cast<int>(sizeof(wchar_t))) != 0 || header.size > 1024 * 1024)
            return false;

        std::wstring text;
        if (header.size > 0)
        {
            text.resize(header.size / static_cast<int>(sizeof(wchar_t)));
            if (!text.empty() && !ReadExact(socket, &text[0], static_cast<std::size_t>(header.size)))
                return false;
        }

        msg.header = header;
        msg.data = text;
        return true;
    }

    bool SendMessage(tcp::socket& socket, MessagePaisev msg)
    {
        msg.refreshSize();
        if (!WriteExact(socket, &msg.header, sizeof(msg.header)))
            return false;

        if (msg.header.size > 0)
            return WriteExact(socket, msg.data.data(), static_cast<std::size_t>(msg.header.size));

        return true;
    }

    class SocketSender final : public ISenderPaisev
    {
    public:
        explicit SocketSender(tcp::socket& sock) : socket(sock) {}
        void send(MessagePaisev& msg) const override { SendMessage(socket, msg); }
        void sendConfirmation(MessagePaisev& msg) const override { SendMessage(socket, msg); }
    private:
        tcp::socket& socket;
    };

    class SocketReceiver final : public IReceiverPaisev
    {
    public:
        explicit SocketReceiver(tcp::socket& sock) : socket(sock) {}
        void receive(MessagePaisev& msg) const override
        {
            if (!ReceiveMessage(socket, msg))
                throw std::runtime_error("client disconnected");
        }
    private:
        tcp::socket& socket;
    };

    void Log(const std::wstring& text)
    {
        std::lock_guard<std::mutex> lock(g_logMutex);
        std::wcout << text << std::endl;
    }

    int AllocateClientIdLocked()
    {
        int id = 1;
        while (true)
        {
            auto it = std::find_if(g_sessions.begin(), g_sessions.end(), [id](const std::shared_ptr<ClientSession>& session)
                {
                    return session->sessionID == id;
                });

            if (it == g_sessions.end())
                return id;

            ++id;
        }
    }

    std::wstring BuildActiveIdsCsvLocked()
    {
        std::wstring result;
        bool first = true;
        for (const auto& session : g_sessions)
        {
            if (!session->connected.load())
                continue;

            if (!first)
                result += L",";

            result += std::to_wstring(session->sessionID);
            first = false;
        }
        return result;
    }

    int ActiveClientsCountLocked()
    {
        return static_cast<int>(std::count_if(g_sessions.begin(), g_sessions.end(), [](const std::shared_ptr<ClientSession>& session)
            {
                return session->connected.load();
            }));
    }

    void SendToClient(const std::shared_ptr<ClientSession>& session, MessagePaisev msg)
    {
        if (!session || !session->connected.load() || !session->socket)
            return;

        std::lock_guard<std::mutex> lock(session->sendMutex);
        SocketSender sender(*session->socket);
        msg.send(sender);
    }

    void SendConfirmation(const std::shared_ptr<ClientSession>& session, bool ok, const std::wstring& text, int auxId = 0)
    {
        MessagePaisev response(session ? session->sessionID : TARGET_MAIN_THREAD, MT_CONFIRM, text, ok ? 1 : 0, auxId);
        SendToClient(session, response);
    }

    std::vector<std::shared_ptr<ClientSession>> SnapshotClientsLocked()
    {
        std::vector<std::shared_ptr<ClientSession>> result;
        for (const auto& session : g_sessions)
        {
            if (session->connected.load())
                result.push_back(session);
        }
        return result;
    }

    void BroadcastClientList()
    {
        std::vector<std::shared_ptr<ClientSession>> recipients;
        std::wstring ids;
        int count = 0;

        {
            std::lock_guard<std::mutex> lock(g_sessionsMutex);
            recipients = SnapshotClientsLocked();
            ids = BuildActiveIdsCsvLocked();
            count = ActiveClientsCountLocked();
        }

        for (const auto& session : recipients)
        {
            MessagePaisev listMessage(session->sessionID, MT_CLIENT_LIST, ids, 1, count);
            SendToClient(session, listMessage);
        }
    }

    void RemoveClient(int id)
    {
        std::shared_ptr<ClientSession> removed;
        {
            std::lock_guard<std::mutex> lock(g_sessionsMutex);
            auto it = std::find_if(g_sessions.begin(), g_sessions.end(), [id](const std::shared_ptr<ClientSession>& session)
                {
                    return session->sessionID == id;
                });

            if (it != g_sessions.end())
            {
                removed = *it;
                removed->connected.store(false);
                g_sessions.erase(it);
            }
        }

        if (removed && removed->socket)
        {
            boost::system::error_code ignored;
            removed->socket->shutdown(tcp::socket::shutdown_both, ignored);
            removed->socket->close(ignored);
        }

        BroadcastClientList();
        Log(L"[client " + std::to_wstring(id) + L"] disconnected");
    }

    void RouteTextMessage(const std::shared_ptr<ClientSession>& sender, const MessagePaisev& incoming)
    {
        std::vector<std::shared_ptr<ClientSession>> recipients;
        {
            std::lock_guard<std::mutex> lock(g_sessionsMutex);
            if (incoming.header.to == TARGET_ALL_THREADS)
            {
                recipients = SnapshotClientsLocked();
            }
            else
            {
                auto it = std::find_if(g_sessions.begin(), g_sessions.end(), [&incoming](const std::shared_ptr<ClientSession>& session)
                    {
                        return session->sessionID == incoming.header.to && session->connected.load();
                    });

                if (it != g_sessions.end())
                    recipients.push_back(*it);
            }
        }

        if (recipients.empty())
        {
            SendConfirmation(sender, false, L"Адресат не найден.");
            return;
        }

        std::wstring text = L"Клиент " + std::to_wstring(sender->sessionID) + L": " + incoming.data;
        for (const auto& recipient : recipients)
        {
            MessagePaisev outgoing(recipient->sessionID, MT_SEND_TEXT, text, 1, sender->sessionID);
            SendToClient(recipient, outgoing);
        }

        SendConfirmation(sender, true, L"Сообщение доставлено.");
    }

    void HandleClient(std::shared_ptr<ClientSession> session)
    {
        Log(L"[client " + std::to_wstring(session->sessionID) + L"] connected");
        BroadcastClientList();

        while (g_running.load() && session->connected.load())
        {
            MessagePaisev incoming;
            try
            {
                SocketReceiver receiver(*session->socket);
                incoming.receive(receiver);
            }
            catch (...)
            {
                break;
            }

            session->lastActivity = std::chrono::steady_clock::now();

            switch (incoming.header.messageType)
            {
            case MT_SEND_TEXT:
            {
                if (incoming.data.empty())
                    SendConfirmation(session, false, L"Пустое сообщение не отправлено.");
                else
                    RouteTextMessage(session, incoming);
                break;
            }
            case MT_REFRESH_THREADS:
            {
                BroadcastClientList();
                break;
            }
            case MT_REFRESH_THREADS:
            {
                SendConfirmation(session, true, L"Клиент отключен от сервера.");
                session->connected.store(false);
                break;
            }
            case MT_SHUTDOWN:
            {
                SendConfirmation(session, false, L"Остановка сервера удалённо запрещена.");
                break;
            }
            default:
                SendConfirmation(session, false, L"Неизвестная команда.");
                break;
            }
        }

        RemoveClient(session->sessionID);
    }
}

int wmain()
{
    SetConsoleOutputCP(CP_UTF8);
    std::locale::global(std::locale(""));

    Log(L"[main] message server started on port 54000");

    try
    {
        boost::asio::io_context io;
        tcp::acceptor acceptor(io, tcp::endpoint(tcp::v4(), SERVER_PORT));

        while (g_running.load())
        {
            auto socket = std::make_shared<tcp::socket>(io);
            acceptor.accept(*socket);

            std::shared_ptr<ClientSession> session;
            {
                std::lock_guard<std::mutex> lock(g_sessionsMutex);
                session = std::make_shared<ClientSession>(AllocateClientIdLocked(), socket);
                g_sessions.push_back(session);
            }

            session->thread = std::make_shared<std::thread>(HandleClient, session);
            session->thread->detach();
        }
    }
    catch (const std::exception& ex)
    {
        std::cerr << "Server error: " << ex.what() << std::endl;
        return 1;
    }

    return 0;
}