using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace WotModCockpit.Setup
{
    /// <summary>
    /// Точка входа сетапа. Без ключей — окно с галочками; с ключами — консольные режимы
    /// (<see cref="CommandLine.Usage"/>).
    /// </summary>
    internal static class Program
    {
        /// <summary>Все выбранные моды установлены.</summary>
        public const int ExitOk = 0;

        /// <summary>Хотя бы один мод не установился.</summary>
        public const int ExitPartial = 1;

        /// <summary>Ошибка в ключах.</summary>
        public const int ExitBadArguments = 2;

        /// <summary>Игра запущена — ставить нельзя.</summary>
        public const int ExitGameRunning = 3;

        /// <summary>Папка игры не подходит.</summary>
        public const int ExitGameUnusable = 4;

        /// <summary>Сам сетап повреждён (или это голая заглушка).</summary>
        public const int ExitBrokenBundle = 5;

        /// <summary>Непредвиденный сбой самого установщика.</summary>
        public const int ExitCrash = 6;

        /// <summary>Заголовок окон сообщений.</summary>
        public const string Caption = "Установка модов";

        private static string _renderPath;

        [STAThread]
        private static int Main(string[] args)
        {
            Trace("main " + string.Join(" ", args));
            CommandLine cmd;
            string error;
            bool parsed = CommandLine.TryParse(args, out cmd, out error);

            if (!parsed || cmd.IsConsoleMode)
            {
                return RunConsole(parsed ? cmd : null, error);
            }

            _renderPath = cmd.RenderPath;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            BundleFile bundle;
            try
            {
                bundle = BundleFile.Open(Application.ExecutablePath);
            }
            catch (BundleException ex)
            {
                if (cmd.RenderPath == null)
                {
                    MessageBox.Show(ex.Message, Caption, MessageBoxButtons.OK,
                        ex.IsBareStub ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                }

                return ExitBrokenBundle;
            }

            using (bundle)
            {
                if (cmd.RenderPath != null)
                {
                    return MainForm.Render(bundle, cmd.GameRoot, cmd.RenderPath);
                }

                Application.Run(new MainForm(bundle, cmd.GameRoot));
            }

            return ExitOk;
        }

        private static int RunConsole(CommandLine cmd, string parseError)
        {
            string logError;
            using (ConsoleOutput output = ConsoleOutput.Open(cmd == null ? null : cmd.LogPath, out logError))
            {
                if (cmd == null)
                {
                    output.Line("Ошибка: " + parseError + ".");
                    output.Line(CommandLine.Usage);
                    return ExitBadArguments;
                }

                if (logError != null)
                {
                    output.Line("Ошибка: " + logError + ".");
                    return ExitBadArguments;
                }

                if (cmd.Help)
                {
                    output.Line(CommandLine.Usage);
                    return ExitOk;
                }

                BundleFile bundle;
                try
                {
                    bundle = BundleFile.Open(Application.ExecutablePath);
                }
                catch (BundleException ex)
                {
                    output.Line(ex.Message);
                    return ExitBrokenBundle;
                }

                using (bundle)
                {
                    BundleManifest m = bundle.Manifest;
                    output.Line("Сборка «" + m.Name + "» " + m.Version +
                                (string.IsNullOrEmpty(m.GameVersion) ? string.Empty : ", собрана под клиент " + m.GameVersion) +
                                ": " + m.Components.Count + " " + Plural(m.Components.Count, "мод", "мода", "модов") + ".");

                    if (cmd.List)
                    {
                        foreach (BundleComponent c in m.Components)
                        {
                            output.Line(c.Id + "\t" + c.Version + "\t" + c.Title + "\t" + c.FileName + "\t" + c.Size);
                        }

                        if (!cmd.Silent)
                        {
                            return ExitOk;
                        }
                    }

                    return RunSilent(cmd, bundle, output);
                }
            }
        }

        private static int RunSilent(CommandLine cmd, BundleFile bundle, ConsoleOutput output)
        {
            // Тихая установка без явной папки не делается: молча писать в найденную наугад
            // установку игры — ровно то, чего от установщика никто не ждёт.
            if (string.IsNullOrEmpty(cmd.GameRoot))
            {
                output.Line("Ошибка: в тихом режиме папку игры нужно указать явно: --game:<папка игры>.");
                return ExitBadArguments;
            }

            GameInfo game = GameLocator.Inspect(GameLocator.FindRootFrom(cmd.GameRoot));
            if (!game.IsUsable)
            {
                output.Line("Папка игры не подходит: " + game.Problem);
                return ExitGameUnusable;
            }

            List<BundleComponent> all = bundle.Manifest.Components;
            var selected = new List<BundleComponent>();
            if (cmd.Components == null)
            {
                selected.AddRange(all);
            }
            else
            {
                foreach (string id in cmd.Components)
                {
                    BundleComponent c = all.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (c == null)
                    {
                        output.Line("Ошибка: в сборке нет мода «" + id + "». Есть: " + string.Join(", ", all.Select(x => x.Id)) + ".");
                        return ExitBadArguments;
                    }

                    if (!selected.Contains(c))
                    {
                        selected.Add(c);
                    }
                }
            }

            if (selected.Count == 0)
            {
                output.Line("Ошибка: не выбрано ни одного мода.");
                return ExitBadArguments;
            }

            string blocked = GameProcess.WhyBlocked();
            if (blocked != null)
            {
                output.Line(blocked);
                return ExitGameRunning;
            }

            string mismatch = VersionMismatch(bundle.Manifest, game);
            if (mismatch != null)
            {
                output.Line("Внимание: " + mismatch);
            }

            List<InstallResult> results = new ModInstaller(bundle, game.ModsDir).InstallAll(selected, output.Line);
            int ok = results.Count(r => r.Success);
            if (ok == results.Count)
            {
                output.Line("Готово: установлено " + ok + " " + Plural(ok, "мод", "мода", "модов") +
                            ". Изменения вступят в силу при следующем запуске игры.");
                return ExitOk;
            }

            output.Line("Установлено " + ok + " из " + results.Count + "; остальное — см. строки [FAIL] выше.");
            return ExitPartial;
        }

        /// <summary>
        /// Предупреждение, если сборку делали под другой клиент. Это не запрет: питоновские моды
        /// часто переживают обновление игры, но человек должен знать, почему мод мог не заработать.
        /// </summary>
        /// <param name="manifest">Описание сборки.</param>
        /// <param name="game">Игра.</param>
        /// <returns>Текст предупреждения либо <c>null</c>.</returns>
        public static string VersionMismatch(BundleManifest manifest, GameInfo game)
        {
            if (string.IsNullOrEmpty(manifest.GameVersion) || game == null || string.IsNullOrEmpty(game.ClientVersion) ||
                string.Equals(manifest.GameVersion, game.ClientVersion, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return "сборка собиралась под клиент " + manifest.GameVersion + ", а у вас " + game.ClientVersion +
                   " — если мод не заработает, его нужно пересобрать.";
        }

        /// <summary>
        /// Непредвиденный сбой: вместо системного диалога .NET с кнопкой «Продолжить» —
        /// понятное сообщение и выход. Продолжать установку после сбоя нельзя: неизвестно, в каком
        /// состоянии остались файлы. В режиме <c>--render</c> текст пишется рядом с картинкой.
        /// </summary>
        /// <param name="ex">Исключение.</param>
        private static void Crash(Exception ex)
        {
            string text = ex == null ? "неизвестная ошибка" : ex.ToString();
            Trace("crash " + text);
            if (_renderPath != null)
            {
                try
                {
                    System.IO.File.WriteAllText(_renderPath + ".error.txt", text, new System.Text.UTF8Encoding(false));
                }
                catch (Exception writeError) when (InstalledMods.IsIoFailure(writeError))
                {
                    // некуда записать — код возврата всё равно скажет о сбое
                }
            }
            else
            {
                MessageBox.Show(
                    "Установщик столкнулся с непредвиденной ошибкой и будет закрыт. Уже установленные моды остаются на месте.\n\n" + text,
                    Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            Environment.Exit(ExitCrash);
        }

        /// <summary>Диагностика: если задана переменная WMCSETUP_TRACE, шаги пишутся в этот файл.</summary>
        /// <param name="line">Что записать.</param>
        internal static void Trace(string line)
        {
            string path = Environment.GetEnvironmentVariable("WMCSETUP_TRACE");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
            }
            catch (Exception ex) when (InstalledMods.IsIoFailure(ex))
            {
                // диагностика не должна мешать работе
            }
        }

        /// <summary>Русское множественное число.</summary>
        /// <param name="n">Число.</param>
        /// <param name="one">Форма для 1.</param>
        /// <param name="few">Форма для 2–4.</param>
        /// <param name="many">Форма для 5–20.</param>
        /// <returns>Нужная форма.</returns>
        public static string Plural(int n, string one, string few, string many)
        {
            int mod100 = n % 100;
            if (mod100 >= 11 && mod100 <= 14)
            {
                return many;
            }

            switch (n % 10)
            {
                case 1:
                    return one;
                case 2:
                case 3:
                case 4:
                    return few;
                default:
                    return many;
            }
        }
    }
}
