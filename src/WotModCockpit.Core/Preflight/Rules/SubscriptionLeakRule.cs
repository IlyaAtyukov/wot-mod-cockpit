namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT009. Подписка <c>+=</c> на долгоживущий игровой синглтон без парной отписки <c>-=</c>.
///
/// <para><b>Что решает, баг это или нет.</b> <c>Event.Event(manager)</c> регистрируется
/// в <c>EventManager</c> и массово чистится через <c>EventManager.clear()</c> при разрушении
/// владельца, а <c>Event.Event()</c> БЕЗ менеджера не чистится никогда.
/// <c>PlayerEvents.py</c> объявляет все 80 своих событий как <c>Event.Event()</c> без менеджера —
/// они живут всю сессию клиента, и подписка без отписки утекает по-настоящему.
/// <c>ClientArena.py</c>, наоборот, создаёт события как <c>Event.Event(em)</c>: они умирают
/// вместе с ареной, и отписка там необязательна.</para>
///
/// <para>Поэтому правило смотрит на ВЛАДЕЛЬЦА: замер разведки — 10 срабатываний у наивной
/// версии, из них истинных 3; после ограничения по владельцу — 3 из 3.</para>
/// </summary>
public sealed class SubscriptionLeakRule : IPreflightRule
{
    /// <summary>Синглтоны, живущие всю сессию клиента. Подписка на них обязана иметь пару.</summary>
    public static IReadOnlyList<string> LongLivedOwners { get; } = new[]
    {
        "g_playerEvents", "g_eventBus", "g_currentVehicle", "g_prbLoader", "g_appLoader",
        "g_clanCache", "g_itemsCache", "g_guiResetters", "g_keyEventHandlers", "g_mouseEventHandlers",
        "g_replayCtrl", "g_settingsCore", "g_lobbyContext", "g_playerCtx", "ServicesLocator"
    };

    /// <inheritdoc/>
    public string Id => "WOT009";

    /// <inheritdoc/>
    public string Title => "Подписка на игровой синглтон без отписки";

    /// <inheritdoc/>
    public void Analyze(PreflightContext context, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        // Пара «+=» / «-=» ищется по ВСЕМУ дереву мода: отписку часто выносят в отдельный
        // файл (у XVM это owg_module_fini), и проверка в пределах одного файла врала бы.
        var subscribed = new Dictionary<string, (ModSourceFile File, int Line, string Owner)>(StringComparer.Ordinal);
        var unsubscribed = new HashSet<string>(StringComparer.Ordinal);

        // Владельцы, у которых мод снимает подписку ДИНАМИЧЕСКИ — через getattr по имени
        // из переменной. Какое именно событие снимается, статически не узнать, поэтому
        // по такому владельцу правило молчит: соврать здесь хуже, чем промолчать.
        var dynamicallyUnsubscribed = new HashSet<string>(StringComparer.Ordinal);

        foreach (ModSourceFile file in context.Files)
        {
            AliasMap aliases = BuildAliases(file);

            foreach (PythonStatement st in file.Model.Statements)
            {
                string code = st.Trimmed;

                int plus = code.IndexOf("+=", StringComparison.Ordinal);
                int minus = code.IndexOf("-=", StringComparison.Ordinal);
                bool isSub = plus > 0;
                int at = isSub ? plus : minus;
                if (at <= 0) continue;

                string left = code[..at].Trim();

                // Левая часть без точки — это локальный алиас события:
                // `event = getattr(g_playerEvents, 'onAvatarReady', None)` … `event -= handler`.
                // Без разбора алиасов «-=» вообще не регистрировалось, и собственный
                // рецепт каталога R05 получал от своего же preflight три ложных WOT009.
                if (!left.Contains('.', StringComparison.Ordinal))
                {
                    if (isSub) continue;
                    if (aliases.Events.TryGetValue(left, out List<string>? keys))
                        foreach (string k in keys) unsubscribed.Add(k);
                    if (aliases.DynamicOwners.TryGetValue(left, out List<string>? owners))
                        foreach (string o in owners) dynamicallyUnsubscribed.Add(o);
                    continue;
                }

                int dot = left.LastIndexOf('.');
                if (dot <= 0) continue;

                string owner = Resolve(aliases, left[..dot]);
                string evt = left[(dot + 1)..];
                if (!IsEventName(evt)) continue;

                string key = owner + "." + evt;
                if (isSub) subscribed.TryAdd(key, (file, st.Line, owner));
                else unsubscribed.Add(key);
            }
        }

        foreach ((string key, (ModSourceFile file, int line, string owner)) in subscribed)
        {
            if (unsubscribed.Contains(key)) continue;
            if (dynamicallyUnsubscribed.Contains(owner)) continue;
            if (!IsLongLived(context, owner)) continue;

            output.Add(new PreflightDiagnostic(Id, PreflightSeverity.Warning, file.FullPath, line, 0,
                $"Подписка {key} есть, а парного «-=» во всём дереве мода нет. Владелец {owner} живёт всю сессию " +
                "клиента (PlayerEvents объявляет свои события как Event.Event() БЕЗ менеджера, поэтому " +
                "EventManager.clear() их не снимет), так что обработчик останется в списке и после выгрузки мода " +
                "и будет вызываться на мёртвом состоянии.")
            {
                Hint = $"Снимите подписку в fini(): {key} -= <тот же объект-обработчик>. Отписываться надо ТЕМ ЖЕ " +
                       "объектом — заново созданные lambda/partial из списка не удалятся.",
                Reference = "scripts/common/Event.py (Event/SafeEvent), scripts/client/PlayerEvents.py",
                RelativePath = file.RelativePath,
                Snippet = file.Model.LineAt(line)?.Raw.Trim()
            });
        }
    }

    /// <summary>
    /// Алиасы файла: чем на самом деле является короткое имя слева от <c>-=</c>.
    /// </summary>
    /// <param name="Owners">
    /// <c>ev = g_playerEvents</c> — имя, за которым стоит другой объект-владелец.
    /// </param>
    /// <param name="Events">
    /// <c>e = g_playerEvents.onAvatarReady</c> или <c>e = getattr(g_playerEvents, 'onAvatarReady', None)</c>
    /// — имя, за которым стоит КОНКРЕТНОЕ событие. Значений может быть несколько
    /// (переменную переиспользуют в цикле), поэтому список.
    /// </param>
    /// <param name="DynamicOwners">
    /// <c>e = getattr(g_playerEvents, name, None)</c> — событие выбирается в рантайме.
    /// Здесь известен только владелец.
    /// </param>
    private sealed record AliasMap(
        Dictionary<string, string> Owners,
        Dictionary<string, List<string>> Events,
        Dictionary<string, List<string>> DynamicOwners);

    /// <summary>
    /// Собирает алиасы одного файла. Проход единственный и лексический: область видимости
    /// не моделируется намеренно — задача не «доказать отписку», а «не соврать, что её нет».
    /// </summary>
    private static AliasMap BuildAliases(ModSourceFile file)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var events = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var dynamic = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (PythonStatement st in file.Model.Statements)
        {
            string code = st.Trimmed;
            int eq = PreflightSyntax.IndexOfAssignment(code);
            if (eq <= 0) continue;

            string name = code[..eq].Trim();
            if (!IsEventName(name)) continue;                      // тот же критерий «простой идентификатор»
            string rhs = code[(eq + 1)..].Trim();

            int open = PreflightSyntax.FindCall(code, "getattr", allowMethodReceiver: false);
            if (open > eq)
            {
                IReadOnlyList<string> args = PreflightSyntax.SplitArguments(code, open);
                IReadOnlyList<string> raw = PreflightSyntax.SplitArgumentsRaw(code, st.RawCode, open);
                if (args.Count >= 2 && IsDotted(args[0]))
                {
                    string owner = args[0].Trim();
                    string literal = raw.Count >= 2 ? Unquote(raw[1]) : string.Empty;
                    if (literal.Length > 0 && IsEventName(literal)) Add(events, name, owner + "." + literal);
                    else Add(dynamic, name, owner);
                }
                continue;
            }

            if (!IsDotted(rhs)) continue;

            int dot = rhs.LastIndexOf('.');
            if (dot > 0 && IsEventName(rhs[(dot + 1)..])) Add(events, name, rhs);
            else owners.TryAdd(name, rhs);
        }

        return new AliasMap(owners, events, dynamic);
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out List<string>? list)) map[key] = list = [];
        if (!list.Contains(value, StringComparer.Ordinal)) list.Add(value);
    }

    /// <summary>Разворачивает алиас владельца: <c>ev.onX</c> → <c>g_playerEvents.onX</c>.</summary>
    private static string Resolve(AliasMap aliases, string owner) =>
        aliases.Owners.TryGetValue(owner, out string? real) ? real : owner;

    private static bool IsDotted(string s)
    {
        string t = s.Trim();
        if (t.Length == 0) return false;
        if (!char.IsLetter(t[0]) && t[0] != '_') return false;
        foreach (char c in t) if (!char.IsLetterOrDigit(c) && c != '_' && c != '.') return false;
        return true;
    }

    /// <summary>Содержимое строкового литерала <c>'onAvatarReady'</c> либо пустая строка.</summary>
    private static string Unquote(string s)
    {
        string t = s.Trim();
        if (t.Length < 2) return string.Empty;
        char q = t[0];
        if (q is not ('\'' or '"') || t[^1] != q) return string.Empty;
        return t[1..^1];
    }

    private static bool IsEventName(string name)
    {
        if (name.Length == 0) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        foreach (char c in name) if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    /// <summary>
    /// Владелец — долгоживущий объект клиента. <c>self</c>, локальные переменные и собственные
    /// шины событий мода (в корпусе это <c>g_eventsManager</c>, 21 подписка из 68) не в счёт.
    /// </summary>
    private static bool IsLongLived(PreflightContext context, string owner)
    {
        string root = owner.Split('.')[0];
        if (root is "self" or "cls") return false;
        if (LongLivedOwners.Contains(root, StringComparer.Ordinal)) return true;

        // Собственные объекты мода не проверяем: их время жизни автор контролирует сам.
        foreach (ModSourceFile f in context.Files)
            if (f.Model.FindDefinition(root) is not null) return false;

        return context.Api.IsClientSingleton(root);
    }
}
