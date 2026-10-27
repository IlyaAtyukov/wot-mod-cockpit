namespace WotModCockpit.Core.Models;

/// <summary>
/// Глобальные настройки приложения (не проекта). Живут в
/// <c>%APPDATA%\WotModCockpit\settings.json</c> (каталог можно переопределить переменной
/// окружения <c>WOTCOCKPIT_SETTINGS_DIR</c>, см. <see cref="Services.JsonSettingsStore"/>),
/// формат — тот же camelCase-JSON, что и у <c>wotmod.json</c> (<see cref="ProjectStoreDefaults.JsonOptions"/>).
/// Изменяемый POCO: правится формой настроек.
/// </summary>
public sealed class CockpitSettings
{
    /// <summary>
    /// Идентификаторы новых модов по умолчанию начинаются с этого префикса:
    /// <c>ru.local.&lt;пакет&gt;</c>. Значение по умолчанию поля <see cref="ModIdPrefix"/>.
    /// </summary>
    public const string DefaultModIdPrefix = "ru.local";

    /// <summary>
    /// Корень установки игры, например <c>C:\Games\Tanki</c>. Каталог считается ТОЛЬКО ДЛЯ ЧТЕНИЯ:
    /// кокпит не пишет туда сам, запись делает исключительно build.py по явной команде пользователя.
    /// <para>По умолчанию пусто: на первом запуске каталог ищется по реестру и типовым папкам
    /// (<see cref="Services.GameDirectoryFinder"/>), а если не нашёлся — остаётся пустым, и шапка
    /// предлагает выбрать его вручную.</para>
    /// </summary>
    public string GameDir { get; set; } = string.Empty;

    /// <summary>
    /// Команда Python 3.x для build.py: <c>python</c>/<c>py</c> из PATH либо абсолютный путь
    /// к <c>python.exe</c>. Пусто — найти автоматически (PATH, лаунчер <c>py -0p</c>, реестр),
    /// см. <see cref="Services.PythonLocator.ResolveForBuild"/>.
    /// </summary>
    public string PythonExe { get; set; } = string.Empty;

    /// <summary>
    /// Каталог шаблона мода (<c>wot-mod-template</c>: там лежат <c>build.py</c> и <c>src\meta.xml</c>),
    /// из которого мастер «Новый мод…» создаёт проекты. Пусто — искать самому: переменная
    /// окружения <c>WOTCOCKPIT_TEMPLATE_DIR</c>, затем соседние с кокпитом каталоги
    /// <c>wot-mod-template*</c> (см. <see cref="Scaffold.TemplateLocator"/>). Выбор шаблона в мастере
    /// запоминается сюда.
    /// </summary>
    public string TemplateDir { get; set; } = string.Empty;

    /// <summary>
    /// Префикс идентификатора, который мастер «Новый мод…» предлагает для нового мода:
    /// <c>&lt;префикс&gt;.&lt;пакет&gt;</c>. По умолчанию <see cref="DefaultModIdPrefix"/>
    /// (<c>ru.local</c>). Для модов, которые будут публиковаться, лучше взять свой обратный
    /// домен, например <c>io.github.&lt;ник&gt;</c>: тогда идентификаторы разных авторов не пересекутся.
    /// </summary>
    public string ModIdPrefix { get; set; } = DefaultModIdPrefix;

    /// <summary>
    /// Путь к исполняемому файлу VS Code для <see cref="Abstractions.IEditorLauncher"/>,
    /// например <c>&lt;папка VS Code&gt;\bin\code.cmd</c> или <c>&lt;папка VS Code&gt;\Code.exe</c>.
    /// Пустая строка = искать самому: переменная <c>VSCODE_EXE</c>, PATH, типовые папки установки.
    /// </summary>
    public string EditorExe { get; set; } = string.Empty;

    /// <summary>
    /// Шаблон аргументов редактора. Подстановки: <c>{file}</c> — абсолютный путь, <c>{line}</c> — номер строки.
    /// Для VS Code рабочий вариант: <c>--goto "{file}:{line}"</c>.
    /// </summary>
    public string EditorArgsTemplate { get; set; } = "--goto \"{file}:{line}\"";

    /// <summary>
    /// Каталог с декомпилированными исходниками клиента для навигации по трейсбекам, например
    /// <c>&lt;шаблон мода&gt;\.scratch\wot-src</c>.
    /// ВНИМАНИЕ на раскладку: префикс <c>scripts/</c> из виртуального пути в этом каталоге ОТСУТСТВУЕТ,
    /// внутри лежат сразу <c>client\</c>, <c>client_common\</c>, <c>common\</c>. То есть
    /// <c>scripts/common/items/vehicles.py</c> → <c>&lt;WotSrcDir&gt;\common\items\vehicles.py</c>.
    /// </summary>
    public string WotSrcDir { get; set; } = string.Empty;

    /// <summary>
    /// Путь к <c>pyright</c> для разовой проверки типов. Пусто — искать самому:
    /// <c>node_modules</c> рядом с кокпитом, <c>pyright.exe</c>/<c>pyright.cmd</c> в PATH,
    /// затем <c>%APPDATA%\npm</c>.
    ///
    /// <para>Поле существует именно потому, что автопоиск покрывает не всё. Годится и
    /// <c>…\node_modules\pyright\index.js</c> (тогда его запускает Node), и обёртка
    /// <c>pyright.cmd</c>/<c>pyright.exe</c> от npm -g, yarn, pnpm или pip — установку
    /// с нестандартным префиксом иначе было бы нечем указать, и фича превращалась
    /// в тупик «pyright не найден».</para>
    /// </summary>
    public string PyrightPath { get; set; } = string.Empty;

    /// <summary>
    /// Путь к <c>node.exe</c>. Пусто — брать <c>node</c> из PATH. Нужен только тогда, когда
    /// <see cref="PyrightPath"/> указывает на <c>index.js</c>, а Node.js стоит не в PATH.
    /// </summary>
    public string NodePath { get; set; } = string.Empty;

    /// <summary>Путь к каталогу последнего открытого проекта. Пусто при первом запуске.</summary>
    public string LastProjectDir { get; set; } = string.Empty;

    /// <summary>Недавние проекты, самый свежий первым. Ограничивается 10 элементами при сохранении.</summary>
    public List<string> RecentProjects { get; set; } = new();

    /// <summary>
    /// Сколько последних килобайт python.log читать при старте слежения, прежде чем перейти
    /// в режим хвоста. По умолчанию 512 — ровно тот размер, до которого движок усекает файл,
    /// то есть «весь лог текущего запуска игры». При 64 КБ было видно последние ~10 % файла,
    /// и сценарий «игра уже упала, ищу свою строку» упирался в потолок молча.
    /// Прочитать файл целиком независимо от этого значения умеет кнопка «дочитать весь файл».
    /// </summary>
    public int LogPrimeTailKilobytes { get; set; } = 512;

    /// <summary>Сколько записей лога держать в памяти UI. Старые вытесняются.</summary>
    public int LogBufferCapacity { get; set; } = 20_000;

    /// <summary>Показывать в логе только записи мода (<see cref="LogEntry.IsMine"/>).</summary>
    public bool LogOnlyMine { get; set; }

    /// <summary>Пересобирать проект автоматически при изменении файлов в <c>src\</c>. В игру при этом НИЧЕГО не пишется.</summary>
    public bool AutoBuildOnSave { get; set; }

    /// <summary>Последний выбор в диалоге «Собрать сетап» на экране «Моды».</summary>
    public SetupBundleSettings SetupBundle { get; set; } = new();
}
