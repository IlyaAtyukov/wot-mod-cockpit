using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.Core.Bundle;

/// <summary>Что упаковать в сетап.</summary>
public sealed record SetupBundleRequest
{
    /// <summary>Заглушка установщика — <c>setup\WotModCockpit.Setup.exe</c> рядом с кокпитом.</summary>
    public required string StubPath { get; init; }

    /// <summary>Куда положить готовый сетап (<c>.exe</c>).</summary>
    public required string OutputPath { get; init; }

    /// <summary>Имя сборки — заголовок окна сетапа.</summary>
    public required string Name { get; init; }

    /// <summary>Версия сборки.</summary>
    public required string Version { get; init; }

    /// <summary>Версия клиента, под которую собирались моды; пусто — неизвестна.</summary>
    public string GameVersion { get; init; } = string.Empty;

    /// <summary>Собранные моды (<c>dist\&lt;id&gt;_&lt;версия&gt;.mtmod</c>) в порядке показа.</summary>
    public required IReadOnlyList<string> ModFiles { get; init; }

    /// <summary>Разрешено заменить уже существующий файл сетапа.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Момент сборки для описания; <c>null</c> — сейчас (подменяется в тестах).</summary>
    public DateTimeOffset? CreatedUtc { get; init; }
}

/// <summary>Готовый сетап.</summary>
/// <param name="OutputPath">Где лежит.</param>
/// <param name="SizeBytes">Размер файла.</param>
/// <param name="Manifest">Что внутри.</param>
public sealed record SetupBundleResult(string OutputPath, long SizeBytes, SetupBundleManifest Manifest);

/// <summary>
/// Упаковывает собранные моды в один <c>Setup.exe</c>: заглушка + ZIP с модами и описанием +
/// трейлер (<see cref="SetupBundleFormat"/>).
/// <para>
/// В каталог игры отсюда не пишется НИЧЕГО: сетап — это файл, который человек запустит сам.
/// Запись идёт во временный файл рядом с целью, готовый файл перечитывается и сверяется
/// по контрольным суммам (<see cref="SetupBundleReader"/>) и только потом встаёт на место.
/// Сорванная сборка не оставляет ни полуфайла, ни подменённого старого сетапа.
/// </para>
/// <para>
/// Каждый мод проверяется так же, как перед установкой кокпитом: открывается как ZIP, не пуст,
/// внутри есть <c>meta.xml</c> с <c>&lt;id&gt;</c>. Имя, версия и описание в сетапе берутся из
/// этого meta.xml — из самого архива, а не из проекта, чтобы показанное совпадало с ставящимся.
/// </para>
/// </summary>
public static class SetupBundleBuilder
{
    /// <summary>Потолок длины имени сборки.</summary>
    public const int MaxNameLength = 80;

    /// <summary>Потолок длины версии сборки.</summary>
    public const int MaxVersionLength = 40;

    /// <summary>Что не так с именем сборки; <c>null</c> — всё в порядке.</summary>
    /// <param name="name">Имя.</param>
    /// <returns>Причина либо <c>null</c>.</returns>
    public static string? ValidateName(string? name)
    {
        string value = (name ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "Имя сборки не может быть пустым — это заголовок окна сетапа.";
        }

        if (value.Length > MaxNameLength)
        {
            return $"Имя сборки длиннее {MaxNameLength} символов.";
        }

        return value.Any(char.IsControl) ? "В имени сборки есть управляющие символы." : null;
    }

    /// <summary>Что не так с версией сборки; <c>null</c> — всё в порядке.</summary>
    /// <param name="version">Версия.</param>
    /// <returns>Причина либо <c>null</c>.</returns>
    public static string? ValidateVersion(string? version)
    {
        string value = (version ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "Версия сборки не может быть пустой.";
        }

        if (value.Length > MaxVersionLength)
        {
            return $"Версия длиннее {MaxVersionLength} символов.";
        }

        return value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+')
            ? null
            : "В версии допустимы латинские буквы, цифры и знаки . - _ +";
    }

    /// <summary>
    /// Имя файла сетапа по имени и версии сборки: <c>Сборка модов 2026.10.02.exe</c>.
    /// Запрещённые в именах файлов символы заменяются подчёркиванием.
    /// </summary>
    /// <param name="name">Имя сборки.</param>
    /// <param name="version">Версия сборки.</param>
    /// <returns>Имя файла.</returns>
    public static string DefaultFileName(string name, string version)
    {
        string raw = $"{(name ?? string.Empty).Trim()} {(version ?? string.Empty).Trim()}".Trim();
        var safe = new StringBuilder(raw.Length);
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in raw)
        {
            safe.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string stem = safe.ToString().Trim().TrimEnd('.');
        return (stem.Length == 0 ? "setup" : stem) + ".exe";
    }

    /// <summary>Версия сборки по умолчанию — сегодняшняя дата: <c>2026.10.02</c>.</summary>
    /// <param name="now">Момент.</param>
    /// <returns>Версия.</returns>
    public static string DefaultVersion(DateTime now) => now.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);

    /// <summary>Собирает сетап.</summary>
    /// <param name="request">Что и куда.</param>
    /// <param name="ct">Отмена — проверяется между модами.</param>
    /// <returns>Готовый сетап.</returns>
    /// <exception cref="SetupBundleException">Что-то не так со входом или запись не удалась.</exception>
    public static SetupBundleResult Build(SetupBundleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string name = request.Name?.Trim() ?? string.Empty;
        string version = request.Version?.Trim() ?? string.Empty;
        if ((ValidateName(name) ?? ValidateVersion(version)) is { } bad)
        {
            throw new SetupBundleException(bad);
        }

        string output = FullPath(request.OutputPath, "файл сетапа");
        string stub = FullPath(request.StubPath, "заглушка установщика");
        CheckStub(stub);

        if (!output.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupBundleException("Сетап должен быть файлом .exe: " + output);
        }

        if (string.Equals(output, stub, StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupBundleException("Сетап нельзя записать поверх самой заглушки.");
        }

        if (File.Exists(output) && !request.Overwrite)
        {
            throw new SetupBundleException("Файл уже существует: " + output);
        }

        if (request.ModFiles is null || request.ModFiles.Count == 0)
        {
            throw new SetupBundleException("Не выбрано ни одного мода.");
        }

        List<(string Path, SetupBundleComponent Component)> mods = ReadMods(request.ModFiles, output, ct);

        var manifest = new SetupBundleManifest
        {
            Name = name,
            Version = version,
            CreatedUtc = (request.CreatedUtc ?? DateTimeOffset.UtcNow).UtcDateTime
                .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            GameVersion = request.GameVersion?.Trim() ?? string.Empty,
            Components = mods.Select(m => m.Component).ToArray(),
        };

        string dir = Path.GetDirectoryName(output)!;
        string temp = Path.Combine(dir, Path.GetFileName(output) + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(dir);
            WriteSetup(temp, stub, manifest, mods, ct);

            // Готовый файл читается заново той же логикой, что у заглушки: что не прочиталось
            // здесь, не прочитается и у человека.
            SetupBundleReader.Read(temp, verifyContent: true);

            File.Move(temp, output, request.Overwrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(temp);
            throw new SetupBundleException("Сетап не записан: " + ex.Message, ex);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return new SetupBundleResult(output, new FileInfo(output).Length, manifest);
    }

    private static void WriteSetup(
        string temp,
        string stub,
        SetupBundleManifest manifest,
        List<(string Path, SetupBundleComponent Component)> mods,
        CancellationToken ct)
    {
        using FileStream target = new(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        using (FileStream source = new(stub, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            source.CopyTo(target);
        }

        long payloadOffset = target.Position;

        // ZIP пишется отдельно и только потом дописывается: смещения в его каталоге обязаны
        // считаться от начала самого ZIP, а не от начала exe.
        string zipTemp = Path.Combine(Path.GetTempPath(), "wotcockpit-setup-" + Guid.NewGuid().ToString("N") + ".zip");
        using (FileStream zipStream = new(zipTemp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose))
        {
            using (ZipArchive zip = new(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry manifestEntry = zip.CreateEntry(SetupBundleFormat.ManifestEntryName, CompressionLevel.Optimal);
                using (Stream s = manifestEntry.Open())
                {
                    byte[] json = new UTF8Encoding(false).GetBytes(manifest.ToJson());
                    s.Write(json);
                }

                foreach ((string path, SetupBundleComponent component) in mods)
                {
                    ct.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = zip.CreateEntry(component.File, CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(path);
                    using Stream s = entry.Open();
                    using FileStream mod = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    mod.CopyTo(s);
                }
            }

            long payloadLength = zipStream.Length;
            zipStream.Position = 0;
            zipStream.CopyTo(target);
            target.Write(SetupBundleFormat.CreateTrailer(payloadOffset, payloadLength));
        }

        target.Flush(flushToDisk: true);
    }

    /// <summary>Проверяет каждый мод и собирает его запись для описания.</summary>
    private static List<(string Path, SetupBundleComponent Component)> ReadMods(
        IReadOnlyList<string> files,
        string output,
        CancellationToken ct)
    {
        var result = new List<(string, SetupBundleComponent)>();
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanner = new ModScanner();

        foreach (string raw in files)
        {
            ct.ThrowIfCancellationRequested();
            string path = FullPath(raw, "файл мода");

            if (string.Equals(path, output, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupBundleException("Сетап нельзя записать поверх файла мода: " + path);
            }

            if (!ModFileNames.IsEnabledFile(path))
            {
                throw new SetupBundleException("В сетап кладутся только собранные моды *.mtmod: " + path);
            }

            InstalledMod mod = scanner.ScanOne(Path.GetDirectoryName(path)!, path)
                               ?? throw new SetupBundleException("Файла мода нет: " + path);

            if (mod.ArchiveStatus != ModArchiveStatus.Ok)
            {
                throw new SetupBundleException($"Мод не читается как ZIP ({mod.ArchiveError}): {path}");
            }

            if (mod.EntryCount == 0)
            {
                throw new SetupBundleException("Архив мода пуст — игра его не загрузит: " + path);
            }

            if (mod.MetaStatus != ModMetaStatus.Present || string.IsNullOrWhiteSpace(mod.ModId))
            {
                throw new SetupBundleException("В архиве мода нет meta.xml с <id> — сетапу не по чему узнать мод в игре: " + path);
            }

            string id = mod.ModId!;
            if (ids.TryGetValue(id, out string? first))
            {
                throw new SetupBundleException($"Мод {id} выбран дважды:\n{first}\n{path}");
            }

            string fileName = Path.GetFileName(path);
            if (!names.Add(fileName))
            {
                throw new SetupBundleException("Два мода с одинаковым именем файла: " + fileName);
            }

            ids[id] = path;

            (long size, string sha) = Hash(path);
            result.Add((path, new SetupBundleComponent
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(mod.MetaName) ? id : mod.MetaName!.Trim(),
                Version = mod.MetaVersion?.Trim() ?? ModFileNames.VersionFromFileName(fileName) ?? string.Empty,
                Description = mod.Description?.Trim() ?? string.Empty,
                File = SetupBundleFormat.ModsFolder + fileName,
                Size = size,
                Sha256 = sha,
            }));
        }

        return result;
    }

    /// <summary>Заглушка существует, это exe и к ней ещё ничего не дописано.</summary>
    private static void CheckStub(string stub)
    {
        if (!File.Exists(stub))
        {
            throw new SetupBundleException(
                "Заглушка установщика не найдена: " + stub + "\nОна собирается вместе с кокпитом (проект WotModCockpit.Setup).");
        }

        try
        {
            using FileStream fs = new(stub, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] mz = new byte[2];
            if (fs.Length < 64 || fs.Read(mz) != 2 || mz[0] != 'M' || mz[1] != 'Z')
            {
                throw new SetupBundleException("Заглушка установщика — не exe: " + stub);
            }

            if (SetupBundleFormat.HasTrailer(fs))
            {
                throw new SetupBundleException("Вместо заглушки указан уже собранный сетап: " + stub);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SetupBundleException("Заглушка установщика не читается: " + ex.Message, ex);
        }
    }

    private static (long Size, string Sha256) Hash(string path)
    {
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] hash = SHA256.HashData(fs);
            return (fs.Length, Convert.ToHexString(hash).ToLowerInvariant());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SetupBundleException("Мод не читается: " + ex.Message + "\n" + path, ex);
        }
    }

    private static string FullPath(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new SetupBundleException($"Не указан путь: {what}.");
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SetupBundleException($"Негодный путь ({what}): {path}", ex);
        }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // недописанный временный файл — не повод терять настоящую ошибку
        }
    }
}
