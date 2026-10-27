using System;
using System.ComponentModel;
using System.Diagnostics;

namespace WotModCockpit.Setup
{
    /// <summary>
    /// Запущена ли игра. Моды клиент читает один раз при старте, поэтому при запущенной игре
    /// установка запрещена — тот же список процессов, что у кокпита (<c>ModToggle</c>).
    /// </summary>
    internal static class GameProcess
    {
        private static readonly string[] Names = { "Tanki", "WorldOfTanks", "WorldOfTanks_internal" };

        /// <summary>
        /// <c>null</c> — игра не запущена. Иначе — почему ставить нельзя. «Выяснить не удалось»
        /// тоже запрет: ошибиться тут можно ровно один раз.
        /// </summary>
        /// <returns>Причина запрета либо <c>null</c>.</returns>
        public static string WhyBlocked()
        {
            try
            {
                foreach (string name in Names)
                {
                    Process[] found = Process.GetProcessesByName(name);
                    try
                    {
                        if (found.Length > 0)
                        {
                            return "Игра запущена (" + name + ".exe). Закройте её: моды клиент читает только при запуске.";
                        }
                    }
                    finally
                    {
                        foreach (Process p in found)
                        {
                            p.Dispose();
                        }
                    }
                }

                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException)
            {
                return "Не удалось проверить, запущена ли игра: " + ex.Message;
            }
        }
    }
}
