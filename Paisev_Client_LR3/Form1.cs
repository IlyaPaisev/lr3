using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
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

        [DllImport("SRMapPaisev.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SRMapWaitForProcessed(IntPtr map);

        private const int MT_SEND_TEXT = 1;
        private const int MT_CREATE_THREAD = 2;
        private const int MT_STOP_THREAD = 3;
        private const int MT_DISCONNECT = 6;

        private const int TARGET_ALL_THREADS = 0;
        private const int TARGET_MAIN_THREAD = -1;

        private const string SERVER_HOST = "127.0.0.1";
        private const string SERVER_PORT = "54000";

        private Button buttonStart;
        private Button buttonStop;
        private Button buttonSend;
        private NumericUpDown numericUpDownN;
        private ComboBox comboBoxThreads;
        private TextBox textBoxMessage;
        private Label labelTarget;
        private Label labelMessage;

        private IntPtr mapPtr = IntPtr.Zero;
        private readonly List<int> activeThreadIds = new List<int>();

        public Form1()
        {
            Text = "DialogAppPaisev";
            Width = 700;
            Height = 260;
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            if (!InitMap())
            {
                MessageBox.Show("Не удалось подключиться к серверу. Сначала запустите ConsoleAppPaisev на любой доступной рабочей станции.");
                buttonStart.Enabled = false;
                buttonStop.Enabled = false;
                buttonSend.Enabled = false;
            }
        }

        private void BuildUi()
        {
            buttonStart = new Button { Text = "Start", Left = 20, Top = 20, Width = 110, Height = 32 };
            buttonStop = new Button { Text = "Stop", Left = 150, Top = 20, Width = 110, Height = 32 };
            numericUpDownN = new NumericUpDown { Left = 280, Top = 24, Width = 90, Minimum = 1, Maximum = 100, Value = 1 };
            comboBoxThreads = new ComboBox { Left = 140, Top = 85, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
            labelTarget = new Label { Text = "Адресат:", Left = 20, Top = 88, Width = 100 };
            labelMessage = new Label { Text = "Сообщение:", Left = 20, Top = 130, Width = 100 };
            textBoxMessage = new TextBox { Left = 140, Top = 126, Width = 400 };
            buttonSend = new Button { Text = "Send", Left = 560, Top = 124, Width = 90, Height = 30 };

            Controls.Add(buttonStart);
            Controls.Add(buttonStop);
            Controls.Add(numericUpDownN);
            Controls.Add(comboBoxThreads);
            Controls.Add(textBoxMessage);
            Controls.Add(buttonSend);
            Controls.Add(labelTarget);
            Controls.Add(labelMessage);

            buttonStart.Click += buttonStart_Click;
            buttonStop.Click += buttonStop_Click;
            buttonSend.Click += buttonSend_Click;
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

            ResetUiState();
            var response = WaitForConfirmation();
            SetStatus("Подключено к серверу");
            FillThreadsByIds(response.text, response.auxId);
            return true;
        }

        private void FillThreadsByIds(string idsText, int fallbackCount)
        {
            activeThreadIds.Clear();

            if (!string.IsNullOrWhiteSpace(idsText))
            {
                string[] parts = idsText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    if (int.TryParse(part.Trim(), out int id) && id > 0 && !activeThreadIds.Contains(id))
                        activeThreadIds.Add(id);
                }
            }

            if (activeThreadIds.Count == 0)
            {
                for (int i = 1; i <= fallbackCount; i++)
                    activeThreadIds.Add(i);
            }

            activeThreadIds.Sort();
            RebuildThreadsCombo();
        }

        private void ResetUiState()
        {
            activeThreadIds.Clear();
            RebuildThreadsCombo();
        }

        private (bool ok, int messageType, int to, int status, int auxId, string text) WaitForConfirmation()
        {
            SRMapWaitForProcessed(mapPtr);

            int messageType;
            int sizeBytes;
            int to;
            int status;
            int auxId;
            StringBuilder buffer = new StringBuilder(4096);

            int charsCount = SRMapReceiveW(mapPtr, out messageType, out sizeBytes, out to, out status, out auxId, buffer, buffer.Capacity);
            string text = charsCount > 0 ? buffer.ToString() : "";
            return (true, messageType, to, status, auxId, text);
        }

        private void RebuildThreadsCombo()
        {
            comboBoxThreads.Items.Clear();
            comboBoxThreads.Items.Add("Все потоки");
            comboBoxThreads.Items.Add("Главный поток");
            foreach (int tid in activeThreadIds)
                comboBoxThreads.Items.Add(tid.ToString());
            comboBoxThreads.SelectedIndex = 0;
        }

        private void AddThreadToUi(int id)
        {
            if (id <= 0 || activeThreadIds.Contains(id))
                return;

            activeThreadIds.Add(id);
            activeThreadIds.Sort();
            RebuildThreadsCombo();
        }

        private void RemoveThreadFromUi(int id)
        {
            activeThreadIds.Remove(id);
            RebuildThreadsCombo();
        }

        private void SetStatus(string text)
        {
            Text = string.IsNullOrWhiteSpace(text) ? "DialogAppPaisev" : "DialogAppPaisev - " + text;
        }

        private void buttonStart_Click(object sender, EventArgs e)
        {
            int n = (int)numericUpDownN.Value;
            for (int i = 0; i < n; i++)
            {
                int ok = SRMapSendCommandW(mapPtr, 0, MT_CREATE_THREAD, "", 0, 0);
                if (ok == 0)
                {
                    SetStatus("Не удалось отправить команду создания потока.");
                    return;
                }

                var response = WaitForConfirmation();
                SetStatus(response.text);
                if (response.status == 1)
                    AddThreadToUi(response.auxId);
            }
        }

        private void buttonStop_Click(object sender, EventArgs e)
        {
            if (activeThreadIds.Count == 0)
            {
                SetStatus("Нет активных потоков для остановки.");
                return;
            }

            int lastId = activeThreadIds[activeThreadIds.Count - 1];
            int sendOk = SRMapSendCommandW(mapPtr, lastId, MT_STOP_THREAD, "", 0, 0);
            if (sendOk == 0)
            {
                SetStatus("Не удалось отправить команду остановки потока.");
                return;
            }

            var response = WaitForConfirmation();
            SetStatus(response.text);
            if (response.status == 1)
                RemoveThreadFromUi(response.auxId);
        }

        private void buttonSend_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(textBoxMessage.Text))
                return;

            int to = comboBoxThreads.SelectedIndex == 0
                ? TARGET_ALL_THREADS
                : comboBoxThreads.SelectedIndex == 1
                    ? TARGET_MAIN_THREAD
                    : int.Parse(comboBoxThreads.SelectedItem.ToString());

            int ok = SRMapSendCommandW(mapPtr, to, MT_SEND_TEXT, textBoxMessage.Text, 0, 0);
            if (ok == 0)
            {
                SetStatus("Не удалось отправить сообщение.");
                return;
            }

            var response = WaitForConfirmation();
            SetStatus(response.text);
            if (response.status == 1)
                textBoxMessage.Clear();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                if (mapPtr != IntPtr.Zero)
                {
                    SRMapSendCommandW(mapPtr, 0, MT_DISCONNECT, "", 0, 0);
                    WaitForConfirmation();
                    DestroySRMapPaisev(mapPtr);
                    mapPtr = IntPtr.Zero;
                }
            }
            catch
            {
            }

            base.OnFormClosing(e);
        }
    }
}
