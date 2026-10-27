using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WotModCockpit.Setup
{
    /// <summary>
    /// Вывод консольного режима: в стандартный вывод и, если задан <c>--log</c>, в файл.
    /// <para>
    /// Сетап — оконное приложение, своей консоли у него нет. Если вывод перенаправлен (тест
    /// читает его через канал), пишем прямо в канал в UTF-8. Если нет — подключаемся к консоли
    /// того, кто запустил (cmd, PowerShell), чтобы текст было видно.
    /// </para>
    /// </summary>
    internal sealed class ConsoleOutput : IDisposable
    {
        private const int StdOutputHandle = -11;
        private const int AttachParentProcess = -1;
        private const int FileTypeUnknown = 0;

        private readonly TextWriter _console;
        private readonly StreamWriter _log;

        private ConsoleOutput(TextWriter console, StreamWriter log)
        {
            _console = console;
            _log = log;
        }

        /// <summary>Открывает вывод.</summary>
        /// <param name="logPath">Файл журнала либо <c>null</c>.</param>
        /// <param name="error">Почему журнал не открылся; <c>null</c> — открылся или не нужен.</param>
        /// <returns>Вывод.</returns>
        public static ConsoleOutput Open(string logPath, out string error)
        {
            error = null;
            TextWriter console = OpenConsole();

            StreamWriter log = null;
            if (!string.IsNullOrEmpty(logPath))
            {
                try
                {
                    log = new StreamWriter(logPath, true, new UTF8Encoding(false)) { AutoFlush = true };
                }
                catch (Exception ex) when (InstalledMods.IsIoFailure(ex))
                {
                    error = "файл журнала не открывается: " + ex.Message;
                }
            }

            return new ConsoleOutput(console, log);
        }

        /// <summary>Пишет строку.</summary>
        /// <param name="line">Текст.</param>
        public void Line(string line)
        {
            try
            {
                if (_console != null)
                {
                    _console.WriteLine(line);
                }
            }
            catch (IOException)
            {
                // Канал закрыли — журнал всё равно пишется.
            }

            if (_log != null)
            {
                _log.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_console != null)
            {
                try
                {
                    _console.Flush();
                }
                catch (IOException)
                {
                    // некуда сбрасывать — не беда
                }
            }

            if (_log != null)
            {
                _log.Dispose();
            }
        }

        private static TextWriter OpenConsole()
        {
            try
            {
                IntPtr handle = GetStdHandle(StdOutputHandle);
                bool redirected = handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != FileTypeUnknown;
                if (redirected)
                {
                    return new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                }

                return AttachConsole(AttachParentProcess) ? Console.Out : null;
            }
            catch (Exception ex) when (ex is IOException || ex is EntryPointNotFoundException || ex is DllNotFoundException)
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern int GetFileType(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
    }
}
