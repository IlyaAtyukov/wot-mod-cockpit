namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Маленькая курируемая карта «что печатает мододел» → «как это называется в клиенте».
///
/// <para><b>Зачем она вообще нужна.</b> Каталог событий строится ИЗ ИСХОДНИКОВ и содержит
/// 1030 записей — это правильно и это не обсуждается. Но фильтр по подстроке отвечает только
/// тому, кто уже знает нужное слово, а весь смысл экрана в том, что человек его как раз НЕ знает.
/// Замерено на настоящем каталоге: запрос «battle» — самое естественное английское слово для
/// «бой» — даёт 231 запись, и среди них НЕТ ни одного правильного ответа
/// (<c>onAvatarBecomePlayer</c>, <c>onArenaPeriodChange</c>, <c>onAvatarReady</c>,
/// <c>onArenaCreated</c>): все они называются через Arena, а «battle» попадает в путь доступа
/// у совсем других записей. Русские «бой», «вошёл», «спавн» дают ноль.</para>
///
/// <para><b>Почему это не противоречит принципу «каталог строится из исходников».</b>
/// Карта не добавляет в каталог ни одной записи и ничего о клиенте не утверждает: она только
/// МАРШРУТИЗИРУЕТ запрос — превращает слово человека в слово клиента. Все показанные записи
/// по-прежнему приходят из дампа, а сам факт подмены виден в строке состояния
/// («по запросу «бой» искали также: arena, onAvatarBecomePlayer…»).</para>
///
/// <para>Карта намеренно маленькая. Это не словарь и не онтология: это два десятка слов,
/// которыми называют вещи в реальных вопросах мододелов.</para>
/// </summary>
public static class CatalogSynonyms
{
    /// <summary>Ключ ищется как подстрока запроса в нижнем регистре, значение — что подставить.</summary>
    private static readonly (string Key, string[] Terms)[] Map =
    [
        // Бой: в клиенте он называется Arena, и это ровно то знание, которого у новичка нет.
        ("бой", ["arena", "onAvatarBecomePlayer", "onArenaPeriodChange", "onAvatarReady"]),
        ("battle", ["arena", "onAvatarBecomePlayer", "onArenaPeriodChange", "onAvatarReady"]),
        ("арена", ["arena"]),
        ("вошёл", ["onAvatarBecomePlayer", "onAvatarReady", "onArenaPeriodChange"]),
        ("вошел", ["onAvatarBecomePlayer", "onAvatarReady", "onArenaPeriodChange"]),
        ("вход", ["onAvatarBecomePlayer", "onAvatarReady", "onAccountBecomePlayer"]),
        ("старт", ["onArenaPeriodChange", "onAvatarReady"]),
        ("начало", ["onArenaPeriodChange", "onAvatarReady"]),
        ("спавн", ["onAvatarReady", "onVehicleEnterWorld"]),
        ("spawn", ["onAvatarReady", "onVehicleEnterWorld"]),
        ("конец", ["onArenaPeriodChange", "onBattleResultsReceived"]),
        ("результат", ["onBattleResultsReceived", "onArenaPeriodChange"]),

        // Ангар и лобби.
        ("ангар", ["lobby", "hangar", "onAccountShowGUI"]),
        ("hangar", ["lobby", "hangar", "onAccountShowGUI"]),
        ("лобби", ["lobby"]),
        ("логин", ["onConnected", "onAccountBecomePlayer", "login"]),
        ("вход в игру", ["onConnected", "onAccountBecomePlayer"]),

        // Боевые события.
        ("выстрел", ["onShot", "onGunShot", "shoot"]),
        ("shot", ["onShot", "onGunShot"]),
        ("попадание", ["onVehicleFeedbackReceived", "hit"]),
        ("урон", ["onHealthChanged", "onVehicleFeedbackReceived", "damage"]),
        ("damage", ["onHealthChanged", "onVehicleFeedbackReceived"]),
        ("здоров", ["onHealthChanged"]),
        ("смерть", ["onVehicleKilled", "onHealthChanged"]),
        ("убил", ["onVehicleKilled"]),
        ("прицел", ["onCameraChanged", "onAimingChanged", "aim"]),
        ("камера", ["onCameraChanged", "camera"]),
        ("танк", ["vehicle"]),
        ("техника", ["vehicle"]),
        ("экипаж", ["crew", "tankman"]),
        ("чат", ["messenger", "onMessageReceived"]),
        ("сообщение", ["pushMessage", "SystemMessages", "onMessageReceived"]),
        ("настройк", ["settings", "onSettingsChanged"]),
        ("миникарта", ["minimap"]),
    ];

    /// <summary>
    /// Подбирает синонимы к запросу. Ничего не «угадывает»: срабатывает только точное
    /// вхождение ключа в запрос.
    /// </summary>
    /// <param name="query">Запрос пользователя.</param>
    /// <returns>Дополнительные слова для поиска; пусто — синонимов нет.</returns>
    public static IReadOnlyList<string> Expand(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        string q = query.Trim().ToLowerInvariant();
        List<string>? terms = null;

        foreach ((string key, string[] words) in Map)
        {
            if (!q.Contains(key, StringComparison.Ordinal))
                continue;

            foreach (string w in words)
            {
                // Слово, которое пользователь и так напечатал, синонимом не считается:
                // иначе строка состояния сообщала бы «искали также: arena» на запрос «arena».
                if (q.Contains(w, StringComparison.OrdinalIgnoreCase))
                    continue;

                terms ??= new List<string>(6);
                if (!terms.Contains(w, StringComparer.OrdinalIgnoreCase))
                    terms.Add(w);
            }
        }

        return (IReadOnlyList<string>?)terms ?? [];
    }
}
