using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Одна строка списка модов — либо файл в каталоге игры, либо проект кокпита, которого в
/// игре ещё нет.
/// <para>
/// Вторая роль важна не меньше первой: «Shot Timer — не установлен» это ответ на вопрос
/// пользователя, а пустое место в списке — нет. У такой строки <see cref="IsInstalled"/>
/// равен <c>false</c>, пути нет, и все операции над файлом для неё погашены; доступна ровно
/// одна — поставить сборку из <c>dist\</c>.
/// </para>
/// <para>
/// Строка НИЧЕГО не делает с диском: она только переводит снимок
/// <see cref="InstalledMod"/> на язык разметки. Иконку ей отдаёт <see cref="ModsViewModel"/>.
/// </para>
/// </summary>
public sealed partial class InstalledModViewModel : ObservableObject
{
    private readonly ModIconPlaceholder _placeholder;

    /// <summary>Проект установленного своего мода; у строки «не установлен» он в <see cref="ProjectStatus"/>.</summary>
    private readonly ProjectModStatus? _installedProject;

    /// <summary>Создаёт строку по найденному в игре файлу.</summary>
    /// <param name="mod">Снимок файла.</param>
    /// <param name="project">
    /// Проект этого мода и его состояние в игре, если мод свой, — нужен только затем, чтобы
    /// показать, что имя или описание в <c>src\meta.xml</c> уже не такие, как в архиве, который
    /// игра грузит.
    /// </param>
    public InstalledModViewModel(InstalledMod mod, ProjectModStatus? project = null)
    {
        Mod = mod ?? throw new ArgumentNullException(nameof(mod));
        _installedProject = project;
        _placeholder = SafePlaceholder(mod.IconKey, mod.DisplayName);
        PlateBrush = MakeBrush(_placeholder.ColorHex);
    }

    private InstalledModViewModel(ProjectModStatus project)
    {
        ProjectStatus = project ?? throw new ArgumentNullException(nameof(project));
        _placeholder = SafePlaceholder(project.Project.ModId, project.Project.DisplayName);
        PlateBrush = MakeBrush(_placeholder.ColorHex);
    }

    /// <summary>Создаёт строку для проекта, которого в игре нет.</summary>
    /// <param name="project">Состояние проекта в игре.</param>
    /// <returns>Строка списка.</returns>
    public static InstalledModViewModel NotInstalled(ProjectModStatus project) => new(project);

    /// <summary>Снимок файла либо <c>null</c> у строки «не установлен».</summary>
    public InstalledMod? Mod { get; }

    /// <summary>Состояние проекта либо <c>null</c> у обычной строки.</summary>
    public ProjectModStatus? ProjectStatus { get; }

    // =================================================================== опознание

    /// <summary>Файл в каталоге игры существует.</summary>
    public bool IsInstalled => Mod is not null;

    /// <summary>Мод собран одним из проектов кокпита.</summary>
    public bool IsMine => Mod?.IsMine ?? true;

    /// <summary>Абсолютный путь к файлу либо пустая строка.</summary>
    public string FullPath => Mod?.FullPath ?? string.Empty;

    /// <summary>Имя файла либо пустая строка.</summary>
    public string FileName => Mod?.FileName ?? string.Empty;

    /// <summary>Идентификатор мода: из meta.xml архива, а если его нет — из паспорта проекта.</summary>
    public string ModId => Mod?.ModId ?? Mod?.Mine?.ModId ?? ProjectStatus?.Project.ModId ?? string.Empty;

    /// <summary>Мод виден игре (<c>.mtmod</c>).</summary>
    public bool IsEnabled => Mod?.State == ModState.Enabled;

    /// <summary>Имя для списка: meta.xml → паспорт проекта → имя файла без версии.</summary>
    public string Title => Mod?.DisplayName ?? ProjectStatus?.Project.DisplayName ?? string.Empty;

    /// <summary>Версия мода.</summary>
    public string Version => Mod?.DisplayVersion ?? ProjectStatus?.Project.Version ?? string.Empty;

    /// <summary>Вторая строка: идентификатор и версия.</summary>
    public string Subtitle
    {
        get
        {
            string id = ModId.Length > 0 ? ModId : "без meta.xml";
            return Version.Length > 0 ? id + " · " + Version : id;
        }
    }

    /// <summary>Подпапка каталога модов либо пустая строка.</summary>
    public string Group => Mod?.GroupName ?? string.Empty;

    /// <summary>
    /// Заголовок группы списка. Чужие моды группируются по той подпапке, в которой они лежат
    /// в каталоге игры: эта раскладка уже существует, её сделал модпак, и ломать её незачем.
    /// </summary>
    public string GroupKey
    {
        get
        {
            if (IsMine)
            {
                return "МОИ МОДЫ";
            }

            if (IsInstalled && !IsEnabled)
            {
                return "ВЫКЛЮЧЕННЫЕ";
            }

            return Group.Length > 0 ? "ЧУЖИЕ · " + Group : "ЧУЖИЕ · корень каталога";
        }
    }

    /// <summary>Порядок групп: свои сверху, выключенные в конце.</summary>
    public int GroupSort
    {
        get
        {
            if (IsMine)
            {
                return 0;
            }

            if (IsInstalled && !IsEnabled)
            {
                return 3;
            }

            return Group.Length > 0 ? 2 : 1;
        }
    }

    /// <summary>Значок происхождения.</summary>
    public string OriginBadge => IsMine ? "МОЙ ПРОЕКТ" : "ЧУЖОЙ";

    /// <summary>
    /// Показывать кнопку «Включить». У строки проекта, которого в игре нет, включать нечего,
    /// и погашенная кнопка была бы не подсказкой, а мусором.
    /// </summary>
    public bool ShowEnableAction => IsInstalled && !IsEnabled;

    /// <summary>Показывать кнопку «Выключить».</summary>
    public bool ShowDisableAction => IsInstalled && IsEnabled;

    // =================================================================== поля деталей

    /// <summary>Состояние файла словами.</summary>
    public string StateText => !IsInstalled
        ? "в игре не установлен"
        : IsEnabled ? "ВКЛЮЧЁН" : "ВЫКЛЮЧЕН (.mtmod.off)";

    /// <summary>Размер файла.</summary>
    public string SizeText => Mod is null
        ? string.Empty
        : Mod.SizeBytes.ToString("N0", CultureInfo.CurrentCulture) + " Б";

    /// <summary>Дата последней записи и «сколько назад».</summary>
    public string DateText
    {
        get
        {
            if (Mod is null || Mod.LastWriteTimeUtc == default)
            {
                return string.Empty;
            }

            TimeSpan ago = DateTimeOffset.UtcNow - Mod.LastWriteTimeUtc;
            string when = ago.TotalDays >= 2
                ? $"{(int)ago.TotalDays} дн. назад"
                : ago.TotalHours >= 2
                    ? $"{(int)ago.TotalHours} ч. назад"
                    : $"{Math.Max(1, (int)ago.TotalMinutes)} мин. назад";

            return Mod.LastWriteTimeUtc.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)
                   + " (" + when + ")";
        }
    }

    /// <summary>Число записей архива и метод упаковки.</summary>
    public string ContentText
    {
        get
        {
            if (Mod is null)
            {
                return string.Empty;
            }

            if (Mod.ArchiveStatus != ModArchiveStatus.Ok)
            {
                return ArchiveStatusText;
            }

            string stored = Mod.CompressedEntryCount == 0
                ? "все записи Stored"
                : $"СЖАТЫХ ЗАПИСЕЙ: {Mod.CompressedEntryCount} — движок такой мод не загрузит";

            return Mod.EntryCount.ToString("N0", CultureInfo.CurrentCulture) + " записей · " + stored;
        }
    }

    /// <summary>Архив прочитан целиком и все записи Stored.</summary>
    public bool HasCompressedEntries => Mod is { ArchiveStatus: ModArchiveStatus.Ok, CompressedEntryCount: > 0 };

    /// <summary>Мод исполняет питоновский код или это чистые ресурсы.</summary>
    public string TypeText
    {
        get
        {
            if (Mod is null)
            {
                return string.Empty;
            }

            if (Mod.ArchiveStatus != ModArchiveStatus.Ok)
            {
                return "состав неизвестен — архив не прочитан";
            }

            return Mod.HasEntryPoint
                ? "Python-мод · точка входа: " + string.Join(", ", Mod.EntryPointModules)
                : "ресурсный мод (питоновской точки входа нет)";
        }
    }

    /// <summary>Нативные модули — повод трогать такой мод осторожнее всего.</summary>
    public bool HasNativeCode => Mod?.HasNativeCode ?? false;

    /// <summary>Какие именно нативные модули лежат в архиве.</summary>
    public string NativeText => Mod is null || !Mod.HasNativeCode
        ? string.Empty
        : "Мод несёт нативные модули (" + string.Join(", ", Mod.NativeEntries.Take(4).Select(Path.GetFileName))
          + (Mod.NativeEntries.Count > 4 ? ", …" : string.Empty)
          + ") — он работает внутри процесса игры, и от него могут зависеть другие моды сборки.";

    /// <summary>Состояние meta.xml.</summary>
    public string MetaText => Mod?.MetaStatus switch
    {
        ModMetaStatus.Present => "есть",
        ModMetaStatus.Missing => "нет — имя и версия выведены из имени файла",
        ModMetaStatus.Broken => "повреждён: " + (Mod?.MetaError ?? "не разбирается как XML"),
        _ => "неизвестно — архив не прочитан"
    };

    /// <summary>Чем кончилась попытка прочитать архив.</summary>
    public string ArchiveStatusText => Mod?.ArchiveStatus switch
    {
        ModArchiveStatus.Ok => string.Empty,
        ModArchiveStatus.Broken => "архив не открывается как ZIP: " + (Mod?.ArchiveError ?? "повреждён"),
        ModArchiveStatus.Locked => "файл занят другим процессом — обычно это запущенная игра или открытый архиватор",
        ModArchiveStatus.AccessDenied => "отказано в доступе к файлу",
        ModArchiveStatus.Missing => "файла уже нет на диске",
        _ => string.Empty
    };

    /// <summary>Архив прочитать не удалось.</summary>
    public bool HasArchiveProblem => Mod is not null
                                     && Mod.ArchiveStatus != ModArchiveStatus.Ok
                                     && Mod.ArchiveStatus != ModArchiveStatus.NotRead;

    /// <summary>
    /// Описание из meta.xml. У установленного мода — ТОЛЬКО из архива: строка показывает то, что
    /// стоит в игре, и описание проекта на место пустого в архиве не подставляется — иначе
    /// карточка выдала бы за «в игре» то, чего там нет. У строки «не установлен» — из проекта.
    /// </summary>
    public string Description => Mod is not null
        ? Mod.Description ?? string.Empty
        : ProjectStatus?.Project.Description ?? string.Empty;

    // =================================================================== имя и описание в проекте

    /// <summary>
    /// Проект этой строки: у установленного своего мода — найденный по нему проект, у строки
    /// «не установлен» — сам проект. У чужого мода — <c>null</c>.
    /// </summary>
    public ModProjectInfo? ProjectInfo => (_installedProject ?? ProjectStatus)?.Project;

    /// <summary>
    /// Этот файл игра действительно грузит: он включён и, если состояние проекта известно, он и
    /// есть та версия, которую проект считает действующей.
    /// <para>
    /// Предупреждение «в игре стоит сборка со старым» имеет смысл только для такого файла. У
    /// выключенной или лишней старой версии имя всегда будет «старым», и совет пересобрать её
    /// ничего бы не изменил — новая сборка встаёт рядом, а не на её место.
    /// </para>
    /// </summary>
    private bool IsLoadedByGame =>
        Mod is { State: ModState.Enabled } mod
        && (_installedProject?.ActivePath is not { } active || SamePath(active, mod.FullPath));

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Исправный <c>src\meta.xml</c> проекта — только с ним есть что сравнивать.</summary>
    private ModProjectInfo? ReadableProject =>
        IsLoadedByGame && ProjectInfo is { HasMeta: true, MetaError: null } project ? project : null;

    /// <summary>
    /// Имя или описание в <c>src\meta.xml</c> уже не такие, как в архиве, который грузит игра.
    /// <para>
    /// Так бывает сразу после правки на этом экране: меняется файл ПРОЕКТА, а игра читает
    /// архив и увидит новое только после пересборки и установки. Строка списка честно
    /// показывает то, что стоит в игре, а карточка — что в проекте уже другое.
    /// </para>
    /// <para>
    /// Сравнивается, только когда обе стороны прочитаны: у проекта есть исправный meta.xml, у
    /// архива — тоже. Иначе «различие» было бы следствием нечитаемого файла, а не правки.
    /// </para>
    /// </summary>
    public bool ProjectMetaDiffers =>
        Mod is { MetaStatus: ModMetaStatus.Present } mod
        && ReadableProject is { } project
        && (NameDiffers(mod, project)
            || !string.Equals(Normalize(mod.Description), Normalize(project.Description), StringComparison.Ordinal));

    /// <summary>
    /// В игре стоит архив вовсе без meta.xml, а у проекта имя уже есть: игра покажет имя файла,
    /// пока мод не пересобран. Сравнивать тут нечего, но сказать об этом нужно — иначе правка
    /// имени выглядела бы не сработавшей.
    /// </summary>
    public bool ProjectMetaMissingInGame =>
        Mod is { MetaStatus: ModMetaStatus.Missing }
        && ReadableProject is { MetaName.Length: > 0 };

    /// <summary>Что сказать в карточке, когда проект и игра разошлись; иначе пустая строка.</summary>
    public string ProjectMetaNotice
    {
        get
        {
            if (ProjectMetaMissingInGame)
            {
                return "В игре стоит сборка без meta.xml — имя и описание из проекта появятся там после пересборки мода и установки.";
            }

            if (!ProjectMetaDiffers || ProjectInfo is not { } project || Mod is not { } mod)
            {
                return string.Empty;
            }

            bool nameDiffers = NameDiffers(mod, project);
            string what = nameDiffers
                ? $"В проекте мод уже называется «{project.DisplayName}»"
                : "В проекте описание уже изменено";
            return what + ", а в игре стоит сборка со старым. Пересоберите мод и поставьте из dist — тогда новое появится и здесь, и в игре.";
        }
    }

    /// <summary>Сырые <c>&lt;name&gt;</c> с обеих сторон — без подстановки имени файла или каталога.</summary>
    private static bool NameDiffers(InstalledMod mod, ModProjectInfo project) =>
        !string.Equals(Normalize(mod.MetaName), Normalize(project.MetaName), StringComparison.Ordinal);

    private static string Normalize(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    /// <summary>В каталоге есть ещё один файл с тем же id.</summary>
    public bool HasDuplicateId => Mod?.HasDuplicateId ?? false;

    /// <summary>Рядом лежит такой же файл в другом состоянии (<c>X.mtmod</c> и <c>X.mtmod.off</c>).</summary>
    public bool HasStateTwin => Mod?.HasStateTwin ?? false;

    /// <summary>Пометка про дубликаты — это классическая причина «правлю, а в игре не меняется».</summary>
    public string DuplicateText
    {
        get
        {
            if (HasDuplicateId)
            {
                return "В каталоге есть ещё один файл с тем же id. Игра увидит оба — вероятен конфликт файлов, "
                       + "а правки будут «не применяться».";
            }

            return HasStateTwin
                ? "Рядом лежит такой же файл в другом состоянии (.mtmod и .mtmod.off). "
                  + "Скорее всего, сборка модпака вернула мод, который вы выключали."
                : string.Empty;
        }
    }

    // =================================================================== свой проект

    /// <summary>Каталог проекта либо пустая строка.</summary>
    public string ProjectDir => Mod?.Mine?.ProjectDir ?? ProjectStatus?.Project.ProjectDir ?? string.Empty;

    /// <summary>Свежая сборка в <c>dist\</c> есть.</summary>
    public bool HasBuiltArtifact => BuiltArtifactPath.Length > 0;

    /// <summary>Путь к свежей сборке либо пустая строка.</summary>
    public string BuiltArtifactPath =>
        Mod?.Mine?.BuiltArtifactPath ?? ProjectStatus?.Project.BuiltArtifactPath ?? string.Empty;

    /// <summary>Версия свежей сборки либо пустая строка.</summary>
    public string BuiltVersion =>
        Mod?.Mine?.BuiltVersion ?? ProjectStatus?.Project.BuiltVersion ?? string.Empty;

    /// <summary>Сверка «что собрано» против «что стоит в игре».</summary>
    public string BuildMatchText
    {
        get
        {
            if (!IsMine)
            {
                return string.Empty;
            }

            if (!HasBuiltArtifact)
            {
                return "в dist\\ сборки нет — соберите проект на экране «Сборка и лог»";
            }

            if (!IsInstalled)
            {
                return "собрано " + BuiltVersion + " · в игре не установлен";
            }

            return Mod?.Mine?.Match switch
            {
                ModBuildMatch.SameBytes => "в игре стоит ровно та сборка, что лежит в dist\\ (" + BuiltVersion + ")",
                ModBuildMatch.DifferentBytes => "в dist\\: " + BuiltVersion + " · в игре: " + Version
                                                + " — файлы РАЗЛИЧАЮТСЯ, в игре другая сборка",
                ModBuildMatch.NoBuild => "в dist\\ сборки нет",
                _ => "сверить установленный файл со сборкой не удалось"
            };
        }
    }

    /// <summary>В игре лежит не то, что собрано: повод нажать «поставить из dist».</summary>
    public bool BuildDiffers => IsMine
                                && HasBuiltArtifact
                                && (!IsInstalled || Mod?.Mine?.Match == ModBuildMatch.DifferentBytes);

    /// <summary>Связь с проектом нашлась только по имени файла, а не по id из meta.xml.</summary>
    public bool MatchedByFileNameOnly => Mod?.Mine is { MatchedById: false };

    /// <summary>
    /// Что не так с самим проектом. Отдельная строка нужна ради реального случая
    /// только что начатого проекта: у него ещё нет <c>src\meta.xml</c>, поэтому у него нет
    /// ни id, ни имени архива, и связать его с установленным модом нечем.
    /// </summary>
    public string ProjectProblem
    {
        get
        {
            ModProjectInfo? p = ProjectStatus?.Project;
            if (p is null)
            {
                return string.Empty;
            }

            if (!p.HasMeta)
            {
                return "У проекта нет src\\meta.xml — без него у мода нет ни id, ни имени архива, "
                       + "и связать проект с установленным файлом нечем.";
            }

            return p.MetaError is { Length: > 0 } error ? "src\\meta.xml не читается: " + error : string.Empty;
        }
    }

    // =================================================================== иконка

    /// <summary>В архиве есть запись с иконкой — её имеет смысл читать.</summary>
    public bool HasIconEntry => Mod?.IconEntryPath is { Length: > 0 };

    /// <summary>
    /// Откуда брать иконку строки: из установленного в игре файла, а если её там нет — из
    /// СОБРАННОГО артефакта своего проекта. Пустая строка — читать нечего, останется плитка с буквой.
    /// <para>
    /// Запасной путь нужен ровно для своих модов, и вот почему. Иконка появляется в проекте
    /// раньше, чем в игре: сначала её кладут в <c>src\res\gui\maps\icons\mods\</c>, потом собирают,
    /// и только потом ставят. Между этими шагами свой мод выглядел бы в списке безымянной плиткой
    /// с буквой — притом что иконка уже нарисована и уже лежит в <c>dist\</c>. Чужих модов это не
    /// касается: у них никакого <c>dist\</c> нет, и их иконка всегда только из установленного файла.
    /// </para>
    /// <para>
    /// Подмены состояния здесь нет: строка отдельно и прямо говорит, что стоит в игре и совпадает
    /// ли оно со сборкой (<see cref="BuildMatchText"/>), а иконка — опознавательный знак проекта,
    /// а не признак установленного.
    /// </para>
    /// </summary>
    public string IconSourcePath
    {
        get
        {
            if (HasIconEntry)
            {
                return FullPath;
            }

            return IsMine ? BuiltArtifactPath : string.Empty;
        }
    }

    /// <summary>Есть откуда прочитать иконку.</summary>
    public bool HasIconSource => IconSourcePath.Length > 0;

    /// <summary>Иконка из архива мода либо <c>null</c>: тогда рисуется плитка с буквой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private ImageSource? _icon;

    /// <summary>Иконка нашлась и прочиталась.</summary>
    public bool HasIcon => Icon is not null;

    /// <summary>Буква для плитки-заглушки.</summary>
    public string PlateLetter => _placeholder.Letter;

    /// <summary>
    /// Цвет плитки-заглушки. Его вычисляет Core по устойчивому хешу, поэтому один и тот же
    /// мод всегда выглядит одинаково, а соседние строки различаются.
    /// </summary>
    public Brush PlateBrush { get; }

    /// <summary>Кладёт в строку иконку, прочитанную из архива.</summary>
    /// <param name="png">Байты PNG.</param>
    public void SetIcon(byte[] png)
    {
        if (png is null || png.Length == 0)
        {
            return;
        }

        try
        {
            using var ms = new MemoryStream(png);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            Icon = bmp;
        }
        catch (Exception)
        {
            // Битый PNG в чужом архиве — обычное дело. Останется плитка с буквой.
            Icon = null;
        }
    }

    // =================================================================== поиск и подсказка

    /// <summary>Подходит ли строка под запрос: имя, id, имя файла, подпапка.</summary>
    /// <param name="query">Подстрока.</param>
    /// <returns><c>true</c>, если подходит.</returns>
    public bool Matches(string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        return Contains(Title, query)
               || Contains(ModId, query)
               || Contains(FileName, query)
               || Contains(Group, query);
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Length > 0 && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Подсказка строки списка: полный путь и главное про мод.</summary>
    public string ToolTipText
    {
        get
        {
            if (!IsInstalled)
            {
                return Title + "\nпроект: " + ProjectDir + "\nв игре не установлен";
            }

            string text = FullPath + "\n" + StateText + " · " + SizeText;
            if (Description.Length > 0)
            {
                text += "\n\n" + Description;
            }

            return text;
        }
    }

    // =================================================================== мелочи

    private static ModIconPlaceholder SafePlaceholder(string? key, string? displayName)
    {
        try
        {
            return ModIcons.Placeholder(key, displayName);
        }
        catch (Exception)
        {
            return new ModIconPlaceholder("?", "#8A8A93");
        }
    }

    private static Brush MakeBrush(string colorHex)
    {
        Color color = Color.FromRgb(0x8A, 0x8A, 0x93);

        try
        {
            if (ColorConverter.ConvertFromString(colorHex) is Color parsed)
            {
                color = parsed;
            }
        }
        catch (Exception)
        {
            // Цвет — украшение; неразобранный оттенок не повод ронять список.
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
