using System.Text;

namespace WotModCockpit.Core.ApiCatalog;

/// <summary>Какой шаблон применён.</summary>
public enum SnippetKind
{
    /// <summary>Шаблон A: подписка на владельца, доступного всегда (резолверы R1, R2, R5).</summary>
    EventSubscription,

    /// <summary>
    /// Шаблон A2: двухступенчатая подписка на владельца, которого вне боя не существует
    /// (резолверы R3 и R4). Первая ступень — <c>g_playerEvents.onAvatarBecomePlayer</c>,
    /// вторая — уже сама подписка.
    /// </summary>
    LateEventSubscription,

    /// <summary>Шаблон B: кооперативный монкипатч метода клиента.</summary>
    MonkeyPatch,

    /// <summary>Шаблон C: получение сервиса через <c>helpers.dependency</c>.</summary>
    ServiceAccess,
}

/// <summary>Готовый сниппет для буфера обмена.</summary>
/// <param name="Kind">Применённый шаблон.</param>
/// <param name="Title">Заголовок для UI.</param>
/// <param name="Code">Исходный код на Python 2.7. Всегда заканчивается переводом строки.</param>
/// <param name="Warnings">Честные предупреждения: чего кокпит не знает и что пользователь должен доделать.</param>
public sealed record GeneratedSnippet(SnippetKind Kind, string Title, string Code, IReadOnlyList<string> Warnings);

/// <summary>
/// Генератор сниппетов на Python 2.7 — клиент «Мира танков» исполняет именно 2.7.
/// <para>
/// Каждый сниппет ОБЯЗАН проходить <c>tools/check_py2_syntax.py</c> из шаблона мода;
/// в тестах это проверяется запуском настоящего валидатора на реальных элементах каталога.
/// Подсунуть мододелу невалидный код хуже, чем не дать ничего.
/// </para>
/// <para>Жёсткие правила шаблонов (каждое куплено болью, см. комментарии в самом коде сниппетов):</para>
/// <list type="number">
///   <item>Подписка всегда парная: <c>subscribe()</c> в <c>init()</c>, <c>unsubscribe()</c> в <c>fini()</c>,
///         объект-владелец запоминается, чтобы отписка шла от ТОГО ЖЕ объекта.</item>
///   <item>Тело обработчика целиком в <c>try/except</c>: у <c>Event.Event</c> исключение из обработчика
///         всплывает в клиент (<c>common/Event.py</c> делает <c>LOG_CURRENT_EXCEPTION(); raise</c>).</item>
///   <item>Монкипатч берёт оригинал через <c>getattr</c> В МОМЕНТ УСТАНОВКИ, вызывает его ПЕРВЫМ
///         и возвращает его результат — так мод садится поверх XVM/pmod, а они поверх него.</item>
///   <item><c>fini()</c> монкипатча НЕ восстанавливает оригинал через <c>setattr</c>: если поверх нас
///         кто-то уже накрутил свою обёртку, восстановление молча снесёт её. Вместо этого флаг.</item>
///   <item>Обёртка объявляется как <c>def w(*args, **kwargs)</c> — годится и для метода, и для функции
///         модуля, и переживает смену сигнатуры в патче клиента.</item>
/// </list>
/// </summary>
public sealed class SnippetGenerator
{
    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "and", "as", "assert", "break", "class", "continue", "def", "del", "elif", "else", "except",
        "exec", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "not", "or",
        "pass", "print", "raise", "return", "try", "while", "with", "yield", "None", "True", "False",
        "self", "cls",
    };

    /// <summary>Сниппет подписки на событие. Шаблон A или A2 выбирается автоматически по резолверу.</summary>
    /// <param name="ev">Событие из каталога.</param>
    /// <param name="modId">Идентификатор мода, например <c>hello_tanker</c>.</param>
    /// <returns>Готовый сниппет.</returns>
    public GeneratedSnippet ForEvent(GameEvent ev, string modId)
    {
        ArgumentNullException.ThrowIfNull(ev);

        string mod = SanitizeModId(modId);
        var warnings = new List<string>();
        bool late = ev.RequiresBattle;

        (string[] args, bool star) = HandlerArgs(ev, warnings);
        string attr = ev.SubscriptionAttribute;
        if (ev.Privacy > 0)
        {
            warnings.Add($"Событие приватное: подписываться надо по mangled-имени {attr}, " +
                         "а не по тому, как оно написано в объявлении.");
        }

        if (ev.Resolution == OwnerResolution.Unresolved)
        {
            warnings.Add("Путь до владельца не найден: подставлен заглушечный _resolveOwner(). " +
                         "Владелец достижим только изнутри клиента — допишите доступ сами.");
        }

        if (ev.PropagatesHandlerExceptions)
        {
            warnings.Add("Событие объявлено как " + (ev.CtorKind == EventCtorKind.Event ? "Event.Event()" : "Event.SuspendedEvent()") +
                         ": исключение из обработчика ВСПЛЫВАЕТ в клиент. Тело обработчика обязано быть в try/except.");
        }

        if (ev.Resolution == OwnerResolution.Service)
        {
            warnings.Add("Сервис может быть ещё не создан в момент init(): dependency.isConfigured() " +
                         "станет истиной только после сборки контейнера. Если подписка не встала, " +
                         "позовите subscribe() повторно, например из onAccountShowGUI.");
        }

        var sb = new StringBuilder(2048);
        sb.Append("# -*- coding: utf-8 -*-\n");
        sb.Append("# WoT Mod Cockpit: подписка на событие ").Append(ev.Name).Append('\n');
        sb.Append("# Владелец: ").Append(ev.OwnerClass ?? "модуль").Append("  (")
          .Append(ev.VirtualPath ?? ev.RelPath).Append(':').Append(ev.Line).Append(")\n");
        sb.Append("# Доступ:   ").Append(ev.AccessPathDisplay).Append('\n');
        sb.Append("# Сигнатура: ").Append(ev.HasSignature
            ? attr + "(" + string.Join(", ", ev.Signature) + ")"
            : "не восстановлена по местам возбуждения, обработчик принимает *args").Append('\n');
        if (ev.PropagatesHandlerExceptions)
        {
            sb.Append("# ВНИМАНИЕ: у Event.Event исключение из обработчика уходит в клиент\n");
            sb.Append("#           (common/Event.py: LOG_CURRENT_EXCEPTION(); raise), поэтому\n");
            sb.Append("#           тело обработчика целиком завёрнуто в try/except.\n");
        }

        sb.Append('\n');
        sb.Append("import logging\n");
        AppendOwnerImports(sb, ev, late);
        sb.Append('\n');
        sb.Append("_logger = logging.getLogger('mod_").Append(mod).Append("')\n");
        sb.Append("_owner = [None]\n");
        sb.Append("\n\n");

        // --- обработчик ---------------------------------------------------------
        string handler = "_" + ev.Name;
        sb.Append("def ").Append(handler).Append('(').Append(star ? "*args, **kwargs" : string.Join(", ", args)).Append("):\n");
        sb.Append("    try:\n");
        if (star)
        {
            sb.Append("        _logger.info('").Append(mod).Append(": ").Append(ev.Name)
              .Append(" args=%s kwargs=%s', args, kwargs)\n");
        }
        else if (args.Length == 0)
        {
            sb.Append("        _logger.info('").Append(mod).Append(": ").Append(ev.Name).Append("')\n");
        }
        else
        {
            var fmt = new StringBuilder();
            foreach (string a in args)
                fmt.Append(' ').Append(a).Append("=%s");

            sb.Append("        _logger.info('").Append(mod).Append(": ").Append(ev.Name).Append(fmt).Append("',\n");
            sb.Append("                     ").Append(string.Join(", ", args)).Append(")\n");
        }

        sb.Append("    except Exception:\n");
        sb.Append("        _logger.exception('").Append(mod).Append(": обработчик ").Append(ev.Name).Append(" упал')\n");
        sb.Append("\n\n");

        AppendResolveOwner(sb, ev, mod);

        // --- подписка / отписка ---------------------------------------------------
        sb.Append("def subscribe():\n");
        sb.Append("    if _owner[0] is not None:\n");
        sb.Append("        return\n");
        sb.Append("    owner = _resolveOwner()\n");
        sb.Append("    if owner is None:\n");
        sb.Append("        _logger.warning('").Append(mod).Append(": владелец события ").Append(ev.Name)
          .Append(" пока недоступен')\n");
        sb.Append("        return\n");
        sb.Append("    try:\n");
        sb.Append("        owner.").Append(attr).Append(" += ").Append(handler).Append('\n');
        sb.Append("    except Exception:\n");
        sb.Append("        _logger.exception('").Append(mod).Append(": не удалось подписаться на ").Append(ev.Name).Append("')\n");
        sb.Append("        return\n");
        sb.Append("    _owner[0] = owner\n");
        sb.Append("\n\n");

        sb.Append("def unsubscribe():\n");
        sb.Append("    owner = _owner[0]\n");
        sb.Append("    if owner is None:\n");
        sb.Append("        return\n");
        sb.Append("    try:\n");
        sb.Append("        owner.").Append(attr).Append(" -= ").Append(handler).Append('\n');
        sb.Append("    except Exception:\n");
        sb.Append("        _logger.exception('").Append(mod).Append(": не удалось отписаться от ").Append(ev.Name).Append("')\n");
        sb.Append("    _owner[0] = None\n");
        sb.Append("\n\n");

        if (late)
        {
            // Шаблон A2. Владельца вне боя не существует, поэтому подписка двухступенчатая.
            sb.Append("# Владельца вне боя не существует, поэтому подписка двухступенчатая:\n");
            sb.Append("# сначала ловим появление аватара, и только потом подписываемся.\n");
            sb.Append("def init():\n");
            sb.Append("    g_playerEvents.onAvatarBecomePlayer += subscribe\n");
            sb.Append("    g_playerEvents.onAvatarBecomeNonPlayer += unsubscribe\n");
            sb.Append("\n\n");
            sb.Append("def fini():\n");
            sb.Append("    g_playerEvents.onAvatarBecomePlayer -= subscribe\n");
            sb.Append("    g_playerEvents.onAvatarBecomeNonPlayer -= unsubscribe\n");
            sb.Append("    unsubscribe()\n");
        }
        else
        {
            sb.Append("def init():\n");
            sb.Append("    subscribe()\n");
            if (ev.Resolution == OwnerResolution.Service)
            {
                sb.Append("    # Если в момент init() контейнер DI ещё не собран, _resolveOwner()\n");
                sb.Append("    # вернёт None и подписки не будет. Тогда позовите subscribe() ещё раз\n");
                sb.Append("    # позже — например из обработчика onAccountShowGUI, который мод\n");
                sb.Append("    # получает от загрузчика штатно.\n");
            }

            sb.Append("\n\n");
            sb.Append("def fini():\n");
            sb.Append("    unsubscribe()\n");
        }

        return new GeneratedSnippet(
            late ? SnippetKind.LateEventSubscription : SnippetKind.EventSubscription,
            "Подписка на " + ev.Name + (late ? " (двухступенчатая, только в бою)" : string.Empty),
            sb.ToString(),
            warnings);
    }

    /// <summary>Сниппет кооперативного монкипатча горячей точки.</summary>
    /// <param name="hook">Горячая точка из каталога.</param>
    /// <param name="modId">Идентификатор мода.</param>
    /// <returns>Готовый сниппет.</returns>
    public GeneratedSnippet ForHook(HookPoint hook, string modId)
    {
        ArgumentNullException.ThrowIfNull(hook);

        string mod = SanitizeModId(modId);
        var warnings = new List<string>();

        if (hook.Status == HookStatus.Missing)
        {
            warnings.Add($"{hook.Holder}.{hook.Member} НЕ НАЙДЕН в исходниках текущей версии клиента. " +
                         "Патч, скорее всего, мёртв: проверьте имя перед использованием.");
        }
        else if (hook.Status == HookStatus.Unknown)
        {
            warnings.Add("Точка не проверена: дерево исходников клиента не указано.");
        }
        else if (hook.Status == HookStatus.Inherited)
        {
            warnings.Add($"Метод объявлен не в {hook.Holder}, а в базовом классе {hook.DeclaringClass}. " +
                         $"Патчить надо {hook.Holder} (как это делают моды), иначе изменится поведение всех наследников.");
        }

        string holderModule = hook.HolderModuleName ?? hook.ModuleName ?? string.Empty;
        bool moduleLevel = hook.Status == HookStatus.ModuleLevel;
        if (hook.HolderModuleAmbiguous)
        {
            warnings.Add($"Имя класса {hook.Holder} встречается в нескольких модулях. " +
                         $"Кокпит взял {holderModule}; проверьте, что это тот самый.");
        }

        if (holderModule.Length == 0)
        {
            holderModule = "МОДУЛЬ_НЕИЗВЕСТЕН";
            warnings.Add("Модуль держателя не определён — допишите строку импорта вручную.");
        }

        var sb = new StringBuilder(2560);
        sb.Append("# -*- coding: utf-8 -*-\n");
        sb.Append("# WoT Mod Cockpit: кооперативный монкипатч ").Append(hook.Holder).Append('.').Append(hook.Member).Append('\n');
        if (hook.IsUsable)
        {
            sb.Append("# Объявлен: ").Append(hook.ModuleName).Append('.').Append(hook.DeclaredName)
              .Append(':').Append(hook.Line).Append('(').Append(hook.Signature).Append(")\n");
            sb.Append("# Файл:     ").Append(hook.VirtualPath ?? hook.RelPath ?? string.Empty).Append('\n');
        }
        else
        {
            sb.Append("# ВНИМАНИЕ: в исходниках текущей версии клиента этот член не найден.\n");
        }

        if (hook.ModCount > 0)
            sb.Append("# Это же место патчат: ").Append(string.Join(", ", hook.Mods)).Append('\n');

        sb.Append('\n');
        sb.Append("import logging\n");
        sb.Append("import functools\n");
        if (moduleLevel)
            sb.Append("import ").Append(holderModule).Append('\n');
        else
            sb.Append("from ").Append(holderModule).Append(" import ").Append(hook.Holder).Append('\n');

        string holderExpr = moduleLevel ? holderModule : hook.Holder;

        sb.Append('\n');
        sb.Append("_logger = logging.getLogger('mod_").Append(mod).Append("')\n");
        sb.Append('\n');
        sb.Append("# Реестр на уровне модуля делает патч идемпотентным: повторный импорт или\n");
        sb.Append("# перезагрузка скриптов BigWorld не завернут нашу же обёртку второй раз.\n");
        sb.Append("_PATCHED = {}\n");
        sb.Append("_ENABLED = [True]\n");
        sb.Append("\n\n");

        sb.Append("def _install(holder, methodName, factory):\n");
        sb.Append("    key = (getattr(holder, '__name__', repr(holder)), methodName)\n");
        sb.Append("    if key in _PATCHED:\n");
        sb.Append("        return False\n");
        sb.Append("    # Оригинал берётся В МОМЕНТ УСТАНОВКИ: так мы садимся поверх XVM/pmod,\n");
        sb.Append("    # а мод, загруженный после нас, сядет поверх нас и ничего не заметит.\n");
        sb.Append("    original = getattr(holder, methodName, None)\n");
        sb.Append("    if original is None:\n");
        sb.Append("        _logger.error('").Append(mod).Append(": %s.%s не найден - сменилась версия клиента?',\n");
        sb.Append("                      key[0], methodName)\n");
        sb.Append("        return False\n");
        sb.Append("    wrapper = factory(original)\n");
        sb.Append("    try:\n");
        sb.Append("        wrapper = functools.wraps(original)(wrapper)\n");
        sb.Append("    except Exception:\n");
        sb.Append("        pass\n");
        sb.Append("    setattr(holder, methodName, wrapper)\n");
        sb.Append("    _PATCHED[key] = original\n");
        sb.Append("    _logger.info('").Append(mod).Append(": пропатчен %s.%s', key[0], methodName)\n");
        sb.Append("    return True\n");
        sb.Append("\n\n");

        string factoryName = "_make_" + SanitizeIdentifier(hook.Member, "patched");
        sb.Append("def ").Append(factoryName).Append("(original):\n");
        sb.Append('\n');
        sb.Append("    # Только *args: код одинаково годится и для метода класса, и для функции\n");
        sb.Append("    # модуля, и переживает смену сигнатуры в очередном патче клиента.\n");
        sb.Append("    def wrapper(*args, **kwargs):\n");
        sb.Append("        # 1. Оригинал вызывается ПЕРВЫМ, и наружу отдаётся именно его результат.\n");
        sb.Append("        result = original(*args, **kwargs)\n");
        sb.Append("        # 2. Своя работа изолирована: исключение отсюда не имеет права попасть\n");
        sb.Append("        #    в стек клиента, иначе вместе с нами умрёт весь боевой интерфейс.\n");
        sb.Append("        if _ENABLED[0]:\n");
        sb.Append("            try:\n");
        sb.Append("                _logger.info('").Append(mod).Append(": ").Append(hook.Holder).Append('.')
          .Append(hook.Member).Append(" args=%s kwargs=%s', args, kwargs)\n");
        sb.Append("            except Exception:\n");
        sb.Append("                _logger.exception('").Append(mod).Append(": хук упал')\n");
        sb.Append("        return result\n");
        sb.Append('\n');
        sb.Append("    return wrapper\n");
        sb.Append("\n\n");

        sb.Append("def init():\n");
        sb.Append("    _install(").Append(holderExpr).Append(", '").Append(hook.Member).Append("', ")
          .Append(factoryName).Append(")\n");
        sb.Append("\n\n");
        sb.Append("def fini():\n");
        sb.Append("    # НЕ возвращаем оригинал через setattr: поверх нас мог сесть другой мод,\n");
        sb.Append("    # и восстановление молча снесло бы его патч. Просто разоружаемся.\n");
        sb.Append("    _ENABLED[0] = False\n");

        return new GeneratedSnippet(
            SnippetKind.MonkeyPatch,
            "Монкипатч " + hook.Holder + "." + hook.Member,
            sb.ToString(),
            warnings);
    }

    /// <summary>Сниппет получения сервиса через <c>helpers.dependency</c> со всеми проверками готовности.</summary>
    /// <param name="service">Сервис из каталога.</param>
    /// <param name="modId">Идентификатор мода.</param>
    /// <param name="battleSlot">
    /// Необязательный слот боевой сессии: если задан, в сниппет добавляется доступ
    /// <c>sessionProvider.shared|dynamic.&lt;слот&gt;</c> с проверкой на <c>None</c>.
    /// </param>
    /// <returns>Готовый сниппет.</returns>
    public GeneratedSnippet ForService(GameService service, string modId, BattleControllerSlot? battleSlot = null)
    {
        ArgumentNullException.ThrowIfNull(service);

        string mod = SanitizeModId(modId);
        var warnings = new List<string>
        {
            "У получения сервиса три РАЗНЫХ режима отказа, и все три проверяются в сниппете: " +
            "DependencyError при !isConfigured, DependencyError 'Service is not created' при отсутствии регистрации, " +
            "и None у sessionProvider.shared/.dynamic вне боя.",
        };

        if (battleSlot is not null)
        {
            warnings.Add($"Вне боя sessionProvider.{battleSlot.LocatorKind}.{battleSlot.SlotName} равен None " +
                         "(_EmptyRepository.getController возвращает None), при том что сам сервис резолвится успешно.");
        }

        var sb = new StringBuilder(2048);
        sb.Append("# -*- coding: utf-8 -*-\n");
        sb.Append("# WoT Mod Cockpit: получение сервиса ").Append(service.Name).Append('\n');
        sb.Append("# Модуль:  ").Append(service.ModuleName).Append('\n');
        sb.Append("# Файл:    ").Append(service.VirtualPath ?? service.RelPath).Append(':').Append(service.Line).Append('\n');
        sb.Append("# Обращений в клиенте: ").Append(service.UsageCount)
          .Append(" (instance ").Append(service.InstanceCount)
          .Append(", descriptor ").Append(service.DescriptorCount).Append(")\n");
        sb.Append('\n');
        sb.Append("import logging\n");
        sb.Append("from helpers import dependency\n");
        sb.Append("from ").Append(service.ModuleName).Append(" import ").Append(service.Name).Append('\n');
        sb.Append('\n');
        sb.Append("_logger = logging.getLogger('mod_").Append(mod).Append("')\n");
        sb.Append("\n\n");

        sb.Append("def getService():\n");
        sb.Append("    \"\"\"Возвращает сервис или None. Никогда не бросает.\n");
        sb.Append('\n');
        sb.Append("    Три независимых режима отказа, и каждый нормален в свой момент жизни клиента:\n");
        sb.Append("      1. dependency.isConfigured() == False - слишком рано, контейнер ещё не собран;\n");
        sb.Append("      2. dependency.hasInstance() == False  - сервис не зарегистрирован в текущем\n");
        sb.Append("         контексте (многие живут только в ангаре либо только в бою);\n");
        sb.Append("      3. сервис есть, но его внутреннее состояние пусто - например sessionProvider\n");
        sb.Append("         существует в ангаре, а все sessionProvider.shared.* равны None до боя.\n");
        sb.Append("    \"\"\"\n");
        sb.Append("    try:\n");
        sb.Append("        if not dependency.isConfigured():\n");
        sb.Append("            return None\n");
        sb.Append("        if not dependency.hasInstance(").Append(service.Name).Append("):\n");
        sb.Append("            return None\n");
        sb.Append("        return dependency.instance(").Append(service.Name).Append(")\n");
        sb.Append("    except Exception:\n");
        sb.Append("        _logger.exception('").Append(mod).Append(": не удалось получить ").Append(service.Name).Append("')\n");
        sb.Append("        return None\n");
        sb.Append("\n\n");

        if (battleSlot is not null)
        {
            sb.Append("def getBattleController():\n");
            sb.Append("    \"\"\"sessionProvider.").Append(battleSlot.LocatorKind).Append('.').Append(battleSlot.SlotName)
              .Append(" вне боя равен None - проверять обязательно.\"\"\"\n");
            sb.Append("    provider = getService()\n");
            sb.Append("    if provider is None:\n");
            sb.Append("        return None\n");
            sb.Append("    locator = getattr(provider, '").Append(battleSlot.LocatorKind).Append("', None)\n");
            sb.Append("    if locator is None:\n");
            sb.Append("        return None\n");
            sb.Append("    return getattr(locator, '").Append(battleSlot.SlotName).Append("', None)\n");
            sb.Append("\n\n");
        }

        sb.Append("class _LazyHolder(object):\n");
        sb.Append("    \"\"\"Объявление на уровне класса. dependency.descriptor() ленив и резолвится\n");
        sb.Append("    при первом чтении, поэтому его безопасно писать во время импорта,\n");
        sb.Append("    в отличие от dependency.instance().\"\"\"\n");
        sb.Append('\n');
        sb.Append("    service = dependency.descriptor(").Append(service.Name).Append(")\n");

        return new GeneratedSnippet(
            SnippetKind.ServiceAccess,
            "Получение сервиса " + service.Name,
            sb.ToString(),
            warnings);
    }

    // ------------------------------------------------------------------ приватное

    private static void AppendOwnerImports(StringBuilder sb, GameEvent ev, bool late)
    {
        bool needPlayerEvents = late;

        switch (ev.Resolution)
        {
            case OwnerResolution.Singleton:
            case OwnerResolution.Nested:
                if (ev.ImportLine is { Length: > 0 })
                    sb.Append(ev.ImportLine).Append('\n');
                needPlayerEvents &= !(ev.ImportLine ?? string.Empty).Contains("import g_playerEvents", StringComparison.Ordinal);
                break;

            case OwnerResolution.Service:
            case OwnerResolution.BattleSlot:
                sb.Append("from helpers import dependency\n");
                if (ev.ServiceModule is { Length: > 0 } && ev.ServiceInterface is { Length: > 0 })
                    sb.Append("from ").Append(ev.ServiceModule).Append(" import ").Append(ev.ServiceInterface).Append('\n');
                break;

            case OwnerResolution.EntityAttribute:
                sb.Append("import BigWorld\n");
                break;
        }

        if (needPlayerEvents)
            sb.Append("from PlayerEvents import g_playerEvents\n");
    }

    private static void AppendResolveOwner(StringBuilder sb, GameEvent ev, string mod)
    {
        sb.Append("def _resolveOwner():\n");

        switch (ev.Resolution)
        {
            case OwnerResolution.Singleton:
            case OwnerResolution.Nested:
                sb.Append("    return ").Append(ev.OwnerExpression).Append('\n');
                break;

            case OwnerResolution.Service:
                sb.Append("    try:\n");
                sb.Append("        if not dependency.isConfigured():\n");
                sb.Append("            return None\n");
                sb.Append("        if not dependency.hasInstance(").Append(ev.ServiceInterface).Append("):\n");
                sb.Append("            return None\n");
                sb.Append("        return dependency.instance(").Append(ev.ServiceInterface).Append(")\n");
                sb.Append("    except Exception:\n");
                sb.Append("        _logger.exception('").Append(mod).Append(": не удалось получить ")
                  .Append(ev.ServiceInterface).Append("')\n");
                sb.Append("        return None\n");
                break;

            case OwnerResolution.BattleSlot:
                sb.Append("    # Вне боя sessionProvider.").Append(ev.LocatorKind).Append('.').Append(ev.SlotName)
                  .Append(" равен None:\n");
                sb.Append("    # _EmptyRepository.getController возвращает None, хотя сам сервис резолвится.\n");
                sb.Append("    try:\n");
                sb.Append("        if not dependency.isConfigured():\n");
                sb.Append("            return None\n");
                sb.Append("        if not dependency.hasInstance(IBattleSessionProvider):\n");
                sb.Append("            return None\n");
                sb.Append("        provider = dependency.instance(IBattleSessionProvider)\n");
                sb.Append("    except Exception:\n");
                sb.Append("        _logger.exception('").Append(mod).Append(": не удалось получить IBattleSessionProvider')\n");
                sb.Append("        return None\n");
                sb.Append("    locator = getattr(provider, '").Append(ev.LocatorKind).Append("', None)\n");
                sb.Append("    if locator is None:\n");
                sb.Append("        return None\n");
                sb.Append("    return getattr(locator, '").Append(ev.SlotName).Append("', None)\n");
                break;

            case OwnerResolution.EntityAttribute:
                sb.Append("    player = BigWorld.player()\n");
                sb.Append("    if player is None:\n");
                sb.Append("        return None\n");
                sb.Append("    return getattr(player, '").Append(EntityAttributeName(ev.OwnerExpression)).Append("', None)\n");
                break;

            default:
                sb.Append("    # Путь до владельца кокпит не нашёл: событие объявлено в ")
                  .Append(ev.OwnerClass ?? "модуле").Append(",\n");
                sb.Append("    # но публичной точки доступа к этому объекту в исходниках не видно.\n");
                sb.Append("    # Допишите доступ сами и верните объект-владелец.\n");
                sb.Append("    return None\n");
                break;
        }

        sb.Append("\n\n");
    }

    /// <summary><c>BigWorld.player().arena</c> → <c>arena</c>.</summary>
    private static string EntityAttributeName(string? expr)
    {
        if (string.IsNullOrEmpty(expr))
            return "arena";

        int dot = expr.LastIndexOf('.');
        string name = dot >= 0 ? expr[(dot + 1)..] : expr;
        return IsIdentifier(name) ? name : "arena";
    }

    /// <summary>
    /// Аргументы обработчика. Если хотя бы один аргумент места возбуждения не является простым
    /// идентификатором (а такое сплошь и рядом: <c>True</c>, <c>'EndDialog'</c>,
    /// <c>BattleResultsCache.convertToFullForm(...)</c>), обработчик объявляется через
    /// <c>*args, **kwargs</c>. Частично угаданная арность хуже честного <c>*args</c>:
    /// она даёт TypeError в бою.
    /// </summary>
    private static (string[] Args, bool Star) HandlerArgs(GameEvent ev, List<string> warnings)
    {
        if (!ev.HasSignature)
        {
            warnings.Add("Сигнатуру события восстановить не удалось: обработчик принимает *args, **kwargs. " +
                         "Уточните аргументы по месту возбуждения события.");
            return (Array.Empty<string>(), true);
        }

        // Места возбуждения могут расходиться в АРНОСТИ: SeqTaskQueue.onInited возбуждается
        // и как onInited(index), и как onInited(). Победивший вариант описывает только часть
        // мест, и обработчик с фиксированной арностью даёт TypeError на всех остальных —
        // а у Event.Event это исключение уходит в клиент. Считать «часто встречающийся»
        // вариант правильным нельзя: правильного тут просто нет.
        if (ev.SignatureCandidates.Count > 1)
        {
            var arities = new HashSet<int>();
            foreach (EventSignatureCandidate c in ev.SignatureCandidates) arities.Add(c.Args.Count);
            if (arities.Count > 1)
            {
                warnings.Add("Событие возбуждается с разным числом аргументов (" +
                             string.Join(", ", ev.SignatureCandidates
                                 .Select(c => $"{c.Count}× по {c.Args.Count} в {c.RelPath}:{c.Line}")) +
                             ") — обработчик объявлен через *args, **kwargs, чтобы не получить TypeError в бою.");
                return (Array.Empty<string>(), true);
            }
        }

        var res = new List<string>(ev.Signature.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string a in ev.Signature)
        {
            string t = a.Trim();
            if (!IsIdentifier(t) || PythonKeywords.Contains(t) || !seen.Add(t))
            {
                warnings.Add($"Аргумент '{t}' в месте возбуждения не является простым именем — " +
                             "обработчик объявлен через *args, **kwargs, чтобы не угадывать арность.");
                return (Array.Empty<string>(), true);
            }

            res.Add(t);
        }

        return (res.ToArray(), false);
    }

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || (!char.IsAsciiLetter(s[0]) && s[0] != '_'))
            return false;

        foreach (char c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    private static string SanitizeIdentifier(string s, string fallback)
    {
        if (s.Length == 0)
            return fallback;

        var sb = new StringBuilder(s.Length + 1);
        foreach (char c in s)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');

        if (char.IsAsciiDigit(sb[0]))
            sb.Insert(0, '_');

        return sb.ToString();
    }

    /// <summary>Идентификатор мода попадает и в имя логгера, и в текст сообщений — чистим агрессивно.</summary>
    private static string SanitizeModId(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return "my_mod";

        var sb = new StringBuilder(modId.Length);
        foreach (char c in modId)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else if (c == '.' || c == '-' || c == ' ')
                sb.Append('_');
        }

        string r = sb.ToString().Trim('_');
        if (r.Length == 0)
            return "my_mod";

        return char.IsAsciiDigit(r[0]) ? "mod_" + r : r;
    }
}
