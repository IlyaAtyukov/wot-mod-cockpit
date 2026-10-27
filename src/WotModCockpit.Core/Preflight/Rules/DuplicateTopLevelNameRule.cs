namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT022. Имя <c>def</c>/<c>class</c> верхнего уровня объявлено в файле повторно.
///
/// <para><b>Последствие.</b> Модуль исполняется сверху вниз целиком, поэтому в рантайме
/// живёт ТОЛЬКО последнее объявление, а первое молча исчезает вместе со всем, что оно
/// делало. Синтаксической ошибки нет, <c>tools/check_py2_syntax.py</c> отвечает <c>[OK]</c>,
/// в <c>python.log</c> не появляется ни строки — мод просто наполовину не работает.</para>
///
/// <para><b>Откуда это берётся на практике.</b> Каталог рецептов кокпита вставляет код
/// в конец файла, и имена верхнего уровня у рецептов пересекаются: <c>subscribe</c>/
/// <c>unsubscribe</c> объявлены в R05, R06, R13 и R16; <c>register</c> — в R08, R14, R15;
/// <c>cancel_pending</c> — в R03 и R04; <c>_safe</c> — в R01 и R05. Два вставленных подряд
/// рецепта дают файл, где <c>subscribe()</c> первого рецепта не вызовется НИКОГДА.</para>
///
/// <para>Проверяются только <c>def</c> и <c>class</c>. Переприсваивание переменной верхнего
/// уровня (<c>_subscribed = False</c>) — обычное дело и ошибкой не является; ругаться на него
/// значило бы выдумывать дефект.</para>
/// </summary>
public sealed class DuplicateTopLevelNameRule : PerFileRule
{
    /// <inheritdoc/>
    public override string Id => "WOT022";

    /// <inheritdoc/>
    public override string Title => "Повторное объявление имени верхнего уровня";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        var first = new Dictionary<string, PythonDefinition>(StringComparer.Ordinal);

        foreach (PythonDefinition def in file.Model.Definitions)
        {
            // IsTopLevel = нулевой отступ и нет объемлющего def/class. Условные объявления
            // (`if IS_DEV: def f(): …`) сюда не попадают: у них отступ больше нуля.
            if (!def.IsTopLevel) continue;

            if (first.TryAdd(def.Name, def)) continue;

            PythonDefinition earlier = first[def.Name];

            // Декоратор-патчер (или регистратор) делает всю работу В МОМЕНТ ОБЪЯВЛЕНИЯ,
            // а само имя после этого никому не нужно. Так пишут реальные моды:
            // battlehits объявляет @override(...) def isPresent дважды подряд, а
            // responsive_reticle — @overrideIn(..., condition=isClientWG) def __rotate и
            // @overrideIn(..., condition=isClientLesta) def __rotate. Оба патча ставятся,
            // и ругаться здесь — значит соврать.
            if (InstallsOnDeclaration(context, earlier)) continue;

            string kind = def.IsClass ? "class" : "def";

            // Перекрытый обработчик загрузчика — это уже не «часть мода не работает»,
            // а «мод не работает»: init(), который затёрли, не вызовет никто.
            bool loaderCallback = (file.IsEntryPoint || file.InEntryImportChain)
                                  && !def.IsClass
                                  && EntryPointGuardRule.LoaderCallbacks.Contains(def.Name, StringComparer.Ordinal);

            output.Add(At(file, def.Line,
                loaderCallback ? PreflightSeverity.Error : PreflightSeverity.Warning,
                $"{kind} {def.Name} объявлен повторно: первое объявление стоит на строке {earlier.Line} " +
                "того же файла. Модуль исполняется сверху вниз, поэтому в рантайме останется ТОЛЬКО это, " +
                $"последнее объявление, а всё, что делало первое, не выполнится никогда." +
                (loaderCallback
                    ? $" А {def.Name}() — имя, которое дёргает загрузчик клиента: затёртая версия для мода потеряна целиком."
                    : string.Empty) +
                " Ни синтаксическая проверка, ни python.log про это не скажут ничего.",
                $"Переименуйте одно из объявлений (или объедините их). Если код пришёл из каталога рецептов, " +
                "два рецепта принесли одинаковые имена — префикс вида r05_ снимает конфликт.",
                "Python 2.7 model of execution: последнее присваивание имени в модуле побеждает"));
        }
    }

    /// <summary>
    /// Определение делает свою работу декоратором в момент объявления: патчер
    /// (<c>@overrideMethod</c>, <c>@overrideIn</c>) или регистратор обработчика
    /// (<c>@registerEvent</c>). Имя такого определения дальше не используется.
    /// </summary>
    private static bool InstallsOnDeclaration(PreflightContext context, PythonDefinition def)
    {
        foreach (string decorator in def.Decorators)
        {
            string name = decorator;
            int dot = name.LastIndexOf('.');
            if (dot >= 0) name = name[(dot + 1)..];

            foreach (string patcher in context.Options.PatchDecorators)
                if (string.Equals(name, patcher, StringComparison.Ordinal)) return true;
            foreach (string wrapper in context.Options.SafeSubscribeWrappers)
                if (string.Equals(name, wrapper, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
