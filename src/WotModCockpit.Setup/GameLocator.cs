using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace WotModCockpit.Setup
{
    /// <summary>Папка игры глазами установщика: куда ставить моды и под какую версию клиента.</summary>
    internal sealed class GameInfo
    {
        /// <summary>Корень установки — папка с <c>paths.xml</c>.</summary>
        public string Root { get; set; }

        /// <summary>Каталог модов активной версии, например <c>&lt;папка игры&gt;\mods\1.45.0.0</c>.</summary>
        public string ModsDir { get; set; }

        /// <summary>Версия клиента — последний сегмент пути модов.</summary>
        public string ClientVersion { get; set; }

        /// <summary>Почему в эту папку ставить нельзя; <c>null</c> — можно.</summary>
        public string Problem { get; set; }

        /// <summary>В эту папку можно ставить.</summary>
        public bool IsUsable => Problem == null;
    }

    /// <summary>
    /// Поиск игры и разбор её <c>paths.xml</c>. Только чтение: ни реестр, ни файлы игры здесь не
    /// меняются.
    /// <para>
    /// Куда ставить моды, решает не установщик, а сама игра: в <c>paths.xml</c> есть строка
    /// <c>&lt;Path mask="*.mtmod" mode="recursive"&gt;./mods/1.45.0.0&lt;/Path&gt;</c>, и
    /// клиент читает пакеты модов ровно из этой папки. Тот же разбор делает кокпит
    /// (<c>PathsXmlGameInstallLocator</c>).
    /// </para>
    /// <para>
    /// Списки издателей, имён и типовых папок повторены в кокпите (<c>GameDirectoryFinder</c>
    /// в WotModCockpit.Core): установщик собран под .NET Framework 4.7.2 и общий код с Core
    /// не делит. Меняя списки здесь, поменяйте их и там.
    /// </para>
    /// </summary>
    internal static class GameLocator
    {
        private const string PathsXml = "paths.xml";
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>Издатели и имена, по которым игра опознаётся в списке установленных программ.</summary>
        private static readonly string[] Publishers = { "Lesta", "Wargaming" };

        private static readonly string[] DisplayNames = { "Мир танков", "World of Tanks", "Tanki" };

        /// <summary>Типовые папки установки — на случай, если записи в реестре нет.</summary>
        private static readonly string[] TypicalFolders =
        {
            @"Games\World_of_Tanks_RU",
            @"Games\World_of_Tanks",
            @"Games\Tanki",
            @"Games\Мир танков",
            @"Games\WoT",
        };

        /// <summary>
        /// Папки, похожие на установку игры (в каждой есть <c>paths.xml</c>), — сначала из
        /// списка установленных программ, затем типовые.
        /// </summary>
        /// <returns>Пути без повторов, в порядке убывания уверенности.</returns>
        public static List<string> FindCandidates()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in FromRegistry().Concat(FromTypicalFolders()))
            {
                string full = Normalize(dir);
                if (full != null && seen.Add(full) && SafeFileExists(Path.Combine(full, PathsXml)))
                {
                    result.Add(full);
                }
            }

            return result;
        }

        /// <summary>
        /// Поднимается от выбранной папки к корню игры: человек мог указать <c>mods</c> или
        /// <c>mods\1.45.0.0</c> вместо самой папки игры.
        /// </summary>
        /// <param name="chosen">Выбранная папка.</param>
        /// <returns>Папка с <c>paths.xml</c> либо исходная, если такой выше нет.</returns>
        public static string FindRootFrom(string chosen)
        {
            string dir = Normalize(chosen);
            for (int i = 0; dir != null && i < 4; i++)
            {
                if (SafeFileExists(Path.Combine(dir, PathsXml)))
                {
                    return dir;
                }

                dir = SafeParent(dir);
            }

            return chosen;
        }

        /// <summary>Разбирает папку игры.</summary>
        /// <param name="root">Корень установки.</param>
        /// <returns>Описание; при неудаче — с заполненным <see cref="GameInfo.Problem"/>.</returns>
        public static GameInfo Inspect(string root)
        {
            var info = new GameInfo { Root = root ?? string.Empty };

            string full = Normalize(root);
            if (full == null)
            {
                info.Problem = "Папка игры не указана.";
                return info;
            }

            info.Root = full;
            string pathsXml = Path.Combine(full, PathsXml);
            if (!SafeFileExists(pathsXml))
            {
                info.Problem = "В этой папке нет paths.xml — это не папка игры. Укажите папку, где лежат paths.xml и WorldOfTanks.exe.";
                return info;
            }

            XDocument doc;
            try
            {
                using (var fs = new FileStream(pathsXml, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                    using (XmlReader reader = XmlReader.Create(fs, settings))
                    {
                        doc = XDocument.Load(reader);
                    }
                }
            }
            catch (Exception ex) when (ex is XmlException || ex is IOException || ex is UnauthorizedAccessException)
            {
                info.Problem = "paths.xml не читается: " + ex.Message;
                return info;
            }

            foreach (XElement el in doc.Descendants())
            {
                if (!string.Equals(el.Name.LocalName, "Path", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string raw = (el.Value ?? string.Empty).Trim();
                string[] segments = raw.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => s != ".")
                    .ToArray();
                if (segments.Length < 2 || segments.Contains(".."))
                {
                    continue;
                }

                string last = segments[segments.Length - 1];
                string folder = segments[segments.Length - 2];
                bool mtmodLine = string.Equals((string)el.Attribute("mask"), "*.mtmod", StringComparison.OrdinalIgnoreCase);

                if (!LooksLikeVersion(last) ||
                    !(mtmodLine || string.Equals(folder, "mods", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // Абсолютный путь в paths.xml теоретически возможен, но ставить туда, куда показывает
                // чужой файл, установщик не станет: только внутрь папки игры.
                if (Path.IsPathRooted(raw))
                {
                    continue;
                }

                try
                {
                    info.ModsDir = Path.GetFullPath(Path.Combine(full, string.Join("\\", segments)));
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                {
                    continue;
                }

                info.ClientVersion = last;
                return info;
            }

            info.Problem = "В paths.xml нет строки для модов *.mtmod — непонятно, куда их ставить.";
            return info;
        }

        /// <summary>
        /// Последний сегмент похож на версию клиента: только цифры и точки, хотя бы одна точка.
        /// Так отсекается замыкающий <c>./res</c>.
        /// </summary>
        /// <param name="segment">Сегмент пути.</param>
        /// <returns>Похож ли на версию.</returns>
        public static bool LooksLikeVersion(string segment)
        {
            if (string.IsNullOrEmpty(segment))
            {
                return false;
            }

            bool dot = false;
            bool digit = false;
            foreach (char c in segment)
            {
                if (c == '.')
                {
                    dot = true;
                }
                else if (c >= '0' && c <= '9')
                {
                    digit = true;
                }
                else
                {
                    return false;
                }
            }

            return dot && digit;
        }

        /// <summary>Абсолютный путь без завершающего разделителя либо <c>null</c>.</summary>
        /// <param name="dir">Путь.</param>
        /// <returns>Нормализованный путь.</returns>
        public static string Normalize(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return null;
            }

            try
            {
                string full = Path.GetFullPath(dir.Trim().Trim('"'));
                string root = Path.GetPathRoot(full) ?? string.Empty;
                return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException)
            {
                return null;
            }
        }

        private static IEnumerable<string> FromRegistry()
        {
            var found = new List<string>();
            var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (RegistryHive hive in hives)
            {
                foreach (RegistryView view in views)
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey uninstall = baseKey.OpenSubKey(UninstallKey))
                        {
                            if (uninstall == null)
                            {
                                continue;
                            }

                            foreach (string name in uninstall.GetSubKeyNames())
                            {
                                using (RegistryKey app = uninstall.OpenSubKey(name))
                                {
                                    if (app == null)
                                    {
                                        continue;
                                    }

                                    string publisher = app.GetValue("Publisher") as string ?? string.Empty;
                                    string display = app.GetValue("DisplayName") as string ?? string.Empty;
                                    string location = app.GetValue("InstallLocation") as string;

                                    if (!string.IsNullOrWhiteSpace(location) &&
                                        (ContainsAny(publisher, Publishers) || ContainsAny(display, DisplayNames)))
                                    {
                                        found.Add(location);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException)
                    {
                        // Нет доступа к ветке реестра — ищем дальше.
                    }
                }
            }

            return found;
        }

        private static IEnumerable<string> FromTypicalFolders()
        {
            var found = new List<string>();
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return found;
            }

            foreach (DriveInfo drive in drives)
            {
                bool ready;
                try
                {
                    ready = drive.DriveType == DriveType.Fixed && drive.IsReady;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ready = false;
                }

                if (!ready)
                {
                    continue;
                }

                foreach (string folder in TypicalFolders)
                {
                    found.Add(Path.Combine(drive.RootDirectory.FullName, folder));
                }
            }

            return found;
        }

        private static bool ContainsAny(string text, string[] needles)
        {
            foreach (string needle in needles)
            {
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string SafeParent(string dir)
        {
            try
            {
                return Path.GetDirectoryName(dir);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException)
            {
                return null;
            }
        }

        private static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is SecurityException)
            {
                return false;
            }
        }
    }
}
