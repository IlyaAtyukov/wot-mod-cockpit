using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.Core.Bundle;

/// <summary>Прочитанный сетап.</summary>
/// <param name="Path">Файл сетапа.</param>
/// <param name="FileSize">Размер файла.</param>
/// <param name="StubLength">Сколько занимает заглушка.</param>
/// <param name="PayloadLength">Сколько занимает ZIP с модами.</param>
/// <param name="Manifest">Описание сборки.</param>
public sealed record SetupBundleInfo(
    string Path,
    long FileSize,
    long StubLength,
    long PayloadLength,
    SetupBundleManifest Manifest);

/// <summary>
/// Чтение готового сетапа — та же проверка, которую сделает заглушка у человека, плюс сверка
/// содержимого. Им сборщик проверяет только что записанный файл, а тесты — формат.
/// </summary>
public static class SetupBundleReader
{
    private const long MaxManifestBytes = 1024 * 1024;

    /// <summary>Читает и проверяет сетап.</summary>
    /// <param name="path">Файл сетапа.</param>
    /// <param name="verifyContent">
    /// Сверить каждый мод: размер, SHA-256 и <c>&lt;id&gt;</c> в его meta.xml. Без этого
    /// проверяются только трейлер, описание и наличие файлов в ZIP.
    /// </param>
    /// <returns>Что внутри.</returns>
    /// <exception cref="SetupBundleException">Сетап не читается или не сходится с описанием.</exception>
    public static SetupBundleInfo Read(string path, bool verifyContent = true)
    {
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            (_, long offset, long length) = SetupBundleFormat.ReadTrailer(fs);

            using ZipArchive zip = new(new PayloadStream(fs, offset, length), ZipArchiveMode.Read, leaveOpen: false);
            SetupBundleManifest manifest = ReadManifest(zip);
            Check(manifest, zip, verifyContent);

            return new SetupBundleInfo(Path.GetFullPath(path), fs.Length, offset, length, manifest);
        }
        catch (InvalidDataException ex)
        {
            throw new SetupBundleException("архив внутри сетапа не читается: " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SetupBundleException("сетап не читается: " + ex.Message, ex);
        }
    }

    /// <summary>Достаёт из сетапа байты одного мода.</summary>
    /// <param name="path">Файл сетапа.</param>
    /// <param name="modId">Идентификатор мода.</param>
    /// <returns>Содержимое файла мода.</returns>
    public static byte[] ReadComponent(string path, string modId)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        (_, long offset, long length) = SetupBundleFormat.ReadTrailer(fs);
        using ZipArchive zip = new(new PayloadStream(fs, offset, length), ZipArchiveMode.Read, leaveOpen: false);
        SetupBundleManifest manifest = ReadManifest(zip);

        SetupBundleComponent component = manifest.Components
            .FirstOrDefault(c => string.Equals(c.Id, modId, StringComparison.OrdinalIgnoreCase))
            ?? throw new SetupBundleException("в сетапе нет мода " + modId);

        ZipArchiveEntry entry = zip.GetEntry(component.File)
            ?? throw new SetupBundleException("в архиве нет файла " + component.File);

        using Stream s = entry.Open();
        using MemoryStream ms = new();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static SetupBundleManifest ReadManifest(ZipArchive zip)
    {
        ZipArchiveEntry entry = zip.GetEntry(SetupBundleFormat.ManifestEntryName)
            ?? throw new SetupBundleException("в сетапе нет описания сборки " + SetupBundleFormat.ManifestEntryName);

        if (entry.Length is <= 0 or > MaxManifestBytes)
        {
            throw new SetupBundleException("описание сборки пустое или слишком большое");
        }

        using Stream s = entry.Open();
        using StreamReader reader = new(s, Encoding.UTF8);
        SetupBundleManifest manifest = SetupBundleManifest.FromJson(reader.ReadToEnd());

        if (manifest.Format != SetupBundleFormat.FormatVersion)
        {
            throw new SetupBundleException($"описание сборки в формате {manifest.Format}, ожидался {SetupBundleFormat.FormatVersion}");
        }

        return manifest;
    }

    private static void Check(SetupBundleManifest manifest, ZipArchive zip, bool verifyContent)
    {
        if (manifest.Components.Count == 0)
        {
            throw new SetupBundleException("в сетапе нет ни одного мода");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SetupBundleComponent c in manifest.Components)
        {
            string fileName = c.FileName;
            if (string.IsNullOrWhiteSpace(c.Id) ||
                !c.File.StartsWith(SetupBundleFormat.ModsFolder, StringComparison.Ordinal) ||
                fileName.Length == 0 ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !ModFileNames.IsEnabledFile(fileName))
            {
                throw new SetupBundleException($"негодная запись мода «{c.Id}»: {c.File}");
            }

            if (!ids.Add(c.Id) || !names.Add(fileName))
            {
                throw new SetupBundleException("мод описан дважды: " + c.Id);
            }

            ZipArchiveEntry entry = zip.GetEntry(c.File)
                ?? throw new SetupBundleException("в архиве нет файла " + c.File);

            if (entry.Length != c.Size)
            {
                throw new SetupBundleException($"размер {c.File} не сходится с описанием");
            }

            if (verifyContent)
            {
                VerifyEntry(c, entry);
            }
        }
    }

    /// <summary>Сверяет SHA-256 записи и <c>&lt;id&gt;</c> внутри неё с описанием.</summary>
    private static void VerifyEntry(SetupBundleComponent c, ZipArchiveEntry entry)
    {
        using MemoryStream bytes = new(capacity: (int)Math.Min(entry.Length, int.MaxValue));
        using (Stream s = entry.Open())
        {
            s.CopyTo(bytes);
        }

        string sha = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))).ToLowerInvariant();
        if (!string.Equals(sha, c.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupBundleException($"контрольная сумма {c.File} не сходится с описанием");
        }

        bytes.Position = 0;
        using ZipArchive mod = new(bytes, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry? meta = mod.Entries.FirstOrDefault(
            e => string.Equals(e.FullName.Replace('\\', '/'), "meta.xml", StringComparison.OrdinalIgnoreCase));
        if (meta is null)
        {
            throw new SetupBundleException($"в {c.File} нет meta.xml");
        }

        string? id;
        try
        {
            using Stream metaStream = meta.Open();
            using XmlReader reader = XmlReader.Create(metaStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            id = XDocument.Load(reader).Descendants()
                .Where(e => !e.HasElements && string.Equals(e.Name.LocalName, "id", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value.Trim())
                .FirstOrDefault(v => v.Length > 0);
        }
        catch (XmlException ex)
        {
            throw new SetupBundleException($"meta.xml в {c.File} не разбирается: {ex.Message}", ex);
        }

        if (!string.Equals(id, c.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupBundleException($"id в meta.xml файла {c.File} ({id}) не совпадает с описанием ({c.Id})");
        }
    }
}
