using System.Globalization;
using System.Text.RegularExpressions;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Виртуальный путь из <c>python.log</c> → реальный файл на диске. Реализация <see cref="ITracebackMapper"/>.
/// <para>
/// Пути в логе виртуальные — это пути внутри ресурсов игры (<c>res/packages/scripts.pkg</c> либо
/// содержимое <c>.mtmod</c>), на диске их нет. Пробы идут строго в порядке
/// <b>проект → исходники клиента → россыпь в игре</b>:
/// </para>
/// <list type="number">
///   <item><c>scripts/client/gui/mods/mod_hello_tanker.py</c> →
///         <c>&lt;SrcDir&gt;\res\scripts\client\gui\mods\mod_hello_tanker.py</c>
///         (виртуальный путь дописывается к <c>&lt;SrcDir&gt;\res\</c> один в один);</item>
///   <item><c>scripts/common/items/vehicles.py</c> → <c>&lt;wotSrcDir&gt;\common\items\vehicles.py</c>.
///         ЛОВУШКА: в декомпилированном дереве префикса <c>scripts/</c> НЕТ, внутри сразу
///         <c>client\</c>, <c>client_common\</c>, <c>common\</c>. Проверено на эталонном дереве
///         клиента 1.44: <c>&lt;wotSrcDir&gt;\client\predefined_hosts.py</c> и
///         <c>&lt;wotSrcDir&gt;\common\items\vehicles.py</c> существуют;</item>
///   <item><c>&lt;game&gt;\res_mods\&lt;ver&gt;\scripts\...</c> — dev-раскладка либо чужой мод.
///         Плюс пути вида <c>res_mods/configs/xvm/py_macro\xvm\hitLog.py</c>, которые XVM печатает
///         относительно корня игры.</item>
/// </list>
/// </summary>
public sealed class WotSrcTracebackMapper : ITracebackMapper
{
    /// <summary>Корни декомпилированного дерева клиента: в нём нет уровня <c>scripts/</c>.</summary>
    private static readonly string[] ClientRoots = { "client", "client_common", "common", "server", "editor" };

    /// <inheritdoc />
    public string? MapToDisk(TracebackFrame f, ModProject p, string wotSrcDir)
        => Map(f, p, wotSrcDir, null).DiskPath;

    /// <inheritdoc />
    public TracebackMapping Map(TracebackFrame f, ModProject? p, string wotSrcDir, GameInstall? game)
    {
        ArgumentNullException.ThrowIfNull(f);

        string vp = PythonLogParser.NormalizeVirtualPath(f.VirtualPath);
        if (vp.Length == 0)
            return TracebackMapping.Unresolved(f);

        // 0. Абсолютный путь в логе (бывает у сторонних загрузчиков) — брать как есть.
        if (Path.IsPathRooted(f.VirtualPath) && File.Exists(f.VirtualPath))
            return new TracebackMapping(f, Path.GetFullPath(f.VirtualPath), FrameOrigin.ModProject, f.Line, false);

        // 1. Файл проекта разработчика.
        string? project = TryProject(vp, p);
        if (project is not null)
            return new TracebackMapping(f, project, FrameOrigin.ModProject, f.Line, false);

        // 2. Декомпилированный исходник клиента. Номер строки ПРИБЛИЗИТЕЛЬНЫЙ.
        string? client = TryClientSource(vp, wotSrcDir);
        if (client is not null)
            return new TracebackMapping(f, client, FrameOrigin.ClientSource, f.Line, true);

        // 3. Россыпь в игре (в т.ч. dev-раскладка и конфиги XVM относительно корня игры).
        string? resMods = TryGameDirs(vp, game);
        if (resMods is not null)
            return new TracebackMapping(f, resMods, FrameOrigin.ResMods, f.Line, false);

        return TracebackMapping.Unresolved(f);
    }

    /// <inheritdoc />
    public IReadOnlyList<TracebackFrame> ExtractFrames(LogEntry entry)
    {
        if (entry is null)
            return Array.Empty<TracebackFrame>();

        var frames = new List<TracebackFrame>();

        // 1. Тело traceback: '  File "scripts/...", line 4845, in getItemByCompactDescr'.
        foreach (string line in entry.Continuation)
            AddIfFrame(line, frames);

        // 2. Тот же кадр, но приехавший ОТДЕЛЬНОЙ записью с собственным таймстампом — так печатают
        //    трейсбеки XFW/OpenWG и XVM. Реальный пример из python.log:
        //    '2026-08-28 01:16:28.894: ERROR:   File "res_mods/configs/xvm/py_macro\xvm\hitLog.py", line 953, in ...'
        AddIfFrame(entry.Message, frames);

        // 3. Кадр из заголовка записи: '(scripts/common/items/vehicles.py, 4850)'. Function = null.
        if (!string.IsNullOrEmpty(entry.SourceFile) && entry.SourceLine is int headLine)
        {
            frames.Add(new TracebackFrame(entry.SourceFile, headLine, null)
            {
                RawLine = entry.RawLine
            });
        }

        return frames;
    }

    // ---------------------------------------------------------------- пробы

    /// <summary>Кадр указывает на файл проекта разработчика?</summary>
    /// <param name="vp">Нормализованный виртуальный путь.</param>
    /// <param name="p">Проект или <c>null</c>.</param>
    /// <returns>Существующий путь на диске либо <c>null</c>.</returns>
    private static string? TryProject(string vp, ModProject? p)
    {
        if (p is null || string.IsNullOrEmpty(p.ProjectDir))
            return null;

        // Виртуальный путь дописывается к <SrcDir>\res\ один в один — и для точки входа,
        // и для любого модуля пакета мода.
        if (PythonLogParser.IsMyVirtualPath(vp, p) ||
            vp.StartsWith("scripts/client/gui/mods/", StringComparison.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(p.SrcDir, "res", ToRelativeWindowsPath(vp));
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        // Кадр без префикса scripts/ (сторонний загрузчик печатает './main.py') —
        // пробуем свести к пакету мода по имени файла.
        if (!vp.Contains('/', StringComparison.Ordinal) && !string.IsNullOrEmpty(p.PackageName))
        {
            string candidate = Path.Combine(
                p.SrcDir, "res", "scripts", "client", "gui", "mods", p.PackageName,
                ToRelativeWindowsPath(vp));
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>Кадр указывает на декомпилированный исходник клиента?</summary>
    /// <param name="vp">Нормализованный виртуальный путь.</param>
    /// <param name="wotSrcDir">Каталог <c>.scratch\wot-src</c>.</param>
    /// <returns>Существующий путь на диске либо <c>null</c>.</returns>
    private static string? TryClientSource(string vp, string wotSrcDir)
    {
        if (string.IsNullOrWhiteSpace(wotSrcDir) || !Directory.Exists(wotSrcDir))
            return null;

        // 'scripts/common/items/vehicles.py' -> '<wotSrcDir>\common\items\vehicles.py'
        string rest = vp.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase) ? vp["scripts/".Length..] : vp;
        if (rest.Length == 0)
            return null;

        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        string root = slash < 0 ? rest : rest[..slash];

        // Первый сегмент должен быть одним из корней дерева, иначе это не клиентский путь.
        bool knownRoot = false;
        foreach (string r in ClientRoots)
        {
            if (root.Equals(r, StringComparison.OrdinalIgnoreCase))
            {
                knownRoot = true;
                break;
            }
        }

        if (!knownRoot)
            return null;

        string candidate = Path.Combine(wotSrcDir, ToRelativeWindowsPath(rest));
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    /// <summary>Кадр указывает на файл в каталоге игры (россыпь res_mods или путь от корня)?</summary>
    /// <param name="vp">Нормализованный виртуальный путь.</param>
    /// <param name="game">Установка игры или <c>null</c>.</param>
    /// <returns>Существующий путь на диске либо <c>null</c>.</returns>
    private static string? TryGameDirs(string vp, GameInstall? game)
    {
        if (game is null)
            return null;

        string rel = ToRelativeWindowsPath(vp);

        // 1) res_mods\<ver>\scripts\... — то, что кладёт build.py --dev.
        if (!string.IsNullOrEmpty(game.ResModsDir))
        {
            string candidate = Path.Combine(game.ResModsDir, rel);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        // 2) Путь относительно корня игры: 'res_mods/configs/xvm/py_macro\xvm\hitLog.py'.
        if (!string.IsNullOrEmpty(game.RootPath))
        {
            string candidate = Path.Combine(game.RootPath, rel);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>Кадр traceback из строки, если строка на него похожа.</summary>
    /// <param name="line">Строка лога.</param>
    /// <param name="sink">Куда складывать.</param>
    private static void AddIfFrame(string? line, List<TracebackFrame> sink)
    {
        if (string.IsNullOrEmpty(line))
            return;

        Match m = PythonLogParser.TracebackFrameRe.Match(line);
        if (!m.Success)
            return;

        if (!int.TryParse(m.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ln))
            return;

        string? fn = m.Groups["fn"].Success ? m.Groups["fn"].Value.Trim() : null;
        if (string.IsNullOrEmpty(fn))
            fn = null;

        sink.Add(new TracebackFrame(m.Groups["path"].Value.Replace('\\', '/'), ln, fn) { RawLine = line });
    }

    /// <summary>Виртуальный путь → относительный путь Windows, безопасный для <c>Path.Combine</c>.</summary>
    /// <param name="vp">Нормализованный виртуальный путь с прямыми слэшами.</param>
    /// <returns>Тот же путь обратными слэшами, без ведущего разделителя и без выходов вверх.</returns>
    private static string ToRelativeWindowsPath(string vp)
    {
        string[] parts = vp.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);

        foreach (string part in parts)
        {
            if (part == "." || part == "..")
                continue;                       // наружу из каталога не выпускаем

            kept.Add(part);
        }

        return kept.Count == 0 ? string.Empty : Path.Combine(kept.ToArray());
    }
}
