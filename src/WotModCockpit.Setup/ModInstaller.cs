using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace WotModCockpit.Setup
{
    /// <summary>Итог установки одного мода.</summary>
    internal sealed class InstallResult
    {
        /// <summary>Мод сборки.</summary>
        public BundleComponent Component { get; set; }

        /// <summary>Мод стоит в игре.</summary>
        public bool Success { get; set; }

        /// <summary>Что произошло — человеческими словами.</summary>
        public string Message { get; set; }

        /// <summary>Куда лёг файл.</summary>
        public string TargetPath { get; set; }

        /// <summary>Прежние версии, выключенные переименованием в <c>.mtmod.off</c>.</summary>
        public List<string> Disabled { get; } = new List<string>();
    }

    /// <summary>
    /// Ставит моды сборки в каталог модов игры.
    /// <para>
    /// Правила — те же, что у кокпита, и все про то, чтобы ничего не потерять:
    /// </para>
    /// <list type="bullet">
    ///   <item>пишется только каталог модов и только файлы <c>*.mtmod</c> / <c>*.mtmod.off</c>
    ///         (плюс временный файл, который тут же переименовывается);</item>
    ///   <item>прежние версии того же мода ВЫКЛЮЧАЮТСЯ (<c>X.mtmod</c> → <c>X.mtmod.off</c>), а не
    ///         удаляются: иначе игра загрузила бы обе, а выключенную можно вернуть одним
    ///         переименованием;</item>
    ///   <item>если выключенная копия с таким именем уже лежит, мод не ставится вовсе:
    ///         перезаписывать её нельзя, а оставить две включённые версии — тоже;</item>
    ///   <item>файл сначала целиком распаковывается рядом и сверяется по SHA-256, и только
    ///         потом встаёт на место; сорвалось — выключенное возвращается обратно;</item>
    ///   <item>чужие моды не трогаются никогда.</item>
    /// </list>
    /// </summary>
    internal sealed class ModInstaller
    {
        /// <summary>Хвост временного файла распаковки.</summary>
        public const string TempSuffix = ".wmcsetup-tmp";

        private readonly BundleFile _bundle;
        private readonly string _modsDir;

        public ModInstaller(BundleFile bundle, string modsDir)
        {
            _bundle = bundle;
            _modsDir = modsDir;
        }

        /// <summary>Ставит выбранные моды по одному. Неудача одного не мешает остальным.</summary>
        /// <param name="components">Что ставить.</param>
        /// <param name="log">Куда писать ход установки.</param>
        /// <returns>Итог по каждому моду.</returns>
        public List<InstallResult> InstallAll(IList<BundleComponent> components, Action<string> log)
        {
            var results = new List<InstallResult>();
            log("Каталог модов: " + _modsDir);

            try
            {
                Directory.CreateDirectory(_modsDir);
            }
            catch (Exception ex) when (InstalledMods.IsIoFailure(ex))
            {
                foreach (BundleComponent c in components)
                {
                    results.Add(Fail(c, null, "не удалось создать каталог модов: " + ex.Message));
                }

                Report(results, log);
                return results;
            }

            foreach (BundleComponent c in components)
            {
                // Снимок перечитывается перед каждым модом: предыдущий мог выключить файлы.
                InstalledMods installed = InstalledMods.Scan(_modsDir);
                InstallResult result = InstallOne(c, installed);
                results.Add(result);
                Report(new[] { result }, log);
            }

            return results;
        }

        private InstallResult InstallOne(BundleComponent c, InstalledMods installed)
        {
            string target;
            try
            {
                target = Path.GetFullPath(Path.Combine(_modsDir, c.FileName));
            }
            catch (Exception ex) when (InstalledMods.IsIoFailure(ex))
            {
                return Fail(c, null, "недопустимое имя файла: " + ex.Message);
            }

            if (!string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(_modsDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return Fail(c, target, "файл ведёт за пределы каталога модов");
            }

            // 1. Что выключить: включённые файлы того же мода, кроме того, что сейчас заменится.
            List<InstalledFile> others = installed.Of(c.Id)
                .Where(f => f.Enabled && !string.Equals(f.FullPath, target, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (InstalledFile f in others)
            {
                if (File.Exists(f.FullPath + InstalledMods.OffSuffix))
                {
                    return Fail(c, target,
                        "прежнюю версию " + f.FullPath + " не выключить: рядом уже лежит " + f.FileName + InstalledMods.OffSuffix +
                        " — перезаписывать его нельзя. Разберитесь с этими файлами вручную; ничего не изменено");
                }
            }

            // 2. Распаковка рядом с проверкой контрольной суммы.
            string temp = target + TempSuffix;
            try
            {
                Extract(c, temp);
            }
            catch (Exception ex) when (ex is BundleException || InstalledMods.IsIoFailure(ex) || ex is InvalidDataException)
            {
                TryDelete(temp);
                return Fail(c, target, ex.Message);
            }

            // 3. Прежние версии — в .off.
            var disabled = new List<string>();
            foreach (InstalledFile f in others)
            {
                try
                {
                    File.Move(f.FullPath, f.FullPath + InstalledMods.OffSuffix);
                    disabled.Add(f.FullPath);
                }
                catch (Exception ex) when (InstalledMods.IsIoFailure(ex))
                {
                    string rollback = Rollback(disabled);
                    TryDelete(temp);
                    return Fail(c, target, "не удалось выключить прежнюю версию " + f.FullPath + ": " + ex.Message + rollback);
                }
            }

            // 4. Новый файл — на место.
            bool replaced = File.Exists(target);
            try
            {
                if (replaced)
                {
                    ReplaceFile(temp, target);
                }
                else
                {
                    File.Move(temp, target);
                }
            }
            catch (Exception ex) when (InstalledMods.IsIoFailure(ex))
            {
                string rollback = Rollback(disabled);
                TryDelete(temp);
                return Fail(c, target, "файл не встал на место: " + ex.Message + rollback);
            }

            var ok = new InstallResult
            {
                Component = c,
                Success = true,
                TargetPath = target,
                Message = replaced ? "заменён файл той же версии" : "установлен",
            };
            ok.Disabled.AddRange(disabled);
            return ok;
        }

        /// <summary>Распаковывает мод во временный файл и сверяет размер и SHA-256 с описанием.</summary>
        /// <param name="c">Мод.</param>
        /// <param name="temp">Временный файл.</param>
        private void Extract(BundleComponent c, string temp)
        {
            ZipArchiveEntry entry = _bundle.GetEntry(c);
            if (entry == null)
            {
                throw new BundleException("в сетапе нет файла " + c.File);
            }

            string hash;
            long total = 0;
            using (Stream source = entry.Open())
            using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (SHA256 sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    target.Write(buffer, 0, n);
                    total += n;
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);
                target.Flush(true);
                hash = Hex.Of(sha.Hash);
            }

            if (total != c.Size || !string.Equals(hash, c.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new BundleException("файл мода внутри сетапа повреждён — контрольная сумма не сходится. Скачайте сетап заново");
            }
        }

        /// <summary>Замена файла той же версии: сначала атомарно, на необычной файловой системе — копией.</summary>
        /// <param name="temp">Новый файл.</param>
        /// <param name="target">Старый файл.</param>
        private static void ReplaceFile(string temp, string target)
        {
            try
            {
                File.Replace(temp, target, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(temp, target, true);
                File.Delete(temp);
            }
        }

        /// <summary>Возвращает выключенные файлы обратно, если установка сорвалась.</summary>
        /// <param name="disabled">Исходные пути выключенных файлов.</param>
        /// <returns>Хвост сообщения: пусто, если откат удался.</returns>
        private static string Rollback(List<string> disabled)
        {
            var failed = new List<string>();
            foreach (string path in disabled)
            {
                try
                {
                    if (!File.Exists(path) && File.Exists(path + InstalledMods.OffSuffix))
                    {
                        File.Move(path + InstalledMods.OffSuffix, path);
                    }
                }
                catch (Exception ex) when (InstalledMods.IsIoFailure(ex))
                {
                    failed.Add(path + InstalledMods.OffSuffix);
                }
            }

            if (disabled.Count == 0)
            {
                return string.Empty;
            }

            return failed.Count == 0
                ? " (выключенные файлы возвращены как были)"
                : " (не удалось вернуть: " + string.Join(", ", failed) + " — включите вручную, убрав .off)";
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (InstalledMods.IsIoFailure(ex))
            {
                Debug.WriteLine("временный файл не удалён: " + ex.Message);
            }
        }

        private static InstallResult Fail(BundleComponent c, string target, string message)
        {
            return new InstallResult { Component = c, Success = false, TargetPath = target, Message = message };
        }

        private static void Report(IEnumerable<InstallResult> results, Action<string> log)
        {
            foreach (InstallResult r in results)
            {
                string title = r.Component.Title + " " + r.Component.Version;
                if (r.Success)
                {
                    log("[ok]   " + title + ": " + r.Message + " — " + r.TargetPath);
                    foreach (string off in r.Disabled)
                    {
                        log("       выключена прежняя версия: " + off + " → " + Path.GetFileName(off) + InstalledMods.OffSuffix);
                    }
                }
                else
                {
                    log("[FAIL] " + title + ": НЕ установлен — " + r.Message);
                }
            }
        }
    }
}
