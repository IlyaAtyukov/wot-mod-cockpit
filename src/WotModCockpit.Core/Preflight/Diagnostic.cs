namespace WotModCockpit.Core.Preflight;

/// <summary>
/// Насколько серьёзна находка preflight. Шкала выбрана не «по ощущениям», а по тому,
/// ЧТО СЛУЧИТСЯ У ИГРОКА, если находку не чинить.
/// </summary>
public enum PreflightSeverity
{
    /// <summary>
    /// Справка: мод исправен, но разработчику стоит знать факт про клиент
    /// (например <c>LOG_DEBUG</c> на релизе — заглушка). Никогда не мешает сборке.
    /// </summary>
    Info = 0,

    /// <summary>
    /// Мод останется рабочим, но что-то сломается вероятностно: утечка подписки,
    /// незакрытая петля колбэков, исключение из обработчика, обрывающее ДРУГИЕ моды.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// Клиент закроется, мод не загрузится или не будет найден движком.
    /// Это то, ради чего preflight существует.
    /// </summary>
    Error = 2
}

/// <summary>
/// Одна находка preflight-анализа — аналог записи Roslyn-анализатора для мода
/// «Мира танков».
///
/// <para><b>Главное требование к тексту.</b> <see cref="Message"/> обязан объяснять
/// ПОСЛЕДСТВИЕ, а не факт: не «нет try/except», а «исключение отсюда всплывёт
/// в <c>game.py:init()</c>, где стоит <c>BigWorld.quit()</c> — клиент закроется».
/// Мододел не обязан помнить внутренности загрузчика; диагностика обязана их напомнить.</para>
/// </summary>
/// <param name="RuleId">Идентификатор правила: <c>WOT001</c>…<c>WOT021</c>.</param>
/// <param name="Severity">Серьёзность.</param>
/// <param name="FilePath">Абсолютный путь к файлу мода. Для находок уровня проекта — путь к корню проекта.</param>
/// <param name="Line">Строка, 1-based. 0 — находка не привязана к строке (уровень файла или проекта).</param>
/// <param name="Column">Колонка, 1-based. 0 — не привязана к колонке.</param>
/// <param name="Message">Что произойдёт. Одно-два предложения, с последствием.</param>
public sealed record PreflightDiagnostic(
    string RuleId,
    PreflightSeverity Severity,
    string FilePath,
    int Line,
    int Column,
    string Message)
{
    /// <summary>Как чинить — конкретное действие, а не «исправьте ошибку».</summary>
    public string? Hint { get; init; }

    /// <summary>
    /// Ссылка на место в декомпилированном клиенте, из которого следует правило
    /// (<c>scripts/client/game.py:127</c>). Нужна, чтобы утверждение можно было проверить.
    /// </summary>
    public string? Reference { get; init; }

    /// <summary>Кусок исходной строки мода, на который указывает находка (для показа в списке).</summary>
    public string? Snippet { get; init; }

    /// <summary>
    /// Путь файла ОТНОСИТЕЛЬНО корня проекта, с прямыми слэшами
    /// (<c>src/res/scripts/client/gui/mods/mod_hello_tanker.py</c>). Пусто для находок уровня проекта.
    /// </summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>Находка привязана к конкретной строке файла (а не к файлу или проекту целиком).</summary>
    public bool HasLocation => Line > 0;

    /// <summary>Строка «файл:строка» для лога и для перехода в редактор.</summary>
    public string Location => Line > 0
        ? $"{(RelativePath.Length > 0 ? RelativePath : FilePath)}:{Line}"
        : RelativePath.Length > 0 ? RelativePath : FilePath;

    /// <summary>Однострочное представление для лога и тестов.</summary>
    public override string ToString() => $"{RuleId} {Severity} {Location} {Message}";
}

/// <summary>Итог одного прогона preflight по проекту мода.</summary>
/// <param name="Diagnostics">Находки, отсортированные по серьёзности и месту.</param>
/// <param name="FilesAnalyzed">Сколько файлов мода разобрано.</param>
/// <param name="LinesAnalyzed">Сколько строк разобрано.</param>
/// <param name="Elapsed">Сколько занял анализ.</param>
/// <param name="RulesRun">Идентификаторы правил, которые реально отработали.</param>
public sealed record PreflightResult(
    IReadOnlyList<PreflightDiagnostic> Diagnostics,
    int FilesAnalyzed,
    int LinesAnalyzed,
    TimeSpan Elapsed,
    IReadOnlyList<string> RulesRun)
{
    /// <summary>Пустой результат: анализировать было нечего.</summary>
    public static PreflightResult Empty { get; } =
        new(Array.Empty<PreflightDiagnostic>(), 0, 0, TimeSpan.Zero, Array.Empty<string>());

    /// <summary>Сколько находок уровня <see cref="PreflightSeverity.Error"/>.</summary>
    public int ErrorCount => Count(PreflightSeverity.Error);

    /// <summary>Сколько находок уровня <see cref="PreflightSeverity.Warning"/>.</summary>
    public int WarningCount => Count(PreflightSeverity.Warning);

    /// <summary>Сколько находок уровня <see cref="PreflightSeverity.Info"/>.</summary>
    public int InfoCount => Count(PreflightSeverity.Info);

    /// <summary>Есть ли хоть одна ошибка — то есть стоит ли предупредить перед сборкой.</summary>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>Сколько находок указанной серьёзности.</summary>
    /// <param name="severity">Серьёзность.</param>
    /// <returns>Количество.</returns>
    public int Count(PreflightSeverity severity)
    {
        int c = 0;
        foreach (PreflightDiagnostic d in Diagnostics) if (d.Severity == severity) c++;
        return c;
    }

    /// <summary>Готовая к показу сводка: «2 ошибки, 7 предупреждений, 3 замечания за 18 мс».</summary>
    public string Summary =>
        Diagnostics.Count == 0
            ? $"preflight: замечаний нет ({FilesAnalyzed} файлов, {Elapsed.TotalMilliseconds:F0} мс)"
            : $"preflight: ошибок {ErrorCount}, предупреждений {WarningCount}, замечаний {InfoCount} " +
              $"({FilesAnalyzed} файлов, {Elapsed.TotalMilliseconds:F0} мс)";
}
