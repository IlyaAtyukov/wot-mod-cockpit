using System.Text;
using System.Text.Json;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Settings;

/// <summary>
/// Дизайнер настроек поверх файловой системы проекта мода.
///
/// <para><b>Раскладка, которую служба поддерживает.</b></para>
/// <code>
/// &lt;ProjectDir&gt;\
///     settings.design.json                                     ← источник истины о форме
///     src\res\scripts\client\gui\mods\&lt;pkg&gt;\
///         settings_schema.py                                   ← машинный, участок по маркерам
///         settings.py                                          ← создаётся ОДИН раз, дальше ваш
/// </code>
///
/// <para><b>Почему служба не правит <c>main.py</c>.</b> Вызов <c>settings.register()</c> обязан
/// стоять в <c>onAccountShowGUI</c>, а где именно в теле обработчика — знает только автор мода.
/// Механизм вставки, который есть в кокпите для рецептов, умеет лишь дописывать в конец файла;
/// применённый к <c>main.py</c> он положил бы вызов на уровень модуля — то есть выполнил бы его
/// при импорте, что как раз и запрещено (WOT001, и настройки при этом читались бы до входа
/// в ангар). Поэтому служба только докладывает, подключён ли модуль
/// (<see cref="IsWiredUp"/>), и отдаёт готовый фрагмент.</para>
/// </summary>
public sealed class SettingsDesignerService : ISettingsDesignerService
{
    /// <summary>Имя документа дизайнера в корне проекта мода.</summary>
    public const string DesignFileName = "settings.design.json";

    /// <inheritdoc />
    public string DesignFilePath(ModProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Path.Combine(project.ProjectDir, DesignFileName);
    }

    /// <inheritdoc />
    public string PackageDirectory(ModProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(project.PackageName)) return string.Empty;

        return Path.Combine(project.SrcDir, "res", "scripts", "client", "gui", "mods", project.PackageName.Trim());
    }

    /// <inheritdoc />
    public bool HasDesign(ModProject project) => File.Exists(DesignFilePath(project));

    /// <inheritdoc />
    public SettingsDesign? Load(ModProject project, out string error)
    {
        error = string.Empty;
        string path = DesignFilePath(project);

        if (!File.Exists(path)) return null;

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            SettingsDesign design = SettingsDesign.FromJson(json);

            // Файл правят руками и переносят между машинами, поэтому пустой linkage —
            // штатный вход, а не «этого не бывает». Подставляем идентификатор мода.
            design.Template.Linkage = design.Template.Linkage.Trim();
            if (design.Template.Linkage.Length == 0) design.Template.Linkage = project.ModId;

            return design;
        }
        catch (JsonException ex)
        {
            error = $"{DesignFileName} не разбирается как JSON: {ex.Message}";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{DesignFileName} не читается: {ex.Message}";
            return null;
        }
    }

    /// <inheritdoc />
    public string Save(ModProject project, SettingsDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        string path = DesignFilePath(project);

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(path, design.ToJson() + Environment.NewLine, new UTF8Encoding(false));
            return string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"{DesignFileName} не записывается: {ex.Message}";
        }
    }

    /// <inheritdoc />
    public SettingsApplyResult Apply(ModProject project, SettingsDesign design, bool saveDesign = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(design);

        string designPath = DesignFilePath(project);

        if (string.IsNullOrWhiteSpace(project.ProjectDir))
        {
            return SettingsApplyResult.Failed(designPath, "у проекта не задан каталог.");
        }

        string packageDir = PackageDirectory(project);
        if (packageDir.Length == 0)
        {
            return SettingsApplyResult.Failed(designPath,
                "у мода не задан python-пакет (PackageName). Настройкам нужен модуль внутри пакета: " +
                "точка входа mod_*.py компилируется в байткод через tools/py27c.py и для этого не годится.");
        }

        var optionErrors = new List<string>(design.Code.Validate());

        // Столкновение с рецептом 09. Тот держит в config.json параметры, которые правит
        // РАЗРАБОТЧИК, и сливает их со своим DEFAULT_CONFIG; наш settings.py переписывает свой
        // файл ЦЕЛИКОМ по словарю игрока. Указать им один файл — значит завести два источника
        // умолчаний и терять чужие ключи при каждом «Применить».
        if (SamePath(design.Code.ConfigRelativePath, project.GameConfigRelative))
        {
            optionErrors.Add(
                $"путь к значениям настроек ('{design.Code.ConfigRelativePath}') совпадает с конфигом мода " +
                "из рецепта 09. Модуль настроек переписывает свой файл целиком при каждом «Применить» " +
                "и потеряет ключи, которые туда положил конфиг мода. Дайте настройкам отдельный файл, " +
                $"например mods/configs/{project.PackageName}/settings.json.");
        }

        if (optionErrors.Count > 0) return new SettingsApplyResult(null, [], designPath, optionErrors);

        GeneratedSettingsCode code = SettingsCodeGenerator.Generate(design.Template, design.Code);
        if (!code.IsValid)
        {
            IReadOnlyList<string> errors =
            [
                .. code.Validation.Issues
                    .Where(i => i.Severity == TemplateIssueSeverity.Error)
                    .Select(i => i.ToString())
            ];

            return new SettingsApplyResult(code, [], designPath, errors);
        }

        var writes = new List<RegionWriteResult>(2);
        var errorList = new List<string>();

        try
        {
            Directory.CreateDirectory(packageDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsApplyResult(code, [], designPath, [$"каталог пакета не создаётся: {ex.Message}"]);
        }

        // settings_schema.py: участок между маркерами переписывается, всё вне участка живёт.
        writes.Add(GeneratedRegionWriter.Write(
            Path.Combine(packageDir, code.SchemaFileName),
            code.SchemaRegionBody,
            GeneratedRegionWriter.PythonPrologue,
            code.SchemaInitialTail));

        // settings.py: создаётся один раз. Второй генерации он не подлежит — это уже код автора.
        writes.Add(GeneratedRegionWriter.WriteOnce(
            Path.Combine(packageDir, code.UserFileName),
            code.UserModuleCode));

        foreach (RegionWriteResult w in writes)
        {
            if (!w.Ok) errorList.Add(w.ToString());
        }

        // Без __init__.py каталог не пакет, и `from . import settings` не сработает.
        // Молча создавать файлы, о которых пользователь не просил, служба не станет, но
        // промолчать о такой поломке было бы хуже.
        string initPy = Path.Combine(packageDir, "__init__.py");
        if (!File.Exists(initPy))
        {
            errorList.Add($"в каталоге пакета нет __init__.py ({initPy}) — импорт " +
                          $"'from . import {design.Code.UserModuleName}' не сработает.");
        }

        if (saveDesign && errorList.Count == 0)
        {
            design.GeneratedVarNames.Clear();
            foreach (string name in VarNames(design.Template)) design.GeneratedVarNames.Add(name);
            design.GeneratedSettingsVersion = design.Template.SettingsVersion;

            string saveError = Save(project, design);
            if (saveError.Length > 0) errorList.Add(saveError);
        }

        return new SettingsApplyResult(code, writes, designPath, errorList);
    }

    /// <inheritdoc />
    public bool NeedsVersionBump(SettingsDesign design, out string reason)
    {
        ArgumentNullException.ThrowIfNull(design);
        reason = string.Empty;

        // Ещё ни разу не генерировали — сравнивать не с чем, поднимать нечего.
        if (design.GeneratedSettingsVersion is not int generated) return false;

        IReadOnlyList<string> now = VarNames(design.Template);
        if (now.SequenceEqual(design.GeneratedVarNames, StringComparer.Ordinal)) return false;

        // Версию уже подняли — предупреждать не о чем.
        if (design.Template.SettingsVersion is int current && current > generated) return false;

        string[] added = [.. now.Except(design.GeneratedVarNames, StringComparer.Ordinal)];
        string[] removed = [.. design.GeneratedVarNames.Except(now, StringComparer.Ordinal)];

        var sb = new StringBuilder("состав настроек изменился");
        if (added.Length > 0) sb.Append(", добавлено: ").Append(string.Join(", ", added));
        if (removed.Length > 0) sb.Append(", удалено: ").Append(string.Join(", ", removed));
        if (added.Length == 0 && removed.Length == 0) sb.Append(" (изменился порядок)");

        sb.Append($". Пока settingsVersion остаётся {Describe(design.Template.SettingsVersion)}, ModsSettingsAPI будет ")
          .Append("показывать СТАРУЮ форму из modsettings.dat, а новых ключей в возвращённом словаре не будет. ")
          .Append("Поднимите версию — учтите, что при этом значения игрока сбросятся на те, что лежат ")
          .Append("в шаблоне (здесь это его же сохранённые значения из settings.json).");

        reason = sb.ToString();
        return true;
    }

    /// <inheritdoc />
    public bool IsWiredUp(ModProject project, SettingsDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);

        string packageDir = PackageDirectory(project);
        if (packageDir.Length == 0 || !Directory.Exists(packageDir)) return false;

        string call = design.Code.UserModuleName + ".register(";
        string schemaFile = design.Code.SchemaFileName;
        string userFile = design.Code.UserFileName;

        foreach (string file in SafeEnumerate(packageDir))
        {
            string name = Path.GetFileName(file);
            if (string.Equals(name, schemaFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, userFile, StringComparison.OrdinalIgnoreCase)) continue;

            string text;
            try
            {
                text = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (text.Contains(call, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Два относительных пути указывают на один файл (слэши и регистр не важны).</summary>
    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

        static string Normalize(string p) => p.Trim().Replace('\\', '/').Trim('/');
    }

    /// <summary>Имена настроек шаблона в порядке обхода колонок, без служебного <c>enabled</c>.</summary>
    private static IReadOnlyList<string> VarNames(SettingsTemplate template) =>
        [.. template.AllControls.Where(c => c.ContributesSetting).Select(c => c.VarName!)];

    private static string Describe(int? version) =>
        version is int v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "не заданным";

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.py", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
