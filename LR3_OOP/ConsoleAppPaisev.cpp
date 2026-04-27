#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iostream>
#include <vector>
#include <thread>
#include <memory>
#include <mutex>
#include <fstream>
#include <string>
#include <algorithm>
#include <atomic>

#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif

#include <boost/asio.hpp>

#include "MessagePaisev.h"
#include "Session.h"

using boost::asio::ip::tcp;

namespace
{
    const unsigned short SERVER_PORT = 54000;

    std::vector<std::shared_ptr<Session>> g_sessions;
    std::vector<std::thread> g_threads;
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

    void Log(const std::wstring& text)
    {
        std::lock_guard<std::mutex> lock(g_logMutex);
        std::wcout << text << std::endl;
    }

    int AllocateWorkerId()
    {
        int id = 1;
        while (true)
        {
            auto it = std::find_if(g_sessions.begin(), g_sessions.end(), [id](const std::shared_ptr<Session>& s)
                {
                    return s->sessionID == id;
                });

            if (it == g_sessions.end())
                return id;

            ++id;
        }
    }

    void WorkerProc(std::shared_ptr<Session> session)
    {
        Log(L"[worker " + std::to_wstring(session->sessionID) + L"] started");

        while (session->isRunning())
        {
            MessagePaisev msg;
            if (!session->getMessage(msg))
                break;

            if (msg.header.messageType == MT_STOP_THREAD)
                break;

            if (msg.header.messageType == MT_SEND_TEXT)
            {
                std::wstring fileName = std::to_wstring(session->sessionID) + L".txt";
                std::wofstream file(fileName, std::ios::app);

                if (file.is_open())
                    file << msg.data << std::endl;
            }
        }

        session->stop();
        Log(L"[worker " + std::to_wstring(session->sessionID) + L"] finished");
    }

    int ActiveWorkersCount()
    {
        return static_cast<int>(g_sessions.size());
    }

    std::wstring BuildActiveIdsCsv()
    {
        std::wstring result;
        bool first = true;
        for (const auto& session : g_sessions)
        {
            if (!first)
                result += L",";
            result += std::to_wstring(session->sessionID);
            first = false;
        }
        return result;
    }

    int CreateWorker()
    {
        int id = AllocateWorkerId();
        auto session = std::make_shared<Session>(id);

        g_threads.emplace_back([session]()
            {
                WorkerProc(session);
            });

        g_sessions.push_back(session);
        return id;
    }

    bool StopWorker(int id)
    {
        auto it = std::find_if(g_sessions.begin(), g_sessions.end(), [id](const std::shared_ptr<Session>& s)
            {
                return s->sessionID == id;
            });

        if (it == g_sessions.end())
            return false;

        MessagePaisev stopMsg(id, MT_STOP_THREAD, L"");
        (*it)->addMessage(stopMsg);
        (*it)->stop();
        g_sessions.erase(it);
        return true;
    }

    void JoinAndCleanupThreads()
    {
        for (auto& t : g_threads)
        {
            if (t.joinable())
                t.join();
        }
        g_threads.clear();
    }

    void RouteMessageToSessions(const MessagePaisev& incoming)
    {
        if (incoming.header.to == TARGET_ALL_THREADS)
        {
            for (auto& s : g_sessions)
            {
                MessagePaisev copy(
                    s->sessionID,
                    static_cast<MessageTypesPaisev>(incoming.header.messageType),
                    incoming.data,
                    incoming.header.status,
                    incoming.header.auxId);
                s->addMessage(copy);
            }
            return;
        }

        auto it = std::find_if(g_sessions.begin(), g_sessions.end(), [&incoming](const std::shared_ptr<Session>& s)
            {
                return s->sessionID == incoming.header.to;
            });

        if (it != g_sessions.end())
            (*it)->addMessage(incoming);
    }

    void SendConfirmation(tcp::socket& socket, int to, bool ok, const std::wstring& text, int auxId = 0)
    {
        MessagePaisev response(to, MT_CONFIRM, text, ok ? 1 : 0, auxId);
        SendMessage(socket, response);
    }

    void HandleClient(std::shared_ptr<tcp::socket> socket)
    {
        {
            std::lock_guard<std::mutex> lock(g_sessionsMutex);
            SendConfirmation(*socket, TARGET_MAIN_THREAD, true, BuildActiveIdsCsv(), ActiveWorkersCount());
        }

        while (g_running.load())
        {
            MessagePaisev incoming;
            if (!ReceiveMessage(*socket, incoming))
                break;

            std::lock_guard<std::mutex> lock(g_sessionsMutex);

            switch (incoming.header.messageType)
            {
            case MT_CREATE_THREAD:
            {
                int id = CreateWorker();
                SendConfirmation(*socket, incoming.header.to, true, L"Поток создан.", id);
                break;
            }
            case MT_STOP_THREAD:
            {
                if (StopWorker(incoming.header.to))
                    SendConfirmation(*socket, incoming.header.to, true, L"Поток остановлен.", incoming.header.to);
                else
                    SendConfirmation(*socket, incoming.header.to, false, L"Указанный поток не найден.");
                break;
            }
            case MT_SEND_TEXT:
            {
                if (incoming.data.empty())
                {
                    SendConfirmation(*socket, incoming.header.to, false, L"Пустое сообщение не отправлено.");
                    break;
                }

                if (incoming.header.to == TARGET_MAIN_THREAD)
                {
                    Log(L"[main] " + incoming.data);
                    SendConfirmation(*socket, incoming.header.to, true, L"Сообщение выведено главным потоком.");
                }
                else if (incoming.header.to == TARGET_ALL_THREADS)
                {
                    RouteMessageToSessions(incoming);
                    SendConfirmation(*socket, incoming.header.to, true, L"Сообщение отправлено всем рабочим потокам.");
                }
                else
                {
                    auto it = std::find_if(g_sessions.begin(), g_sessions.end(), [&incoming](const std::shared_ptr<Session>& s)
                        {
                            return s->sessionID == incoming.header.to;
                        });

                    if (it == g_sessions.end())
                        SendConfirmation(*socket, incoming.header.to, false, L"Поток-адресат не найден.");
                    else
                    {
                        RouteMessageToSessions(incoming);
                        SendConfirmation(*socket, incoming.header.to, true, L"Сообщение отправлено в указанный поток.");
                    }
                }
                break;
            }
            case MT_DISCONNECT:
            {
                SendConfirmation(*socket, incoming.header.to, true, L"Клиент отключен от сервера.");
                return;
            }
            case MT_SHUTDOWN:
            {
                SendConfirmation(*socket, incoming.header.to, false, L"Остановка сервера удалённо запрещена.");
                break;
            }
            default:
                SendConfirmation(*socket, incoming.header.to, false, L"Неизвестная команда.");
                break;
            }
        }
    }
}

int wmain()
{
    SetConsoleOutputCP(CP_UTF8);
    std::locale::global(std::locale(""));

    Log(L"[main] TCP server started on port 54000");

    try
    {
        boost::asio::io_context io;
        tcp::acceptor acceptor(io, tcp::endpoint(tcp::v4(), SERVER_PORT));

        while (g_running.load())
        {
            auto socket = std::make_shared<tcp::socket>(io);
            acceptor.accept(*socket);
            std::thread(HandleClient, socket).detach();
        }
    }
    catch (const std::exception& ex)
    {
        std::cerr << "Server error: " << ex.what() << std::endl;
        return 1;
    }

    JoinAndCleanupThreads();
    return 0;
}
