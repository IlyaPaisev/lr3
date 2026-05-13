using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Paisev_Client_LR3
{
    public class Form1 : Form
    {
        [DllImport("SRMapPaisev.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateSRMapPaisev(string mapName, string mutexName, string messageEventName, string processedEventName);

        [DllImport("SRMapPaisev.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void DestroySRMapPaisev(IntPtr map);

        [DllImport("SRMapPaisev.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int SRMapSendCommandW(IntPtr map, int to, int messageType, string data, int status, int auxId);

        [DllImport("SRMapPaisev.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int SRMapReceiveW(IntPtr map, out int messageType, out int sizeBytes, out int to, out int status, out int auxId, StringBuilder buffer, int bufferChars);

        private const int MT_SEND_TEXT = 1;
        private const int MT_DISCONNECT = 6;
        private const int MT_REFRESH_THREADS = 7;
        private const int MT_CLIENT_LIST = 8;
        private const int MT_CONFIRM = 5;

        private const int TARGET_ALL_THREADS = 0;

        private const string SERVER_HOST = "127.0.0.1";
        private const string SERVER_PORT = "54000";

        private Button buttonConnect;
        private Button buttonDisconnect;
        private Button buttonRefresh;
        private Button buttonSend;
        private ComboBox comboBoxThreads;
        private TextBox textBoxMessage;
        private ListBox listBoxMessages;
        private Label labelTarget;
        private Label labelMessage;
        private Label labelInbox;

        private IntPtr mapPtr = IntPtr.Zero;
        private Thread receiveThread;
        private volatile bool connected;
        private readonly List<int> activeClientIds = new List<int>();

        public Form1()
        {
            Text = "DialogAppPaisev";
            Width = 760;
            Height = 430;
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            SetConnectedState(false);
        }

        private void BuildUi()
        {
            buttonConnect = new Button { Text = "Connect", Left = 20, Top = 20, Width = 110, Height = 32 };
            buttonDisconnect = new Button { Text = "Disconnect", Left = 150, Top = 20, Width = 110, Height = 32 };
            buttonRefresh = new Button { Text = "Refresh", Left = 280, Top = 20, Width = 110, Height = 32 };
            comboBoxThreads = new ComboBox { Left = 140, Top = 85, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
            labelTarget = new Label { Text = "Адресат:", Left = 20, Top = 88, Width = 100 };
            labelMessage = new Label { Text = "Сообщение:", Left = 20, Top = 130, Width = 100 };
            textBoxMessage = new TextBox { Left = 140, Top = 126, Width = 460 };
            buttonSend = new Button { Text = "Send", Left = 620, Top = 124, Width = 90, Height = 30 };
            labelInbox = new Label { Text = "Полученные сообщения:", Left = 20, Top = 175, Width = 180 };
            listBoxMessages = new ListBox { Left = 20, Top = 200, Width = 690, Height = 155 };

            Controls.Add(buttonConnect);
            Controls.Add(buttonDisconnect);
            Controls.Add(buttonRefresh);
            Controls.Add(comboBoxThreads);
            Controls.Add(textBoxMessage);
            Controls.Add(buttonSend);
            Controls.Add(labelTarget);
            Controls.Add(labelMessage);
            Controls.Add(labelInbox);
            Controls.Add(listBoxMessages);

            buttonConnect.Click += buttonConnect_Click;
            buttonDisconnect.Click += buttonDisconnect_Click;
            buttonRefresh.Click += buttonRefresh_Click;
            buttonSend.Click += buttonSend_Click;
        }

        private void SetConnectedState(bool isConnected)
        {
            buttonConnect.Enabled = !isConnected;
            buttonDisconnect.Enabled = isConnected;
            buttonRefresh.Enabled = isConnected;
            buttonSend.Enabled = isConnected;
            comboBoxThreads.Enabled = isConnected;
            textBoxMessage.Enabled = isConnected;

            if (!isConnected)
            {
                activeClientIds.Clear();
                RebuildClientsCombo();
            }
        }

        private bool InitMap()
        {
            try
            {
                mapPtr = CreateSRMapPaisev(SERVER_HOST, SERVER_PORT, "", "");
            }
            catch (DllNotFoundException ex)
            {
                MessageBox.Show("Не найден SRMapPaisev.dll рядом с клиентом.\nПроверьте копирование DLL в папку с Paisev_Client_LR3.exe.\n\n" + ex.Message, "Ошибка загрузки DLL", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            catch (BadImageFormatException ex)
            {
                MessageBox.Show("Несовместимая разрядность клиента и SRMapPaisev.dll (x86/x64).\nСоберите клиент и DLL в одной платформе (обычно x64).\n\n" + ex.Message, "Ошибка разрядности", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                MessageBox.Show("В SRMapPaisev.dll не найдена функция CreateSRMapPaisev.\nПроверьте, что подключена актуальная версия DLL.\n\n" + ex.Message, "Ошибка точки входа", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            catch (Win32Exception ex)
            {
                MessageBox.Show("Не удалось загрузить нативные зависимости SRMapPaisev.dll (например, VC++ Runtime).\n\n" + ex.Message, "Ошибка Win32", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            catch (SEHException ex)
            {
                MessageBox.Show("Низкоуровневая ошибка при вызове SRMapPaisev.dll.\nПроверьте зависимости Visual C++ Runtime и совместимость DLL.\n\n" + ex.Message, "SEH ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (mapPtr == IntPtr.Zero)
                return false;

            var firstMessage = ReceiveFromServer();
            if (firstMessage.messageType == MT_CLIENT_LIST)
                FillClientsByIds(firstMessage.text, firstMessage.auxId);

            connected = true;
            receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();
            SetStatus("Подключено к серверу");
            return true;
        }

        private (int messageType, int to, int status, int auxId, string text) ReceiveFromServer()
        {
            int messageType;
            int sizeBytes;
            int to;
            int status;
            int auxId;
            StringBuilder buffer = new StringBuilder(4096);

            int charsCount = SRMapReceiveW(mapPtr, out messageType, out sizeBytes, out to, out status, out auxId, buffer, buffer.Capacity);
            string text = charsCount > 0 ? buffer.ToString() : "";
            return (messageType, to, status, auxId, text);
        }

        private void ReceiveLoop()
        {
            while (connected && mapPtr != IntPtr.Zero)
            {
                try
                {
                    var message = ReceiveFromServer();
                    if (!connected || message.messageType == 0)
                        break;

                    BeginInvoke(new Action(() => ProcessServerMessage(message.messageType, message.status, message.auxId, message.text)));
                }
                catch
                {
                    break;
                }
            }

            if (connected && !IsDisposed)
                BeginInvoke(new Action(() =>
                {
                    SetStatus("Соединение с сервером потеряно");
                    DisconnectTransport();
                }));
        }

        private void ProcessServerMessage(int messageType, int status, int auxId, string text)
        {
            if (messageType == MT_CLIENT_LIST)
            {
                FillClientsByIds(text, auxId);
                SetStatus("Активных клиентов: " + auxId);
                return;
            }

            if (messageType == MT_SEND_TEXT)
            {
                listBoxMessages.Items.Add(text);
                listBoxMessages.TopIndex = listBoxMessages.Items.Count - 1;
                return;
            }

            if (messageType == MT_CONFIRM)
            {
                SetStatus(text);
                return;
            }
        }

        private void FillClientsByIds(string idsText, int fallbackCount)
        {
            activeClientIds.Clear();

            if (!string.IsNullOrWhiteSpace(idsText))
            {
                string[] parts = idsText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    if (int.TryParse(part.Trim(), out int id) && id > 0 && !activeClientIds.Contains(id))
                        activeClientIds.Add(id);
                }
            }

            if (activeClientIds.Count == 0)
            {
                for (int i = 1; i <= fallbackCount; i++)
                    activeClientIds.Add(i);
            }

            activeClientIds.Sort();
            RebuildClientsCombo();
        }

        private void RebuildClientsCombo()
        {
            comboBoxThreads.Items.Clear();
            comboBoxThreads.Items.Add("Все клиенты");
            foreach (int id in activeClientIds)
                comboBoxThreads.Items.Add(id.ToString());
            comboBoxThreads.SelectedIndex = comboBoxThreads.Items.Count > 0 ? 0 : -1;
        }

        private void SetStatus(string text)
        {
            Text = string.IsNullOrWhiteSpace(text) ? "DialogAppPaisev" : "DialogAppPaisev - " + text;
        }

        private void buttonConnect_Click(object sender, EventArgs e)
        {
            if (mapPtr != IntPtr.Zero)
                return;

            if (!InitMap())
            {
                MessageBox.Show("Не удалось подключиться к серверу. Сначала запустите ConsoleAppPaisev на любой доступной рабочей станции.");
                DisconnectTransport();
                return;
            }

            SetConnectedState(true);
        }

        private void buttonDisconnect_Click(object sender, EventArgs e)
        {
            DisconnectFromServer();
        }

        private void buttonRefresh_Click(object sender, EventArgs e)
        {
            SendCommand(TARGET_ALL_THREADS, MT_REFRESH_THREADS, "");
        }

        private void buttonSend_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(textBoxMessage.Text))
                return;

            int to = comboBoxThreads.SelectedIndex == 0
                ? TARGET_ALL_THREADS
                : int.Parse(comboBoxThreads.SelectedItem.ToString());

            if (SendCommand(to, MT_SEND_TEXT, textBoxMessage.Text))
                textBoxMessage.Clear();
        }

        private bool SendCommand(int to, int messageType, string data)
        {
            if (mapPtr == IntPtr.Zero)
                return false;

            int ok = SRMapSendCommandW(mapPtr, to, messageType, data, 0, 0);
            if (ok == 0)
            {
                SetStatus("Не удалось отправить команду серверу.");
                return false;
            }

            return true;
        }

        private void DisconnectFromServer()
        {
            if (mapPtr != IntPtr.Zero)
                SRMapSendCommandW(mapPtr, 0, MT_DISCONNECT, "", 0, 0);

            DisconnectTransport();
            SetStatus("Отключено");
        }

        private void DisconnectTransport()
        {
            connected = false;

            if (receiveThread != null && receiveThread.IsAlive && receiveThread != Thread.CurrentThread)
                receiveThread.Join(1000);

            receiveThread = null;

            if (mapPtr != IntPtr.Zero)
            {
                DestroySRMapPaisev(mapPtr);
                mapPtr = IntPtr.Zero;
            }
            SetConnectedState(false);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                DisconnectFromServer();
            }
            catch
            {
            }

            base.OnFormClosing(e);
        }
    }
}