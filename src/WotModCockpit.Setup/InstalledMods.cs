using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace WotModCockpit.Setup
{
    /// <summary>Файл мода в папке игры: включённый <c>*.mtmod</c> или выключенный <c>*.mtmod.off</c>.</summary>
    internal sealed class InstalledFile
    {
        /// <summary>Полный путь.</summary>
        public string FullPath { get; set; }

        /// <summary>Включён (<c>.mtmod</c>), а не выключен (<c>.mtmod.off</c>).</summary>
        public bool Enabled { get; set; }

        /// <summary><c>&lt;id&gt;</c> из meta.xml внутри архива либо <c>null</c>.</summary>
        public string MetaId { get; set; }

        /// <summary><c>&lt;version&gt;</c> из meta.xml внутри архива либо <c>null</c>.</summary>
        public string MetaVersion { get; set; }

        /// <summary>Размер файла.</summary>
        public long Size { get; set; }

        /// <summary>Имя файла.</summary>
        public string FileName => Path.GetFileName(FullPath);

        /// <summary>Имя без <c>.off</c> — каким файл был во включённом виде.</summary>
        public string BaseName => Enabled ? FileName : FileName.Substring(0, FileName.Length - InstalledMods.OffSuffix.Length);

        /// <summary>Версия: из meta.xml, а без него — из имени по правилу build.py.</summary>
        public string Version => !string.IsNullOrEmpty(MetaVersion) ? MetaVersion : ModVersion.FromFileName(BaseName);
    }

    /// <summary>
    /// Снимок каталога модов: что из модов сборки уже стоит. Только чтение.
    /// <para>
    /// Обход рекурсивный — так же папку читает игра (<c>mode="recursive"</c> в paths.xml), и мод,
    /// который модпак положил в подпапку, тоже грузится. Архивы не распаковываются: читается
    /// только каталог ZIP и маленький meta.xml.
    /// </para>
    /// </summary>
    internal sealed class InstalledMods
    {
        /// <summary>Расширение включённого мода.</summary>
        public const string ModExtension = ".mtmod";

        /// <summary>Суффикс выключенного мода — так выключает и кокпит.</summary>
        public const string OffSuffix = ".off";

        private const int MaxDepth = 16;
        private const long MaxMetaBytes = 256 * 1024;

        private InstalledMods(string modsDir, List<InstalledFile> files)
        {
            ModsDir = modsDir;
            Files = files;
        }

        /// <summary>Каталог, который обходили.</summary>
        public string ModsDir { get; }

        /// <summary>Найденные файлы модов.</summary>
        public List<InstalledFile> Files { get; }

        /// <summary>Обходит каталог. Ничего не бросает: недоступное просто пропускается.</summary>
        /// <param name="modsDir">Каталог модов; может не существовать.</param>
        /// <returns>Снимок.</returns>
        public static InstalledMods Scan(string modsDir)
        {
            var files = new List<InstalledFile>();
            if (!string.IsNullOrEmpty(modsDir) && SafeDirExists(modsDir))
            {
                Walk(modsDir, files, 0);
            }

            return new InstalledMods(modsDir, files);
        }

        /// <summary>
        /// Файлы того же мода. Опознание — по <c>&lt;id&gt;</c> внутри архива; у архива без
        /// meta.xml — по имени файла <c>&lt;id&gt;_&lt;версия&gt;.mtmod</c>. Если id внутри есть
        /// и он другой, имя файла не в счёт: <c>ru.test.clock_lite_1.0.mtmod</c> не принадлежит
        /// моду <c>ru.test.clock</c>.
        /// </summary>
        /// <param name="modId">Идентификатор мода.</param>
        /// <returns>Включённые и выключенные файлы этого мода.</returns>
        public List<InstalledFile> Of(string modId)
        {
            return Files.Where(f => IsSameMod(f, modId)).ToList();
        }

        /// <summary>Что из этого мода уже стоит в игре — для колонки «В игре».</summary>
        /// <param name="component">Мод сборки.</param>
        /// <param name="nothingToDo">
        /// Ставить незачем: в игре уже ровно эта сборка или версия новее. Такой мод по умолчанию
        /// без галочки — человек может поставить её сам.
        /// </param>
        /// <returns>Короткий текст.</returns>
        public string Describe(BundleComponent component, out bool nothingToDo)
        {
            nothingToDo = false;
            List<InstalledFile> same = Of(component.Id);
            List<InstalledFile> enabled = same.Where(f => f.Enabled).ToList();

            if (enabled.Count == 0)
            {
                return same.Count == 0 ? "не установлен" : "выключен (.off)";
            }

            InstalledFile sameName = enabled.FirstOrDefault(
                f => string.Equals(f.FileName, component.FileName, StringComparison.OrdinalIgnoreCase));
            if (sameName != null)
            {
                bool identical = IsSameBytes(sameName, component);
                nothingToDo = identical && enabled.Count == 1;
                string extra = enabled.Count > 1 ? " (+ ещё " + (enabled.Count - 1) + ")" : string.Empty;
                return (identical ? "уже стоит эта сборка" : "стоит другая сборка " + component.Version) + extra;
            }

            string best = enabled
                .Select(f => f.Version)
                .Where(v => !string.IsNullOrEmpty(v))
                .OrderByDescending(v => v, ModVersion.Comparer)
                .FirstOrDefault();

            if (best == null)
            {
                return "стоит другая сборка";
            }

            int cmp = ModVersion.Compare(best, component.Version);
            if (cmp > 0)
            {
                nothingToDo = true;
                return "в игре новее: " + best;
            }

            return cmp == 0 ? "стоит " + best + " (другой файл)" : "стоит " + best;
        }

        /// <summary>Тот же мод: id внутри архива главнее имени файла.</summary>
        /// <param name="file">Файл в игре.</param>
        /// <param name="modId">Идентификатор мода.</param>
        /// <returns>Признак.</returns>
        public static bool IsSameMod(InstalledFile file, string modId)
        {
            if (!string.IsNullOrEmpty(file.MetaId))
            {
                return file.MetaId.Equals(modId, StringComparison.OrdinalIgnoreCase);
            }

            string name = file.BaseName;
            return name.StartsWith(modId + "_", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(modId + ModExtension, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameBytes(InstalledFile file, BundleComponent component)
        {
            if (file.Size != component.Size)
            {
                return false;
            }

            try
            {
                using (var fs = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (SHA256 sha = SHA256.Create())
                {
                    return string.Equals(Hex.Of(sha.ComputeHash(fs)), component.Sha256, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                return false;
            }
        }

        private static void Walk(string dir, List<InstalledFile> files, int depth)
        {
            if (depth > MaxDepth)
            {
                return;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                entries = new string[0];
            }

            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
            foreach (string path in entries)
            {
                bool enabled = path.EndsWith(ModExtension, StringComparison.OrdinalIgnoreCase);
                bool disabled = path.EndsWith(ModExtension + OffSuffix, StringComparison.OrdinalIgnoreCase);
                if (!enabled && !disabled)
                {
                    continue;
                }

                var file = new InstalledFile { FullPath = path, Enabled = enabled };
                ReadDetails(file);
                files.Add(file);
            }

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                subdirs = new string[0];
            }

            Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
            foreach (string sub in subdirs)
            {
                Walk(sub, files, depth + 1);
            }
        }

        /// <summary>Размер и meta.xml из архива. Битый архив — штатный случай: поля остаются пустыми.</summary>
        /// <param name="file">Файл в игре.</param>
        private static void ReadDetails(InstalledFile file)
        {
            try
            {
                file.Size = new FileInfo(file.FullPath).Length;

                using (var fs = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, false))
                {
                    ZipArchiveEntry meta = zip.Entries.FirstOrDefault(
                        e => string.Equals(e.FullName.Replace('\\', '/'), "meta.xml", StringComparison.OrdinalIgnoreCase));
                    if (meta == null || meta.Length > MaxMetaBytes)
                    {
                        return;
                    }

                    var settings = new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        IgnoreComments = true,
                    };

                    using (Stream s = meta.Open())
                    using (XmlReader reader = XmlReader.Create(s, settings))
                    {
                        XDocument doc = XDocument.Load(reader);
                        file.MetaId = Tag(doc, "id");
                        file.MetaVersion = Tag(doc, "version") ?? Tag(doc, "versionLst");
                    }
                }
            }
            catch (Exception ex) when (IsIoFailure(ex) || ex is InvalidDataException || ex is XmlException)
            {
                // Архив не читается — мод всё равно показывается, просто без id.
            }
        }

        /// <summary>
        /// Значение тега на любой глубине, без учёта регистра: схема meta.xml у чужих модов не
        /// железная (тот же разбор, что у кокпита).
        /// </summary>
        /// <param name="doc">Документ.</param>
        /// <param name="name">Имя тега.</param>
        /// <returns>Непустое значение либо <c>null</c>.</returns>
        private static string Tag(XDocument doc, string name)
        {
            foreach (XElement el in doc.Descendants())
            {
                if (!el.HasElements && string.Equals(el.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                {
                    string value = el.Value.Trim();
                    if (value.Length > 0)
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private static bool SafeDirExists(string dir)
        {
            try
            {
                return Directory.Exists(dir);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                return false;
            }
        }

        /// <summary>Обыденные беды файловой системы, от которых обход не должен падать.</summary>
        /// <param name="ex">Исключение.</param>
        /// <returns>Можно ли его проглотить.</returns>
        internal static bool IsIoFailure(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException ||
                   ex is ArgumentException || ex is NotSupportedException;
        }
    }

    /// <summary>Версии модов: разбор из имени файла и сравнение по числам.</summary>
    internal static class ModVersion
    {
        /// <summary>Сравнение версий для сортировки.</summary>
        public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

        /// <summary>
        /// Версия из имени <c>&lt;id&gt;_&lt;версия&gt;.mtmod</c>: хвост после последнего <c>_</c>,
        /// состоящий из цифр и точек.
        /// </summary>
        /// <param name="fileName">Имя файла.</param>
        /// <returns>Версия либо <c>null</c>.</returns>
        public static string FromFileName(string fileName)
        {
            string stem = fileName ?? string.Empty;
            if (stem.EndsWith(InstalledMods.OffSuffix, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem.Substring(0, stem.Length - InstalledMods.OffSuffix.Length);
            }

            if (stem.EndsWith(InstalledMods.ModExtension, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem.Substring(0, stem.Length - InstalledMods.ModExtension.Length);
            }

            int underscore = stem.LastIndexOf('_');
            if (underscore <= 0 || underscore == stem.Length - 1)
            {
                return null;
            }

            string tail = stem.Substring(underscore + 1);
            return tail.All(c => c == '.' || (c >= '0' && c <= '9')) && tail.Any(c => c != '.') ? tail : null;
        }

        /// <summary>Сравнивает версии по частям как числа: <c>1.10.0</c> новее <c>1.9.3</c>.</summary>
        /// <param name="a">Первая.</param>
        /// <param name="b">Вторая.</param>
        /// <returns>Знак сравнения.</returns>
        public static int Compare(string a, string b)
        {
            string[] pa = (a ?? string.Empty).Split('.');
            string[] pb = (b ?? string.Empty).Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                string x = i < pa.Length ? pa[i] : "0";
                string y = i < pb.Length ? pb[i] : "0";
                long nx;
                long ny;
                int cmp = long.TryParse(x, out nx) && long.TryParse(y, out ny)
                    ? nx.CompareTo(ny)
                    : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0)
                {
                    return cmp;
                }
            }

            return 0;
        }
    }

    /// <summary>Шестнадцатеричная запись хеша.</summary>
    internal static class Hex
    {
        /// <summary>Байты в строчные шестнадцатеричные знаки.</summary>
        /// <param name="bytes">Байты.</param>
        /// <returns>Строка.</returns>
        public static string Of(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            const string digits = "0123456789abcdef";
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = digits[bytes[i] >> 4];
                chars[(i * 2) + 1] = digits[bytes[i] & 0xF];
            }

            return new string(chars);
        }
    }
}
