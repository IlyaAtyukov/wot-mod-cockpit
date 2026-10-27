using System.Text.Json.Serialization;

namespace WotModCockpit.Core.Models;

/// <summary>
/// Проект мода на диске разработчика — то, что кокпит собирает, ставит и за чем следит в логе.
/// Физически это каталог по образцу шаблона мода <c>wot-mod-template</c>:
/// <code>
/// &lt;ProjectDir&gt;\
///     build.py                                  ← тулчейн, вызывается процессом (см. IBuildRunner)
///     wotmod.json                               ← ЭТОТ объект, дескриптор кокпита
///     tools\py27c.py, tools\check_py2_syntax.py
///     src\meta.xml                              ← манифест мода: id / version / name / description
///     src\res\scripts\client\gui\mods\mod_&lt;pkg&gt;.py        ← точка входа, компилируется в .pyc
///     src\res\scripts\client\gui\mods\&lt;pkg&gt;\main.py       ← тело мода, едет обычным .py
///     dist\&lt;ModId&gt;_&lt;Version&gt;.mtmod             ← результат сборки
/// </code>
/// Класс — изменяемый POCO (не record): его правит форма настроек проекта и сериализует
/// <see cref="Abstractions.IProjectStore"/> в <c>wotmod.json</c> через <c>System.Text.Json</c>.
/// Имена свойств в JSON — camelCase (см. <see cref="ProjectStoreDefaults.JsonOptions"/>).
/// </summary>
public sealed class ModProject
{
    /// <summary>
    /// Абсолютный путь к корню проекта — каталог, в котором лежит <c>build.py</c> и <c>wotmod.json</c>.
    /// В <c>wotmod.json</c> НЕ сериализуется (см. <see cref="JsonIgnoreAttribute"/>): его подставляет
    /// <see cref="Abstractions.IProjectStore.Load"/> из фактического пути загрузки, иначе перенос
    /// каталога ломал бы проект.
    /// </summary>
    [JsonIgnore]
    public string ProjectDir { get; set; } = string.Empty;

    /// <summary>
    /// Идентификатор мода — то же, что <c>&lt;id&gt;</c> в <c>src\meta.xml</c>, например <c>ru.local.hello_tanker</c>.
    /// Входит в имя артефакта <c>&lt;ModId&gt;_&lt;Version&gt;.mtmod</c>. Источник истины — meta.xml;
    /// значение здесь считается кэшем и при загрузке сверяется с meta.xml
    /// (расхождение отдаётся как <see cref="ProjectValidation"/>, а не как исключение).
    /// </summary>
    public string ModId { get; set; } = string.Empty;

    /// <summary>Версия мода из <c>&lt;version&gt;</c> в meta.xml, например <c>1.0.0</c>. Тоже кэш meta.xml.</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>Человекочитаемое имя из <c>&lt;name&gt;</c> в meta.xml, например <c>Hello Tanker</c>. Показывается в заголовке кокпита.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Описание из <c>&lt;description&gt;</c> в meta.xml. Может быть пустым.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Префикс, которым мод помечает СВОИ строки в <c>python.log</c> — например <c>[HelloTanker</c>
    /// (без закрывающей скобки, чтобы ловились и <c>[HelloTanker]</c>, и <c>[HelloTanker/loader]</c>).
    /// По нему <see cref="Abstractions.ILogParser"/> выставляет <see cref="LogEntry.IsMine"/>,
    /// а UI красит «свои» строки. Пустая строка = фильтр «моё» выключен, IsMine всегда false.
    /// </summary>
    public string LogTag { get; set; } = string.Empty;

    /// <summary>
    /// Имя модуля точки входа БЕЗ расширения, например <c>mod_hello_tanker</c>.
    /// Загрузчик клиента (<c>scripts/client/gui/mods/__init__.py</c>) видит только файлы <c>mod_*.pyc</c>,
    /// поэтому имя обязано начинаться с <c>mod_</c>. Отсюда получаются:
    /// реальный файл <c>src\res\scripts\client\gui\mods\&lt;EntryPointModule&gt;.py</c> и
    /// виртуальный путь в логе <c>scripts/client/gui/mods/&lt;EntryPointModule&gt;.py</c>.
    /// </summary>
    public string EntryPointModule { get; set; } = string.Empty;

    /// <summary>
    /// Имя python-пакета с телом мода, например <c>hello_tanker</c>
    /// (полное имя в игре — <c>gui.mods.hello_tanker</c>, каталог — <c>src\res\scripts\client\gui\mods\hello_tanker</c>).
    /// Пустая строка допустима: бывают моды из одной только точки входа.
    /// </summary>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>
    /// Путь к каталогу исходников относительно <see cref="ProjectDir"/>. По умолчанию <c>src</c>.
    /// Передаётся в build.py как <c>--src</c>.
    /// </summary>
    public string SrcDirRelative { get; set; } = "src";

    /// <summary>
    /// Путь к каталогу сборки относительно <see cref="ProjectDir"/>. По умолчанию <c>dist</c>.
    /// Передаётся в build.py как <c>--dist</c>.
    /// </summary>
    public string DistDirRelative { get; set; } = "dist";

    /// <summary>
    /// Имя файла сборочного скрипта относительно <see cref="ProjectDir"/>. По умолчанию <c>build.py</c>.
    /// Кокпит НИКОГДА не повторяет логику этого скрипта на C# — он его только запускает.
    /// </summary>
    public string BuildScriptRelative { get; set; } = "build.py";

    /// <summary>
    /// Команда запуска Python 3.x для build.py ИМЕННО ЭТОГО проекта: команда из PATH либо абсолютный
    /// путь к <c>python.exe</c>. Пусто (по умолчанию) и устаревший литерал <c>python</c> из старых
    /// <c>wotmod.json</c> означают «как в настройках кокпита, иначе найти самому» — см.
    /// <see cref="Services.PythonLocator.ResolveForBuild"/>. Фактическая команда сборки — в
    /// <see cref="EffectivePythonExe"/>.
    /// Python 2.7 здесь НЕ нужен — байткод 2.7 делает <c>tools/py27c.py</c> из-под 3.x.
    /// </summary>
    public string PythonExe { get; set; } = string.Empty;

    /// <summary>
    /// Корень установки игры, записанный в <c>wotmod.json</c> ЭТОГО проекта, например
    /// <c>C:\Games\Tanki</c>. Нужен, только когда в шапке кокпита игра не выбрана: каталог игры
    /// из настроек кокпита главнее всегда — именно его показывают проверка «установка опознана»
    /// и диалог подтверждения записи. Пустая строка (по умолчанию) — обычный случай. Сюда кокпит
    /// сам ничего не пишет: <c>wotmod.json</c> обычно лежит в git, и абсолютный путь одной машины
    /// у остальных был бы чужим. Фактический каталог сборки — в <see cref="EffectiveGameDir"/>.
    /// </summary>
    public string GameDir { get; set; } = string.Empty;

    /// <summary>
    /// Каталог игры, с которым проект реально собирается и ставится: каталог из настроек кокпита
    /// (шапки), а если там игра не выбрана — <see cref="GameDir"/> проекта. Заполняет тот, кто открыл
    /// проект (главный экран); в <c>wotmod.json</c> НЕ пишется. Пусто — использовать <see cref="GameDir"/>.
    /// </summary>
    [JsonIgnore]
    public string EffectiveGameDir { get; set; } = string.Empty;

    /// <summary>
    /// Python, которым реально запускается build.py: результат
    /// <see cref="Services.PythonLocator.ResolveForBuild"/> по проекту и настройкам кокпита.
    /// В <c>wotmod.json</c> НЕ пишется. Пусто — использовать <see cref="PythonExe"/>, а если пусто
    /// и оно — <c>python</c>.
    /// </summary>
    [JsonIgnore]
    public string EffectivePythonExe { get; set; } = string.Empty;

    /// <summary>
    /// Путь к конфигу мода в игре ОТНОСИТЕЛЬНО корня установки, например
    /// <c>mods\configs\hello_tanker\config.json</c>. Нужен, чтобы кокпит мог показать/открыть
    /// действующий конфиг. Пустая строка = у мода конфига нет.
    /// </summary>
    public string GameConfigRelative { get; set; } = string.Empty;

    /// <summary>
    /// Дополнительные флаги, которые всегда добавляются к вызову build.py
    /// (например <c>--no-syntax-check</c> или <c>--compile-all</c>). Могут быть пустыми.
    /// Флаги действия (<c>--install</c>/<c>--dev</c>/<c>--uninstall</c>/<c>--list</c>/<c>--clean</c>)
    /// сюда класть НЕЛЬЗЯ — их подставляет <see cref="BuildAction"/>.
    /// </summary>
    public List<string> ExtraBuildArgs { get; set; } = new();

    // ---- вычисляемые пути; в JSON не пишутся ----

    /// <summary>Абсолютный путь к каталогу исходников (<see cref="ProjectDir"/> + <see cref="SrcDirRelative"/>).</summary>
    [JsonIgnore]
    public string SrcDir => Path.Combine(ProjectDir, SrcDirRelative);

    /// <summary>Абсолютный путь к каталогу сборки (<see cref="ProjectDir"/> + <see cref="DistDirRelative"/>).</summary>
    [JsonIgnore]
    public string DistDir => Path.Combine(ProjectDir, DistDirRelative);

    /// <summary>Абсолютный путь к <c>build.py</c>.</summary>
    [JsonIgnore]
    public string BuildScriptPath => Path.Combine(ProjectDir, BuildScriptRelative);

    /// <summary>Абсолютный путь к <c>src\meta.xml</c> — манифесту мода (источник истины для Id/Version/Name).</summary>
    [JsonIgnore]
    public string MetaXmlPath => Path.Combine(SrcDir, "meta.xml");

    /// <summary>
    /// Ожидаемое имя артефакта сборки: <c>&lt;ModId&gt;_&lt;Version&gt;.mtmod</c>.
    /// Это ПРЕДСКАЗАНИЕ, а не факт: реальный путь надо брать из stdout build.py
    /// (строка <c>[build] OK   &lt;абсолютный путь&gt; (N bytes, ...)</c>), см. <see cref="BuildResult.ArtifactPath"/>.
    /// </summary>
    [JsonIgnore]
    public string ExpectedArtifactPath => Path.Combine(DistDir, $"{ModId}_{Version}.mtmod");

    /// <summary>
    /// Абсолютный путь к файлу-исходнику точки входа:
    /// <c>&lt;SrcDir&gt;\res\scripts\client\gui\mods\&lt;EntryPointModule&gt;.py</c>.
    /// </summary>
    [JsonIgnore]
    public string EntryPointSourcePath =>
        Path.Combine(SrcDir, "res", "scripts", "client", "gui", "mods", EntryPointModule + ".py");

    /// <summary>
    /// Виртуальный путь точки входа внутри ресурсов игры — ровно в таком виде он появляется в python.log:
    /// <c>scripts/client/gui/mods/&lt;EntryPointModule&gt;.py</c>. Разделители — ПРЯМЫЕ слэши.
    /// </summary>
    [JsonIgnore]
    public string EntryPointVirtualPath =>
        $"scripts/client/gui/mods/{EntryPointModule}.py";

    /// <summary>
    /// Виртуальный префикс каталога пакета мода: <c>scripts/client/gui/mods/&lt;PackageName&gt;/</c>
    /// либо пустая строка, если <see cref="PackageName"/> не задан. Разделители — ПРЯМЫЕ слэши.
    /// По нему <see cref="Abstractions.ITracebackMapper"/> отличает «мой» кадр от кадра клиента.
    /// </summary>
    [JsonIgnore]
    public string PackageVirtualPrefix =>
        string.IsNullOrEmpty(PackageName) ? string.Empty : $"scripts/client/gui/mods/{PackageName}/";
}
