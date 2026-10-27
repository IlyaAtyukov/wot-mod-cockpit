using System.Reflection;
using System.Text;

namespace WotModCockpit.Core.Recipes;

/// <summary>
/// Каталог рецептов. Метаданные — здесь, код — во встроенных ресурсах
/// <c>WotModCockpit.Core.Recipes.Recipes.rNN_*.py</c>.
///
/// <para>Почему код лежит отдельными <c>.py</c>, а не строками в C#: его надо гонять через
/// <c>tools/check_py2_syntax.py</c> (тест это и делает), подсвечивать как питон и править
/// без экранирования кавычек. Строковый литерал в C# ничего из этого не даёт.</para>
///
/// <para>Каждый рецепт проверен: (1) прогнан валидатором py2.7; (2) все перечисленные
/// в <see cref="Recipe.Requirements"/> символы найдены в дереве исходников клиента 1.44.</para>
/// </summary>
public static class RecipeLibrary
{
    private const string ResourcePrefix = "WotModCockpit.Core.Recipes.Recipes.";

    // Нативные модули движка: .py у них нет, в индексе клиента их нет и быть не может.
    private static readonly RecipeRequirement BigWorld = new("BigWorld", IsNative: true);
    private static readonly RecipeRequirement ResMgr = new("ResMgr", IsNative: true);

    private static readonly Lazy<IReadOnlyList<Recipe>> LazyAll = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Все рецепты в порядке возрастания сложности изучения.</summary>
    public static IReadOnlyList<Recipe> All => LazyAll.Value;

    /// <summary>Рецепт по идентификатору (регистр не важен).</summary>
    /// <param name="id">Идентификатор, например <c>R07</c>.</param>
    /// <returns>Рецепт либо <c>null</c>.</returns>
    public static Recipe? ById(string? id) =>
        id is null ? null : All.FirstOrDefault(r => string.Equals(r.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Читает встроенный ресурс с кодом рецепта.</summary>
    /// <param name="resourceName">Короткое имя файла, например <c>r01_skeleton.py</c>.</param>
    /// <returns>Текст в UTF-8 с нормализованными переводами строк.</returns>
    /// <exception cref="InvalidOperationException">Ресурса нет в сборке — это дефект сборки, а не среды.</exception>
    internal static string ReadResource(string resourceName)
    {
        Assembly asm = typeof(RecipeLibrary).Assembly;

        // Сначала каноничное имя, потом поиск по суффиксу. Второе — не перестраховка:
        // логическое имя ресурса собирается MSBuild'ом из RootNamespace и пути, и меняется
        // от одной только правки RootNamespace. Каталог рецептов не должен разваливаться
        // от переименования, о котором никто не вспомнит.
        Stream? s = asm.GetManifestResourceStream(ResourcePrefix + resourceName);

        if (s is null)
        {
            string suffix = "." + resourceName;
            string? match = Array.Find(
                asm.GetManifestResourceNames(),
                n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

            if (match is not null) s = asm.GetManifestResourceStream(match);
        }

        if (s is null)
        {
            throw new InvalidOperationException(
                $"Встроенный ресурс с кодом рецепта '{resourceName}' не найден. " +
                "Проверьте <EmbeddedResource Include=\"Recipes\\Recipes\\*.py\" /> в WotModCockpit.Core.csproj.");
        }

        using var reader = new StreamReader(s, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        // \r\n -> \n: код уходит в буфер обмена и в .py-файл мода, где важна
        // предсказуемость, а не то, какие концы строк были у ресурса в git.
        return reader.ReadToEnd().Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static IReadOnlyList<Recipe> Build() =>
    [
        new Recipe(
            "R01",
            "Скелет мода: init/fini и шесть событий загрузчика",
            RecipeCategory.Lifecycle,
            RecipeDifficulty.Beginner,
            "Каркас, на который навешиваются остальные рецепты.",
            "Стартовая точка любого мода. Вставляется первым в main.py. Загрузчик рассылает ровно шесть " +
            "событий — onAccountShowGUI, onAccountBecomePlayer, onAccountBecomeNonPlayer, " +
            "onAvatarBecomePlayer, onConnected, onDisconnected — и все они здесь уже объявлены.",
            "Соблазн убрать @_safe «чтобы короче». Тогда одно исключение из init() всплывает в game.init(), " +
            "где стоит except Exception: BigWorld.quit() — клиент закрывается, и моды, чья очередь ещё " +
            "не наступила, init() не получают вовсе.",
            "r01_skeleton.py",
            []),

        new Recipe(
            "R02",
            "Логирование в python.log и пампинг flushPythonLog",
            RecipeCategory.Logging,
            RecipeDifficulty.Beginner,
            "Правильные средства записи в лог плюс петля сброса на диск.",
            "Нужен во всех модах: без него отладка невозможна. Пампинг обязателен, если лог смотрят " +
            "снаружи (tail, живой лог кокпита): на боевом реалме клиент сам файл не сбрасывает.",
            "debug_utils.LOG_DEBUG на релизном клиенте — пустая заглушка (SVR_RELEASE=4 < _logLevel=RELEASE=5), " +
            "записи не будет вообще. Работают LOG_NOTE/LOG_WARNING/LOG_ERROR и stdlib logging.",
            "r02_logging.py",
            [BigWorld, new RecipeRequirement("debug_utils", "LOG_NOTE")]),

        new Recipe(
            "R03",
            "Системное сообщение в ангаре",
            RecipeCategory.Ui,
            RecipeDifficulty.Beginner,
            "SystemMessages.pushMessage с безопасным выбором SM_TYPE.",
            "Самый простой способ показать что-то игроку в ангаре. Применять по onAccountShowGUI: " +
            "приветствие мода, отчёт, предупреждение о неправильной настройке.",
            "До входа в ангар ISystemMessages ещё нет в DI-контейнере — dependency.instance бросит " +
            "DependencyError. Без задержки 2-3 с сообщение у части игроков теряется: панель уведомлений " +
            "ещё не создана.",
            "r03_system_message.py",
            [BigWorld, new RecipeRequirement("gui.SystemMessages", "pushMessage"), new RecipeRequirement("gui.SystemMessages", "SM_TYPE")]),

        new Recipe(
            "R04",
            "Сообщение в боевом логе",
            RecipeCategory.Ui,
            RecipeDifficulty.Beginner,
            "Локальная строка в чат боя через MessengerEntry.",
            "Единственный простой способ показать что-то игроку прямо в бою, не рисуя своего UI. " +
            "Применять после onAvatarReady с задержкой. Сообщение локальное: на сервер не уходит.",
            "entry_decorator отдаёт вызов «текущей области»: в ангаре сообщение уйдёт в лобби-чат. " +
            "Если боевой GUI ещё не создан, battle_entry.__view() вернёт None и сообщение исчезнет " +
            "БЕЗ ошибки — молчаливая потеря.",
            "r04_battle_message.py",
            [BigWorld, new RecipeRequirement("messenger.MessengerEntry", "g_instance"), new RecipeRequirement("messenger.gui.entry_decorator", "addClientMessage")]),

        new Recipe(
            "R05",
            "Вход в бой и выход из боя, с парной отпиской",
            RecipeCategory.Events,
            RecipeDifficulty.Intermediate,
            "Подписка на g_playerEvents с симметричной отпиской в fini().",
            "База для любого боевого мода. Показывает три обязательные вещи: ленивый импорт PlayerEvents, " +
            "try/except в каждом обработчике и симметричную отписку.",
            "Отписка functools.partial или lambda, созданными заново на месте, НЕ сработает — объект " +
            "не найдётся в списке Event. Держите ссылку в переменной модуля. Второй риск: забытый флаг " +
            "при повторном init() даёт двойные вызовы.",
            "r05_battle_events.py",
            [
                new RecipeRequirement("PlayerEvents", "g_playerEvents"),
                new RecipeRequirement("constants", "ARENA_PERIOD")
            ]),

        new Recipe(
            "R06",
            "Результаты боя",
            RecipeCategory.Events,
            RecipeDifficulty.Intermediate,
            "onBattleResultsReceived: победа/поражение, карта, длительность.",
            "Когда мод должен что-то посчитать или показать по итогам боя. Событие приходит уже " +
            "в ангаре, а не в бою.",
            "Исключение из обработчика уходит обратно в Avatar.receiveBattleResults; клиент не упадёт, " +
            "но self.base.confirmBattleResultsReceiving() НЕ вызовется — сервер решит, что игрок " +
            "результатов не получил. Ключи внутри results меняются от патча к патчу: читайте только " +
            "через .get() с дефолтом.",
            "r06_battle_results.py",
            [
                new RecipeRequirement("PlayerEvents", "g_playerEvents"),
                new RecipeRequirement("ArenaType", "g_cache"),
                new RecipeRequirement("helpers.i18n", "makeString")
            ]),

        new Recipe(
            "R07",
            "Периодическая задача с корректной отменой",
            RecipeCategory.Lifecycle,
            RecipeDifficulty.Intermediate,
            "BigWorld.callback в реестре по имени, гасится в fini().",
            "Любой таймер, опрос состояния, отложенное действие, пампинг лога. Реестр по имени защищает " +
            "от двойного запуска — самой частой причины «мод тормозит клиент».",
            "Повторяющегося таймера в движке НЕТ — повтор делается перепланированием из тела колбэка. " +
            "id надо сбросить в None в НАЧАЛЕ колбэка: он уже отработал, и cancelCallback по нему бросит " +
            "исключение. Две подписки подряд дают два параллельных таймера, гасится только последний.",
            "r07_periodic_task.py",
            [BigWorld]),

        new Recipe(
            "R08",
            "Хоткей: перехват нажатия клавиши",
            RecipeCategory.Input,
            RecipeDifficulty.Intermediate,
            "g_keyEventHandlers и InputHandler — обе точки входа клиента.",
            "Открыть окно мода, переключить режим, показать сводку. Разобраны обе легальные точки " +
            "с плюсами и минусами. Рекомендуется g_keyEventHandlers: клиент сам оборачивает вызов " +
            "в try/except.",
            "Повторы (isRepeat) до g_keyEventHandlers не доходят. Коды клавиш — из scripts/client/Keys.py, " +
            "это НЕ коды Windows. Возврат True «съедает» клавишу у всех, кто дальше по цепочке. " +
            "Патчить game.handleKeyEvent нельзя: моды затрут друг друга.",
            "r08_hotkey.py",
            [
                new RecipeRequirement("gui", "g_keyEventHandlers"),
                new RecipeRequirement("gui.InputHandler", "g_instance"),
                new RecipeRequirement("Keys", "KEY_F1")
            ]),

        new Recipe(
            "R09",
            "JSON-конфиг в mods/configs с дефолтами",
            RecipeCategory.Configuration,
            RecipeDifficulty.Intermediate,
            "Поиск корня игры, глубокое слияние с дефолтами, устойчивость к битому файлу.",
            "Как только у мода появляется хоть одна настройка. Мод обязан работать вообще без файла — " +
            "это его рабочий режим по умолчанию.",
            "Корень игры не равен ResMgr.appDirectory(): последний указывает на .../WoT/win64/, " +
            "а не на корень. Блокнот пишет UTF-8 с BOM — json.loads на нём падает, нужен utf-8-sig. " +
            "json.loads в py2 отдаёт unicode-ключи.",
            "r09_json_config.py",
            [ResMgr]),

        new Recipe(
            "R10",
            "Безопасный монкипатч метода клиента",
            RecipeCategory.Patching,
            RecipeDifficulty.Advanced,
            "Пять правил патча, маркер, снятие в fini() без затирания чужого мода.",
            "Когда нужного события нет и остаётся только перехватить метод клиента. Ваш мод не один: " +
            "XVM, pmod и ModsListAPI патчат те же методы, и порядок init() определяется сортировкой " +
            "имён mod_*.pyc.",
            "Снимать патч в fini() можно ТОЛЬКО если сверху всё ещё ваша обёртка (проверка по маркеру), " +
            "иначе затрёте чужой мод. Оригинал вызывать ВСЕГДА, даже если ваш код упал — своя логика " +
            "и вызов оригинала должны быть в РАЗНЫХ try.",
            "r10_monkeypatch.py",
            [
                new RecipeRequirement("Avatar", "PlayerAvatar"),
                new RecipeRequirement("constants", "VEHICLE_HIT_FLAGS")
            ]),

        new Recipe(
            "R11",
            "Сервис через helpers.dependency",
            RecipeCategory.GameState,
            RecipeDifficulty.Intermediate,
            "Получение сервисов клиента с проверкой готовности контейнера.",
            "Точка доступа ко всему внутреннему API клиента: инвентарь, лобби, боевая сессия, app loader. " +
            "Для C#-разработчика это ServiceProvider.GetService с проверкой на null.",
            "В init() контейнер ЕЩЁ НЕ НАПОЛНЕН. dependency.hasInstance() сама бросает DependencyError, " +
            "если менеджера нет вообще, — try/except нужен вокруг неё, а не только вокруг instance(). " +
            "IBattleSessionProvider существует только в бою.",
            "r11_dependency_service.py",
            [
                new RecipeRequirement("helpers.dependency", "instance"),
                new RecipeRequirement("helpers.dependency", "hasInstance"),
                new RecipeRequirement("skeletons.gui.shared", "IItemsCache"),
                new RecipeRequirement("skeletons.gui.lobby_context", "ILobbyContext"),
                new RecipeRequirement("skeletons.gui.battle_session", "IBattleSessionProvider"),
                new RecipeRequirement("skeletons.gui.app_loader", "IAppLoader")
            ]),

        new Recipe(
            "R12",
            "Информация о текущем бою",
            RecipeCategory.GameState,
            RecipeDifficulty.Intermediate,
            "Карта, режим, состав команд — только то, что игрок видит сам.",
            "Основа информационных модов: сводка в боевой лог, счётчики, статистика по режимам. " +
            "Читает только то, что игрок и так видит на загрузочном экране и по Tab.",
            "BigWorld.player() в ангаре — это Account, у него НЕТ .arena. Сразу по onAvatarBecomePlayer " +
            "список arena.vehicles ещё НЕПОЛНЫЙ: техника доезжает событиями VEHICLE_ADDED — читать " +
            "по onAvatarReady с задержкой около 2 с. arenaType может быть None.",
            "r12_battle_info.py",
            [
                BigWorld,
                new RecipeRequirement("BattleReplay", "g_replayCtrl"),
                new RecipeRequirement("constants", "ARENA_GUI_TYPE_LABEL"),
                new RecipeRequirement("helpers.i18n", "makeString")
            ]),

        new Recipe(
            "R13",
            "Свой танк и игрок в ангаре",
            RecipeCategory.GameState,
            RecipeDifficulty.Intermediate,
            "g_currentVehicle, реакция на смену танка, баланс аккаунта.",
            "Ангарные моды: показать что-то про выбранный танк, пересчитать при смене машины, " +
            "вывести баланс. Только свои данные, которые игрок и так видит.",
            "До onAccountShowGUI g_currentVehicle пуст (item == None). itemsCache.waitForSync == True " +
            "означает, что данные ещё едут — не врите игроку прошлыми числами. Отписка от onChanged " +
            "обязательна: _CachedVehicle живёт всю сессию.",
            "r13_hangar_info.py",
            [
                BigWorld,
                new RecipeRequirement("CurrentVehicle", "g_currentVehicle"),
                new RecipeRequirement("helpers.dependency", "instance"),
                new RecipeRequirement("skeletons.gui.shared", "IItemsCache")
            ]),

        new Recipe(
            "R14",
            "Регистрация в ModsListAPI",
            RecipeCategory.Interop,
            RecipeDifficulty.Beginner,
            "Кнопка «Моды» в ангаре; мод работает и без установленного API.",
            "Чтобы мод было видно игроку и можно было по нему кликнуть — обычно открыть настройки. " +
            "Внешняя зависимость me.poliroid.modslistapi; мод обязан работать и без неё.",
            "Регистрироваться в init() НЕЛЬЗЯ: порядок init() модов — сортировка имён mod_*.pyc, " +
            "ваш мод может загрузиться раньше ModsListAPI и получить ImportError. Все параметры кроме " +
            "icon обязательны — при None метод молча ничего не делает.",
            "r14_mods_list_api.py",
            [new RecipeRequirement("gui.modsListApi", "g_modsListApi", IsExternal: true)]),

        new Recipe(
            "R15",
            "Настройки через ModsSettingsAPI",
            RecipeCategory.Interop,
            RecipeDifficulty.Advanced,
            "Окно с галочками в ангаре, хранение и уведомление об изменении.",
            "Когда настроек больше двух и не хочется гонять игрока в JSON. Даёт готовое окно, хранение " +
            "и уведомление об изменении. Внешняя зависимость izeberg.modssettingsapi.",
            "Если не поднять settingsVersion при изменении состава компонентов, compareTemplates " +
            "сравнит JSON, увидит различие и СБРОСИТ настройки игрока. setModTemplate может вернуть " +
            "None. onSettingsChanged широковещательный: приходит от всех модов, фильтруйте по linkage.",
            "r15_mods_settings_api.py",
            [
                new RecipeRequirement("gui.modsSettingsApi", "g_modsSettingsApi", IsExternal: true),
                new RecipeRequirement("gui.modsSettingsApi.templates", IsExternal: true)
            ]),

        new Recipe(
            "R16",
            "Где мы сейчас: ангар, загрузка, бой, логин",
            RecipeCategory.GameState,
            RecipeDifficulty.Intermediate,
            "IAppLoader и GuiGlobalSpaceID вместо угадывания по событиям загрузчика.",
            "Шести событий загрузчика мало: onAccountShowGUI приходит и после боя, onAvatarBecomePlayer " +
            "срабатывает раньше создания боевого GUI. IAppLoader даёт настоящую смену GUI-пространства.",
            "IAppLoader появляется в контейнере не сразу — в init() его может не быть, подписывайтесь " +
            "по onAccountBecomePlayer. onGUISpaceEntered — Event.Event(), исключение уходит в клиент.",
            "r16_gui_space.py",
            [
                new RecipeRequirement("helpers.dependency", "instance"),
                new RecipeRequirement("skeletons.gui.app_loader", "IAppLoader"),
                new RecipeRequirement("skeletons.gui.app_loader", "GuiGlobalSpaceID")
            ])
    ];
}
