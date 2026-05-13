using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Paisev_Client_LR3
{
    public class Form1 : Form
    {
        private const int MT_SEND_TEXT = 1;
        private const int MT_CONFIRM = 5;
        private const int MT_DISCONNECT = 6;
        private const int MT_REFRESH_THREADS = 7;
        private const int MT_CLIENT_LIST = 8;

        private const int TARGET_ALL_THREADS = 0;

        private const string SERVER_HOST = "127.0.0.1";
        private const int SERVER_PORT = 54000;
        private const int HEADER_SIZE = 20;

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

        private Socket socket;
        private Thread receiveThread;
        private volatile bool connected;
        private readonly object sendLock = new object();
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

        private bool ConnectToServer()
        {
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(SERVER_HOST, SERVER_PORT);

                var firstMessage = ReceiveFromServer();
                if (firstMessage.messageType == MT_CLIENT_LIST)
                    FillClientsByIds(firstMessage.text, firstMessage.auxId);

                connected = true;
                receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                receiveThread.Start();
                SetStatus("Подключено к серверу");
                return true;
            }
            catch (SocketException ex)
            {
                MessageBox.Show("Не удалось подключиться к серверу сообщений.\nСначала запустите ConsoleAppPaisev.\n\n" + ex.Message, "Ошибка подключения", MessageBoxButtons.OK, MessageBoxIcon.Error);
                DisconnectTransport();
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка обмена с сервером сообщений.\n\n" + ex.Message, "Ошибка клиента", MessageBoxButtons.OK, MessageBoxIcon.Error);
                DisconnectTransport();
                return false;
            }
        }

        private (int messageType, int to, int status, int auxId, string text) ReceiveFromServer()
        {
            byte[] headerBytes = ReceiveExact(HEADER_SIZE);
            if (headerBytes == null)
                return (0, 0, 0, 0, "");

            int messageType = BitConverter.ToInt32(headerBytes, 0);
            int sizeBytes = BitConverter.ToInt32(headerBytes, 4);
            int to = BitConverter.ToInt32(headerBytes, 8);
            int status = BitConverter.ToInt32(headerBytes, 12);
            int auxId = BitConverter.ToInt32(headerBytes, 16);

            if (sizeBytes < 0 || (sizeBytes % 2) != 0 || sizeBytes > 1024 * 1024)
                return (0, 0, 0, 0, "");

            string text = "";
            if (sizeBytes > 0)
            {
                byte[] textBytes = ReceiveExact(sizeBytes);
                if (textBytes == null)
                    return (0, 0, 0, 0, "");

                text = Encoding.Unicode.GetString(textBytes);
            }

            return (messageType, to, status, auxId, text);
        }

        private byte[] ReceiveExact(int size)
        {
            byte[] buffer = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                int read = socket.Receive(buffer, offset, size - offset, SocketFlags.None);
                if (read <= 0)
                    return null;

                offset += read;
            }
            return buffer;
        }

        private void ReceiveLoop()
        {
            while (connected && socket != null)
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
            if (socket != null)
                return;

            if (ConnectToServer())
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
            if (socket == null)
                return false;

            try
            {
                byte[] textBytes = Encoding.Unicode.GetBytes(data ?? "");
                byte[] header = new byte[HEADER_SIZE];
                Buffer.BlockCopy(BitConverter.GetBytes(messageType), 0, header, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(textBytes.Length), 0, header, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(to), 0, header, 8, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, header, 12, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, header, 16, 4);

                lock (sendLock)
                {
                    SendAll(header);
                    if (textBytes.Length > 0)
                        SendAll(textBytes);
                }
                return true;
            }
            catch (SocketException)
            {
                SetStatus("Не удалось отправить команду серверу.");
                DisconnectTransport();
                return false;
            }
        }

        private void SendAll(byte[] data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int sent = socket.Send(data, offset, data.Length - offset, SocketFlags.None);
                if (sent <= 0)
                    throw new SocketException();

                offset += sent;
            }
        }

        private void DisconnectFromServer()
        {
            if (socket != null)
                SendCommand(TARGET_ALL_THREADS, MT_DISCONNECT, "");

            DisconnectTransport();
            SetStatus("Отключено");
        }

        private void DisconnectTransport()
        {
            connected = false;

            Socket oldSocket = socket;
            socket = null;

            if (oldSocket != null)
            {
                try
                {
                    oldSocket.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                }

                oldSocket.Close();
            }

            if (receiveThread != null && receiveThread.IsAlive && receiveThread != Thread.CurrentThread)
                receiveThread.Join(1000);

            receiveThread = null;
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
