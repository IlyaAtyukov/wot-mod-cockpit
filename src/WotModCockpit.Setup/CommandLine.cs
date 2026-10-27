using System;
using System.Collections.Generic;
using System.Linq;

namespace WotModCockpit.Setup
{
    /// <summary>
    /// Ключи запуска. Без ключей сетап открывает окно — это обычный путь для человека. Ключи
    /// нужны для тихой установки и для проверки сетапа тестами кокпита.
    /// </summary>
    internal sealed class CommandLine
    {
        /// <summary>Текст справки — его печатает <c>--help</c>.</summary>
        public const string Usage =
            "Ключи сетапа (без ключей открывается окно):\n" +
            "  --list                    показать моды сборки и выйти\n" +
            "  --silent --game:<папка>   установить без окна; папку игры указывать обязательно\n" +
            "  --components:<id,id|all>  что ставить в тихом режиме (по умолчанию all)\n" +
            "  --log:<файл>              дописать ход установки в файл (UTF-8)\n" +
            "  --game:<папка>            в окне: начать с этой папки игры\n" +
            "Коды возврата: 0 — всё установлено; 1 — что-то не установилось; 2 — ошибка в ключах;\n" +
            "3 — игра запущена; 4 — папка игры не подходит; 5 — сетап повреждён; 6 — сбой установщика.";

        /// <summary>Установить без окна.</summary>
        public bool Silent { get; private set; }

        /// <summary>Напечатать состав сборки.</summary>
        public bool List { get; private set; }

        /// <summary>Напечатать справку.</summary>
        public bool Help { get; private set; }

        /// <summary>Папка игры либо <c>null</c>.</summary>
        public string GameRoot { get; private set; }

        /// <summary>Какие моды ставить; <c>null</c> — все.</summary>
        public List<string> Components { get; private set; }

        /// <summary>Файл журнала либо <c>null</c>.</summary>
        public string LogPath { get; private set; }

        /// <summary>Нарисовать окно в PNG и выйти (служебный ключ для проверки вёрстки).</summary>
        public string RenderPath { get; private set; }

        /// <summary>Запуск в консольном режиме: окно не открывается.</summary>
        public bool IsConsoleMode => Silent || List || Help;

        /// <summary>Разбирает ключи вида <c>--имя</c>, <c>--имя:значение</c>, <c>/имя=значение</c>.</summary>
        /// <param name="args">Аргументы процесса.</param>
        /// <param name="result">Разобранные ключи.</param>
        /// <param name="error">Что не так, если разобрать не вышло.</param>
        /// <returns>Удалось ли.</returns>
        public static bool TryParse(string[] args, out CommandLine result, out string error)
        {
            result = new CommandLine();
            error = null;

            foreach (string raw in args ?? new string[0])
            {
                string arg = raw.Trim();
                if (arg.Length == 0)
                {
                    continue;
                }

                string body;
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    body = arg.Substring(2);
                }
                else if (arg.StartsWith("/", StringComparison.Ordinal) || arg.StartsWith("-", StringComparison.Ordinal))
                {
                    body = arg.Substring(1);
                }
                else
                {
                    error = "непонятный аргумент «" + raw + "»";
                    return false;
                }

                int sep = body.IndexOfAny(new[] { ':', '=' });
                string name = (sep < 0 ? body : body.Substring(0, sep)).ToLowerInvariant();
                string value = sep < 0 ? null : body.Substring(sep + 1).Trim().Trim('"');

                switch (name)
                {
                    case "silent":
                    case "quiet":
                        result.Silent = true;
                        break;
                    case "list":
                        result.List = true;
                        break;
                    case "help":
                    case "?":
                    case "h":
                        result.Help = true;
                        break;
                    case "game":
                        if (string.IsNullOrEmpty(value))
                        {
                            error = "у ключа --game не указана папка: --game:<папка игры>";
                            return false;
                        }

                        result.GameRoot = value;
                        break;
                    case "components":
                        if (string.IsNullOrEmpty(value))
                        {
                            error = "у ключа --components не указан список: --components:<id,id> или --components:all";
                            return false;
                        }

                        List<string> ids = value
                            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => s.Length > 0)
                            .ToList();
                        result.Components = ids.Any(s => string.Equals(s, "all", StringComparison.OrdinalIgnoreCase)) ? null : ids;
                        break;
                    case "log":
                        if (string.IsNullOrEmpty(value))
                        {
                            error = "у ключа --log не указан файл: --log:<файл>";
                            return false;
                        }

                        result.LogPath = value;
                        break;
                    case "render":
                        if (string.IsNullOrEmpty(value))
                        {
                            error = "у ключа --render не указан файл: --render:<файл.png>";
                            return false;
                        }

                        result.RenderPath = value;
                        break;
                    default:
                        error = "неизвестный ключ «" + raw + "»";
                        return false;
                }
            }

            return true;
        }
    }
}
