using System;
using System.Windows.Forms;

namespace Paisev_Client_LR3
{
    internal static class Program
    {
        /// <summary>
        /// Главная точка входа для приложения.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (sender, args) =>
                MessageBox.Show(args.Exception.ToString(), "Необработанная ошибка UI", MessageBoxButtons.OK, MessageBoxIcon.Error);

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                MessageBox.Show((args.ExceptionObject as Exception)?.ToString() ?? "Неизвестная ошибка", "Критическая ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);

            Application.Run(new Form1());
        }
    }
}
