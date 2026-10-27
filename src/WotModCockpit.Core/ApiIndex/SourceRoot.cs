namespace WotModCockpit.Core.ApiIndex;

/// <summary>
/// Подкорень дерева декомпилированных исходников. Дерево <c>wot-src</c> состоит ровно из трёх
/// каталогов; у клиента 1.44 это было 6396 + 56 + 1599 файлов плюс один файл в самом корне.
/// </summary>
public enum SourceRoot : byte
{
    /// <summary><c>client/</c> — код клиента, 6396 файлов и 78.7 % всех символов.</summary>
    Client = 0,

    /// <summary><c>client_common/</c> — 56 файлов, в том числе ключевой <c>ClientArena.py</c>.</summary>
    ClientCommon = 1,

    /// <summary><c>common/</c> — общий с сервером код, 1599 файлов.</summary>
    Common = 2,

    /// <summary>Файл вне трёх подкорней (например <c>copy_br.py</c> в корне дампа).</summary>
    Unknown = 3
}

/// <summary>
/// Слой корпуса. Без него поиск тонет: 36.5 % символов — это ресурсы и stdlib, к моду
/// отношения не имеющие, причём ВСЕ 148 ложных срабатываний лексического сканера лежат
/// в <see cref="Vendor"/>.
/// </summary>
public enum SourceTier : byte
{
    /// <summary>Игровой код: 5574 файла, 99 376 символов (63.4 %). Только он ищется по умолчанию.</summary>
    Game = 0,

    /// <summary>
    /// Сгенерированные таблицы ресурсов (<c>/gui/impl/gen/</c>, <c>/Scaleform/locale/</c>):
    /// 1311 файлов, 33 767 символов (21.5 %), 17.6 МБ. Почти чистый шум
    /// (в <c>strings.py</c> 17 652 символа вида <c>class _label(DynAccessor)</c>).
    /// </summary>
    Generated = 1,

    /// <summary>
    /// Python-stdlib и вендорные библиотеки (<c>/site-packages/</c>, <c>common/Lib/</c>):
    /// 1167 файлов, 23 567 символов (15 %).
    /// </summary>
    Vendor = 2
}

/// <summary>
/// Раскладка дерева исходников: путь плюс наличие трёх подкорней.
/// <para>
/// Существует ради пустого состояния: дерева может не быть вовсе (у другого пользователя),
/// и приложение обязано это пережить, показав внятное сообщение и предложив указать путь,
/// а не упасть.
/// </para>
/// </summary>
public sealed class SourceTreeLayout
{
    private SourceTreeLayout(string path, bool exists, bool client, bool clientCommon, bool common)
    {
        Path = path;
        Exists = exists;
        HasClient = client;
        HasClientCommon = clientCommon;
        HasCommon = common;
    }

    /// <summary>Абсолютный путь к дереву. Может быть пустой строкой (путь не настроен).</summary>
    public string Path { get; }

    /// <summary>Каталог существует и доступен.</summary>
    public bool Exists { get; }

    /// <summary>Есть подкаталог <c>client</c>.</summary>
    public bool HasClient { get; }

    /// <summary>Есть подкаталог <c>client_common</c>.</summary>
    public bool HasClientCommon { get; }

    /// <summary>Есть подкаталог <c>common</c>.</summary>
    public bool HasCommon { get; }

    /// <summary>Путь настроен (непустая строка).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Path);

    /// <summary>
    /// Похоже на дамп uncompyle6: каталог существует и содержит хотя бы <c>client</c> или <c>common</c>.
    /// Индексировать можно и «непохожий» каталог — это лишь подсказка для UI.
    /// </summary>
    public bool LooksLikeClientSource => Exists && (HasClient || HasCommon);

    /// <summary>Пустая раскладка «путь не задан».</summary>
    public static SourceTreeLayout NotConfigured { get; } = new(string.Empty, false, false, false, false);

    /// <summary>
    /// Смотрит на диск. Не бросает ни при пустом пути, ни при несуществующем диске,
    /// ни при пути с недопустимыми символами — любая беда превращается в <see cref="Exists"/> = false.
    /// </summary>
    /// <param name="path">Путь к дереву или <c>null</c>.</param>
    /// <returns>Раскладка. Никогда не <c>null</c>.</returns>
    public static SourceTreeLayout Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return NotConfigured;

        try
        {
            string full = System.IO.Path.GetFullPath(path);
            if (!Directory.Exists(full))
                return new SourceTreeLayout(full, false, false, false, false);

            return new SourceTreeLayout(
                full,
                true,
                Directory.Exists(System.IO.Path.Combine(full, "client")),
                Directory.Exists(System.IO.Path.Combine(full, "client_common")),
                Directory.Exists(System.IO.Path.Combine(full, "common")));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return new SourceTreeLayout(path, false, false, false, false);
        }
    }

    /// <summary>Человекочитаемое состояние для пустого экрана браузера API.</summary>
    /// <returns>Готовая к показу строка на русском.</returns>
    public string Describe()
    {
        if (!IsConfigured)
            return "Дерево исходников клиента не указано. Укажите папку с дампом uncompyle6 (внутри client\\, client_common\\, common\\).";
        if (!Exists)
            return $"Каталог не найден: {Path}";
        if (!LooksLikeClientSource)
            return $"В каталоге {Path} нет подпапок client\\ и common\\ — это не похоже на дамп исходников клиента.";
        return $"Дерево исходников: {Path}";
    }

    /// <summary>
    /// Классифицирует файл по подкорню. Путь — относительный, с ПРЯМЫМИ слэшами.
    /// </summary>
    /// <param name="relativePath">Например <c>client/gui/SystemMessages.py</c>.</param>
    /// <returns>Подкорень.</returns>
    public static SourceRoot ClassifyRoot(ReadOnlySpan<char> relativePath)
    {
        if (relativePath.StartsWith("client/", StringComparison.Ordinal)) return SourceRoot.Client;
        if (relativePath.StartsWith("client_common/", StringComparison.Ordinal)) return SourceRoot.ClientCommon;
        if (relativePath.StartsWith("common/", StringComparison.Ordinal)) return SourceRoot.Common;
        return SourceRoot.Unknown;
    }

    /// <summary>
    /// Классифицирует файл по слою. Порядок проверок важен: вендорный код проверяется первым,
    /// потому что именно он даёт все ложные срабатывания сканера.
    /// </summary>
    /// <param name="relativePath">Относительный путь с прямыми слэшами.</param>
    /// <returns>Слой.</returns>
    public static SourceTier ClassifyTier(ReadOnlySpan<char> relativePath)
    {
        if (relativePath.Contains("/site-packages/", StringComparison.Ordinal)
            || relativePath.StartsWith("common/Lib/", StringComparison.Ordinal))
            return SourceTier.Vendor;

        if (relativePath.Contains("/gui/impl/gen/", StringComparison.Ordinal)
            || relativePath.Contains("/Scaleform/locale/", StringComparison.Ordinal))
            return SourceTier.Generated;

        return SourceTier.Game;
    }
}
