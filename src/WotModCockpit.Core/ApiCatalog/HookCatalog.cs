namespace WotModCockpit.Core.ApiCatalog;

/// <summary>
/// Курируемая запись каталога горячих точек: что и чем патчат реальные моды.
/// Получена вскрытием 28 <c>.mtmod</c> из установленной игры (только чтение): 309 <c>.pyc</c>,
/// 237 декомпилированы, 7 модов разобраны целиком. 147 <c>.pyc</c> из 311 оказались упакованными
/// стабами и анализу недоступны — поэтому список заведомо неполон и будет пополняться.
/// </summary>
/// <param name="Holder">Класс или модуль-держатель, как его пишут моды.</param>
/// <param name="Member">Имя патчимого члена, как его пишут моды (возможно, mangled).</param>
/// <param name="SiteCount">Сколько точек патча найдено суммарно.</param>
/// <param name="ModsPacked">Идентификаторы модов через <c>|</c>.</param>
/// <param name="KindsPacked">Идиомы патча через <c>|</c>.</param>
public sealed record CuratedHook(string Holder, string Member, int SiteCount, string ModsPacked, string KindsPacked)
{
    /// <summary>Идентификаторы модов, которые это патчат.</summary>
    public string[] Mods => ModsPacked.Length == 0 ? Array.Empty<string>() : ModsPacked.Split('|');

    /// <summary>Идиомы патча: <c>overrideMethod</c>, <c>registerEvent</c>, <c>override</c>, <c>setattr</c>, <c>assign</c>.</summary>
    public string[] Kinds => KindsPacked.Length == 0 ? Array.Empty<string>() : KindsPacked.Split('|');
}

/// <summary>
/// Каталог горячих точек. Единственный курируемый каталог этапа 2 — и это вынужденно:
/// по исходникам клиента нельзя узнать, ЧТО патчат другие моды, для этого надо смотреть на моды.
/// <para>
/// Но курируемый список ОБЯЗАН проверяться по индексу текущей версии клиента. Запись, которой
/// в исходниках нет, помечается <see cref="HookStatus.Missing"/> и показывается как устаревшая,
/// а не как валидная. В разведке так отсеялись 45 точек из 252 — это мёртвые WG-ветки XVM,
/// которых в клиенте Леста 1.44 не существует. Ровно этот механизм спасёт пользователя на 1.45.
/// </para>
/// <para>
/// Две ловушки проверки, обе обязательны (без них подтверждается 54% вместо 82%):
/// де-манглинг <c>_Class__method</c> → <c>__method</c> и подъём по базам, потому что все
/// <c>as_*S</c> объявлены не в самом классе, а в его <c>*Meta</c>-базе.
/// </para>
/// </summary>
public sealed class HookCatalog
{
    /// <summary>Курируемый список: 252 пары (держатель, член) из 7 разобранных модов.</summary>
    public static IReadOnlyList<CuratedHook> Curated { get; } = new CuratedHook[]
    {
        new("game", "handleKeyEvent", 3, "com.modxvm.xvm|izeberg.modssettingsapi|me.poliroid.battlehits", "override|registerEvent"),
        new("Vehicle", "onHealthChanged", 2, "com.modxvm.xvm|me.poliroid.battlehits", "override|registerEvent"),
        new("DamagePanel", "_updateDeviceState", 2, "com.modxvm.xvm", "registerEvent"),
        new("LobbyHeader", "as_setServerS", 2, "com.modxvm.xvm", "registerEvent"),
        new("LobbyHeader", "as_updateBattleTypeS", 2, "com.modxvm.xvm", "overrideMethod|registerEvent"),
        new("PlayerAvatar", "vehicle_onAppearanceReady", 2, "com.modxvm.xvm", "registerEvent"),
        new("ServerInfoPresenter", "_ServerInfoPresenter__updateModel", 2, "com.modxvm.xvm", "registerEvent"),
        new("SixthSenseIndicator", "as_showS", 2, "com.modxvm.xvm", "registerEvent"),
        new("StrategicCamera", "create", 2, "com.modxvm.xvm", "overrideMethod"),
        new("TimersPanel", "_TimersPanel__setFireInVehicle", 2, "com.modxvm.xvm", "registerEvent"),
        new("VehicleGunRotator", "condition", 2, "pruszko.responsive_reticle", "overrideIn"),
        new("game", "fini", 2, "com.modxvm.xvm", "registerEvent"),
        new("i18n", "makeString", 2, "com.modxvm.xvm", "overrideMethod"),
        new("AccountSettings", "setFilter", 1, "com.modxvm.xvm", "overrideStaticMethod"),
        new("AdditionalStatsBlockConstructor", "construct", 1, "com.modxvm.xvm", "overrideMethod"),
        new("AmmunitionPanel", "_AmmunitionPanel__applyCustomizationNewCounter", 1, "com.modxvm.xvm", "overrideMethod"),
        new("AppEntry", "as_updateStageS", 1, "com.modxvm.xvm", "registerEvent"),
        new("AppEntry", "loadView", 1, "com.modxvm.xvm", "overrideMethod"),
        new("AppEntry", "onAsInitializationCompleted", 1, "com.modxvm.xvm", "overrideMethod"),
        new("AppealCMHandler", "getOptions", 1, "me.poliroid.mtreplays.mtrmanager", "override"),
        new("ArcadeCamera", "create", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ArcadeCamera", "enable", 1, "com.modxvm.xvm", "registerEvent"),
        new("ArenaVehiclesPlugin", "_ArenaVehiclesPlugin__showFeatures", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ArenaVehiclesPlugin", "_ArenaVehiclesPlugin__showMinimapHP", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ArenaVehiclesPlugin", "_ArenaVehiclesPlugin__switchToVehicle", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ArenaVehiclesPlugin", "_setInAoI", 1, "com.modxvm.xvm", "registerEvent"),
        new("ArenaVehiclesPlugin", "setSettings", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ArenaVehiclesPlugin", "updateSettings", 1, "com.modxvm.xvm", "overrideMethod"),
        new("BasicCriteriesGroup", "update", 1, "com.modxvm.xvm", "overrideMethod"),
        new("BattleEndWarningPanel", "setTotalTime", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleFieldCtrl", "_BattleFieldCtrl__setAllyMaxHealth", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleFieldCtrl", "_BattleFieldCtrl__setEnemyMaxHealth", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleLoading", "as_setTipS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("BattleLoading", "as_setTipTitleS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("BattleReplay", "getAutoStartFileName", 1, "me.poliroid.mtreplays.mtrmanager", "override"),
        new("BattleReplay", "stop", 1, "com.modxvm.xvm", "overrideMethod"),
        new("BattleResultsCache", "get", 1, "com.modxvm.xvm", "overrideMethod"),
        new("BattleResultsService", "_BattleResultsService__postStatistics", 1, "me.poliroid.mtreplays.mtrmanager", "override"),
        new("BattleResultsWindow", "as_setDataS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("BattleStatisticsDataController", "as_addVehiclesInfoS", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleStatisticsDataController", "as_setFragsS", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleStatisticsDataController", "as_setPersonalStatusS", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleStatisticsDataController", "as_setQuestsInfoS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("BattleStatisticsDataController", "as_setUserTagsS", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleStatisticsDataController", "as_setVehiclesDataS", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleStatisticsDataController", "as_updateInvitationsStatusesS", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleStatisticsDataController", "as_updatePersonalStatusS", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleStatisticsDataController", "as_updatePlayerStatusS", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleStatisticsDataController", "as_updateUserTagsS", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleStatisticsDataController", "as_updateVehicleStatusS", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleStatisticsDataController", "as_updateVehiclesInfoS", 1, "com.modxvm.xvm", "registerEvent"),
        new("BattleStatisticsDataController", "as_updateVehiclesStatsS", 1, "com.modxvm.xvm", "registerEvent"),
        new("CarouselDataProvider", "_vehicleComparisonKey", 1, "com.modxvm.xvm", "overrideClassMethod"),
        new("ChannelsCarouselHandler", "_ChannelsCarouselHandler__setItemField", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ChannelsCarouselHandler", "addChannel", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ChatsPresenter", "_ChatsPresenter__updateModel", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ClientHangarSpace", "_getHangarPath", 1, "me.poliroid.battlehits", "override"),
        new("ClientHangarSpace", "getDefaultHangarPath", 1, "me.poliroid.battlehits", "override"),
        new("ClientHangarSpace", "getVehicleEntity", 1, "me.poliroid.battlehits", "override"),
        new("CommonStatsBlockConstructor", "construct", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CompoundAppearance", "_CompoundAppearance__onModelsRefresh", 1, "me.poliroid.battlehits", "override"),
        new("ContactConverter", "makeVO", 1, "com.modxvm.xvm", "overrideClassMethod"),
        new("ContactNoteManageViewMeta", "as_setUserPropsS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ContactTooltipData", "getDisplayableData", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ContactsListPopover", "_populate", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CrewBooksButtonModel", "setNewAmount", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CrewOperationsPopOver", "onToggleClick", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CrosshairPanelContainer", "as_setSettingsS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CrystalBlockConstructor", "construct", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CurrentVehicleActionsValidator", "_validate", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CustomFilesCache", "_CustomFilesCache__onReadLocalFile", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CustomizationBinSubview", "_finalize", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CustomizationBinSubview", "_initialize", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CustomizationBottomPanel", "_CustomizationBottomPanel__setNotificationCounters", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CustomizationMainView", "_dispose", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CustomizationMainView", "_finalize", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CustomizationMainView", "_initialize", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CustomizationMainView", "_populate", 1, "com.modxvm.xvm", "overrideMethod"),
        new("CustomizationTabItemModel", "setNoveltyCounter", 1, "com.modxvm.xvm", "overrideMethod"),
        new("DailyQuestWidget", "_DailyQuestWidget__shouldHide", 1, "com.modxvm.xvm", "overrideMethod"),
        new("DamageLogPanel", "_DamageLogPanel__isDamageSettingEnabled", 1, "com.modxvm.xvm", "overrideMethod"),
        new("DetailedStatisticsUtils", "getStatistics", 1, "com.modxvm.xvm", "overrideStaticMethod"),
        new("DogTagsController", "_DogTagsController__canShowMarkers", 1, "com.modxvm.xvm", "overrideMethod"),
        new("DynSquadFunctional", "updateVehiclesInfo", 1, "com.modxvm.xvm", "registerEvent"),
        new("DynamicPremiumState", "getVO", 1, "com.modxvm.xvm", "overrideMethod"),
        new("EarningAnimationCommand", "execute", 1, "com.modxvm.xvm", "overrideMethod"),
        new("EventDispatcher", "loadBattleQueue", 1, "me.poliroid.battlehits", "override"),
        new("EventDispatcher", "loadHangar", 1, "me.poliroid.battlehits", "override"),
        new("EventDispatcher", "loadSquad", 1, "me.poliroid.battlehits", "override"),
        new("EventDispatcher", "updateUI", 1, "me.poliroid.battlehits", "override"),
        new("EventEntryPointsContainer", "_EventEntryPointsContainer__updateEntries", 1, "com.modxvm.xvm", "overrideMethod"),
        new("FragsCollectableStats", "getTotalStats", 1, "com.modxvm.xvm", "overrideMethod"),
        new("FullScreenDialogView", "_FullScreenDialogView__setStats", 1, "com.modxvm.xvm", "overrideMethod"),
        new("Hangar", "as_setCarouselS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("Hangar", "as_setEventTournamentBannerVisibleS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("Hangar", "as_updateCarouselEventEntryStateS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("HangarCameraIdle", "_HangarCameraIdle__updateIdleMovement", 1, "me.poliroid.battlehits", "override"),
        new("HangarCameraManager", "_HangarCameraManager__handleLobbyViewMouseEvent", 1, "me.poliroid.battlehits", "override"),
        new("HangarCameraManager", "switchToTank", 1, "me.poliroid.battlehits", "override"),
        new("HangarCameraParallax", "_HangarCameraParallax__update", 1, "me.poliroid.battlehits", "override"),
        new("HangarCarouselDataProvider", "_getSupplyIndices", 1, "com.modxvm.xvm", "overrideMethod"),
        new("HangarHeader", "_HangarHeader__getWidgetAlias", 1, "com.modxvm.xvm", "overrideMethod"),
        new("HangarHeader", "_HangarHeader__updateBattlePassSmallWidget", 1, "com.modxvm.xvm", "overrideMethod"),
        new("HangarVehicleAppearance", "method_name", 1, "me.poliroid.battlehits", "override"),
        new("HeaderMoneyAndXpTooltipData", "_getValue", 1, "com.modxvm.xvm", "overrideMethod"),
        new("HeroTank", "recreateVehicle", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ItemsRequester", "_invalidateItems", 1, "com.modxvm.xvm", "registerEvent"),
        new("ItemsRequester", "clear", 1, "com.modxvm.xvm", "registerEvent"),
        new("ItemsRequester", "getItemByCD", 1, "me.poliroid.mtreplays.mtrmanager", "override"),
        new("LobbyContext", "getPlayerFullName", 1, "me.poliroid.mtreplays.mtrmanager", "override"),
        new("LobbyEntry", "_LobbyEntry__handleLazyChannelCtlInited", 1, "com.modxvm.xvm", "overrideMethod"),
        new("LobbyEntry", "_LobbyEntry__updateCommonChatAccessibility", 1, "com.modxvm.xvm", "overrideMethod"),
        new("LobbyHeader", "_LobbyHeader__setCounter", 1, "com.modxvm.xvm", "overrideMethod"),
        new("LobbyHeader", "_lru_original_as_setHangarMenuDataS", 1, "lesta_replays_upload", "setattr"),
        new("LobbyHeader", "_lru_original_getHangarMenuItemDataProvider", 1, "lesta_replays_upload", "setattr"),
        new("LobbyHeader", "_lru_original_menuItemClick", 1, "lesta_replays_upload", "setattr"),
        new("LobbyHeader", "_lru_original_updateHangarMenuData", 1, "lesta_replays_upload", "setattr"),
        new("LobbyHeader", "as_setHeaderButtonsS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("LobbyMenu", "_LobbyMenu__updateNewSettingsCount", 1, "com.modxvm.xvm", "overrideMethod"),
        new("LobbyMenu", "as_setVersionMessageS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("LoginView", "_populate", 1, "com.modxvm.xvm", "overrideMethod"),
        new("LoginView", "as_setVersionS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("LootBoxesEntryPointWidget", "getIsActive", 1, "com.modxvm.xvm", "overrideStaticMethod"),
        new("MainMenuModel", "setModeId", 1, "com.modxvm.xvm", "registerEvent"),
        new("MarkersManager", "__init__", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MarkersManager", "_dispose", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MarkersManager", "_populate", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MarkersManager", "as_setShowExInfoFlagS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MarkersManager", "createMarker", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MarkersManager", "destroyMarker", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MessageDecorator", "getListVO", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MessengerBar", "_MessengerBar__updateSessionStatsBtn", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MessengerBarMeta", "as_setInitDataS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MinimapComponent", "_dispose", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MinimapComponent", "_populate", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MinimapComponent", "addEntry", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MinimapComponent", "delEntry", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ModuleBlockTooltipData", "_packBlocks", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ModuleInfoMeta", "as_setModuleInfoS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("MoneyBalance", "_MoneyBalance__setStats", 1, "com.modxvm.xvm", "overrideMethod"),
        new("NotificationListButton", "_NotificationListButton__setState", 1, "com.modxvm.xvm", "overrideMethod"),
        new("NotificationListButton", "as_setStateS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("NotificationListView", "_NotificationListView__updateCounters", 1, "com.modxvm.xvm", "overrideMethod"),
        new("NotificationsActionsHandlers", "handleAction", 1, "com.modxvm.xvm", "overrideMethod"),
        new("NotificationsCenterPresenter", "_NotificationsCenterPresenter__onNotificationReceived", 1, "com.modxvm.xvm", "overrideMethod"),
        new("NotificationsCenterPresenter", "_NotificationsCenterPresenter__updateNotifiedMessagesCount", 1, "com.modxvm.xvm", "overrideMethod"),
        new("PersonalEntriesPlugin", "_PersonalEntriesPlugin__updateViewPointEntry", 1, "com.modxvm.xvm", "overrideMethod"),
        new("PersonalEntriesPlugin", "_onVehicleFeedbackReceived", 1, "com.modxvm.xvm", "overrideMethod"),
        new("PersonalEntriesPlugin", "setSettings", 1, "com.modxvm.xvm", "overrideMethod"),
        new("PersonalEntriesPlugin", "start", 1, "com.modxvm.xvm", "overrideMethod"),
        new("PersonalEntriesPlugin", "updateSettings", 1, "com.modxvm.xvm", "overrideMethod"),
        new("PlayerAvatar", "_PlayerAvatar__destroyGUI", 1, "com.modxvm.xvm", "registerEvent"),
        new("PlayerAvatar", "onBecomeNonPlayer", 1, "com.modxvm.xvm", "registerEvent"),
        new("PlayerAvatar", "onBecomePlayer", 1, "com.modxvm.xvm", "registerEvent"),
        new("PlayerAvatar", "updateVehicleGunReloadTime", 1, "com.modxvm.xvm", "registerEvent"),
        new("PlayersPanel", "as_setPanelHPBarVisibilityStateS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("PostMortemControlMode", "onMinimapClicked", 1, "com.modxvm.xvm", "overrideMethod"),
        new("PostmortemPanel", "_PostmortemPanel__onKillerDogTagSet", 1, "com.modxvm.xvm", "overrideMethod"),
        new("PostmortemPanel", "onDogTagKillerInPlaySound", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ProfileMeta", "registerFlashComponent", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ProfileTechnique", "_getTechniqueListVehicles", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ProfileTechnique", "_receiveVehicleDossier", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ProfileTechnique", "_sendAccountData", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ProfileTechnique", "as_setPrestigeVisibleS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ProfileTechniqueWindow", "requestData", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ProfileWindowMeta", "registerFlashComponent", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ProgressiveItemsRewardHandler", "_showAward", 1, "com.modxvm.xvm", "overrideMethod"),
        new("PromoController", "getPromoCount", 1, "com.modxvm.xvm", "overrideMethod"),
        new("RankedBattlesResults", "_populate", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ReferralProgramPresenter", "_ReferralProgramPresenter__updateModel", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ReplayFinishDialogAction", "doAction", 1, "me.poliroid.mtreplays.mtrmanager", "assign"),
        new("Research", "_dispose", 1, "com.modxvm.xvm", "overrideMethod"),
        new("Research", "_populate", 1, "com.modxvm.xvm", "overrideMethod"),
        new("RewardScreenCommand", "execute", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SelectorItem", "isShowNewIndicator", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ServerSettings", "set", 1, "com.modxvm.xvm", "registerEvent"),
        new("ServerSettingsManager", "getSection", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ServerSettingsManager", "setSections", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SessionStatsButton", "_SessionStatsButton__updateBatteleCount", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SessionStatsPresenter", "_SessionStatsPresenter__updateBattleCount", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SessionStatsPresenter", "_SessionStatsPresenter__updateSessionStats", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SettingsCore", "getSetting", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SharedPage", "_switchToPostmortem", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SharedPage", "as_onPostmortemActiveS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SharedPage", "as_setPostmortemTipsVisibleS", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ShowRateSatisfactionCmp", "_convert", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SimpleVehicleCMHandler", "__init__", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SimplifiedStatsBlockConstructor", "construct", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SniperCamera", "_handleSettingsChange", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SniperCamera", "_updateSettingsFromServer", 1, "com.modxvm.xvm", "registerEvent"),
        new("SniperCamera", "create", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SniperCamera", "disable", 1, "com.modxvm.xvm", "registerEvent"),
        new("SniperCamera", "enable", 1, "com.modxvm.xvm", "overrideMethod"),
        new("SniperControlMode", "_SniperControlMode__setupBinoculars", 1, "com.modxvm.xvm", "overrideMethod"),
        new("StatsComposer", "__init__", 1, "com.modxvm.xvm", "overrideMethod"),
        new("StatsRequester", "crystal", 1, "com.modxvm.xvm", "overrideMethod"),
        new("StatsRequester", "freeXP", 1, "com.modxvm.xvm", "overrideMethod"),
        new("StatsRequester", "gold", 1, "com.modxvm.xvm", "overrideMethod"),
        new("StatusBlockConstructor", "construct", 1, "com.modxvm.xvm", "overrideMethod"),
        new("TankCarousel", "__init__", 1, "com.modxvm.xvm", "registerEvent"),
        new("TankCarouselFilterPopover", "_generateMapping", 1, "com.modxvm.xvm", "overrideClassMethod"),
        new("TankCarouselFilterPopover", "_getInitialVO", 1, "com.modxvm.xvm", "overrideMethod"),
        new("TeaserViewer", "show", 1, "com.modxvm.xvm", "overrideMethod"),
        new("TechTreeWindow", "_finalize", 1, "com.modxvm.xvm", "overrideMethod"),
        new("TechTreeWindow", "_initialize", 1, "com.modxvm.xvm", "overrideMethod"),
        new("TimersPanel", "_showDeathZoneTimer", 1, "com.modxvm.xvm", "registerEvent"),
        new("TimersPanel", "_showDestroyTimer", 1, "com.modxvm.xvm", "registerEvent"),
        new("ToolTip", "hide", 1, "com.modxvm.xvm", "registerEvent"),
        new("ToolTip", "onCreateComplexTooltip", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ToolTip", "onCreateTypedTooltip", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ToolTip", "onHideTooltip", 1, "com.modxvm.xvm", "overrideMethod"),
        new("UserMissionsPresenter", "_updateBattlePass", 1, "com.modxvm.xvm", "overrideMethod"),
        new("UserMissionsPresenter", "_updateEntryPoints", 1, "com.modxvm.xvm", "overrideMethod"),
        new("UserMissionsPresenter", "_updateMissions", 1, "com.modxvm.xvm", "overrideMethod"),
        new("Vehicle", "_Vehicle__onAppearanceReady", 1, "me.poliroid.battlehits", "override"),
        new("Vehicle", "isAmmoFull", 1, "com.modxvm.xvm", "overrideMethod"),
        new("Vehicle", "isReadyToFight", 1, "com.modxvm.xvm", "overrideMethod"),
        new("Vehicle", "isReadyToPrebattle", 1, "com.modxvm.xvm", "overrideMethod"),
        new("Vehicle", "set_isCrewActive", 1, "com.modxvm.xvm", "registerEvent"),
        new("Vehicle", "showDamageFromExplosion", 1, "me.poliroid.battlehits", "override"),
        new("Vehicle", "showDamageFromShot", 1, "me.poliroid.battlehits", "override"),
        new("VehicleContextMenuHandler", "_generateOptions", 1, "com.modxvm.xvm", "overrideMethod"),
        new("VehicleContextMenuHandler", "confirmReserveVehicle", 1, "com.modxvm.xvm", "assign"),
        new("VehicleContextMenuHandler", "uncheckReserveVehicle", 1, "com.modxvm.xvm", "assign"),
        new("VehicleEntry", "setActive", 1, "com.modxvm.xvm", "registerEvent"),
        new("VehicleInfoTooltipData", "_VehicleInfoTooltipData__createStatusBlock", 1, "com.modxvm.xvm", "overrideMethod"),
        new("VehicleInfoTooltipData", "_packBlocks", 1, "com.modxvm.xvm", "overrideMethod"),
        new("VehicleMarkerPlugin", "_updateVehicleHealth", 1, "com.modxvm.xvm", "overrideMethod"),
        new("VehicleSelectorPopup", "onSelectVehicles", 1, "com.modxvm.xvm", "overrideMethod"),
        new("VehicleTechTree", "_finalize", 1, "com.modxvm.xvm", "overrideMethod"),
        new("VehicleTechTree", "_initialize", 1, "com.modxvm.xvm", "overrideMethod"),
        new("WWISE", "WW_eventGlobal", 1, "com.modxvm.xvm", "overrideMethod"),
        new("WWISE", "WW_eventGlobalPos", 1, "com.modxvm.xvm", "overrideMethod"),
        new("WWISE", "WW_getSound", 1, "com.modxvm.xvm", "overrideMethod"),
        new("WWISE", "WW_getSoundCallback", 1, "com.modxvm.xvm", "overrideMethod"),
        new("WWISE", "WW_getSoundObject", 1, "com.modxvm.xvm", "overrideMethod"),
        new("WWISE", "WW_getSoundPos", 1, "com.modxvm.xvm", "overrideMethod"),
        new("ZoomStateSwitcher", "_ZoomStateSwitcher__isEnabledBySettings", 1, "com.modxvm.xvm", "overrideMethod"),
        new("_BattleSelectorItems", "select", 1, "com.modxvm.xvm", "registerEvent"),
        new("_CurrentVehicle", "refreshModel", 1, "me.poliroid.battlehits", "override"),
        new("_CurrentVehicle", "updateVehicleDescriptorInModel", 1, "me.poliroid.battlehits", "override"),
        new("_EconomicsRecordsChains", "addResults", 1, "me.poliroid.mtreplays.mtrmanager", "override"),
        new("_FlashBangEffectDesc", "create", 1, "com.modxvm.xvm", "overrideMethod"),
        new("_TechTreeDataProvider", "getAllVehiclePossibleXP", 1, "com.modxvm.xvm", "overrideMethod"),
        new("carousel_data_provider", "_isLockedBackground", 1, "com.modxvm.xvm", "overrideMethod"),
        new("debug_utils", "_doLog", 1, "com.modxvm.xvm", "overrideMethod"),
        new("event_dispatcher", "showBattleResultsWindow", 1, "com.modxvm.xvm", "overrideMethod"),
        new("hint_panel_plugins", "createPlugins", 1, "com.modxvm.xvm", "overrideMethod"),
        new("param_formatter", "formatModuleParamName", 1, "com.modxvm.xvm", "overrideMethod"),
        new("shop", "canBuyGoldForItemThroughWeb", 1, "com.modxvm.xvm", "overrideMethod"),
        new("shop", "canBuyGoldForVehicleThroughWeb", 1, "com.modxvm.xvm", "overrideMethod"),
        new("tooltips", "getUnlockPrice", 1, "com.modxvm.xvm", "overrideMethod"),
    };

    /// <summary>
    /// Сверяет курируемый список с исходниками текущей версии клиента.
    /// </summary>
    /// <param name="scan">Скан дерева. Пустой скан даёт список со статусом <see cref="HookStatus.Unknown"/>.</param>
    /// <returns>Горячие точки, отсортированы по числу модов, затем по числу точек патча.</returns>
    public IReadOnlyList<HookPoint> Verify(ClientSourceScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        var result = new List<HookPoint>(Curated.Count);

        if (scan.IsEmpty)
        {
            foreach (CuratedHook h in Curated)
            {
                result.Add(new HookPoint
                {
                    Holder = h.Holder,
                    Member = h.Member,
                    Mods = h.Mods,
                    SiteCount = h.SiteCount,
                    PatchKinds = h.Kinds,
                    Status = HookStatus.Unknown,
                });
            }

            Sort(result);
            return result;
        }

        // Индекс классов: имя класса → все его объявления в корпусе. Имя НЕ уникально
        // (16 866 классов верхнего уровня), поэтому здесь именно список.
        var classIndex = new Dictionary<string, List<(ScannedClass Cls, ScannedSourceFile File)>>(StringComparer.Ordinal);
        var moduleFuncs = new Dictionary<string, ScannedSourceFile>(StringComparer.Ordinal);
        foreach (ScannedSourceFile f in scan.Files)
        {
            foreach (ScannedClass c in f.Classes)
            {
                if (!classIndex.TryGetValue(c.Name, out List<(ScannedClass, ScannedSourceFile)>? list))
                {
                    list = new List<(ScannedClass, ScannedSourceFile)>(1);
                    classIndex[c.Name] = list;
                }

                list.Add((c, f));
            }

            if (f.ModuleFunctions.Count > 0)
            {
                int dot = f.ModuleName.LastIndexOf('.');
                string leaf = dot >= 0 ? f.ModuleName[(dot + 1)..] : f.ModuleName;

                // Моды пишут держателя коротким именем (`game.handleKeyEvent`), а листовое имя
                // модуля не уникально: `game` есть и как client/game.py, и внутри пакетов.
                // Приоритет у модуля верхнего уровня — именно его и импортирует мод.
                if (leaf.Length > 0 &&
                    (!moduleFuncs.TryGetValue(leaf, out ScannedSourceFile? prev) ||
                     (prev.ModuleName.Contains('.', StringComparison.Ordinal) && dot < 0)))
                {
                    moduleFuncs[leaf] = f;
                }
            }
        }

        foreach (CuratedHook h in Curated)
            result.Add(VerifyOne(h, classIndex, moduleFuncs));

        Sort(result);
        return result;
    }

    private static void Sort(List<HookPoint> list) => list.Sort(static (a, b) =>
    {
        int c = b.ModCount.CompareTo(a.ModCount);
        if (c != 0)
            return c;

        c = b.SiteCount.CompareTo(a.SiteCount);
        if (c != 0)
            return c;

        c = string.CompareOrdinal(a.Holder, b.Holder);
        return c != 0 ? c : string.CompareOrdinal(a.Member, b.Member);
    });

    private static HookPoint VerifyOne(
        CuratedHook h,
        Dictionary<string, List<(ScannedClass Cls, ScannedSourceFile File)>> classIndex,
        Dictionary<string, ScannedSourceFile> moduleFuncs)
    {
        // Модуль ДЕРЖАТЕЛЯ — то, что импортирует мод. Для as_*S он отличается от модуля,
        // где член объявлен: сам член живёт в *Meta-базе, а патчить надо конкретный класс.
        string? holderModule = null;
        bool ambiguous = false;
        if (classIndex.TryGetValue(h.Holder, out List<(ScannedClass Cls, ScannedSourceFile File)>? holderDecls))
        {
            holderModule = holderDecls[0].File.ModuleName;
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach ((ScannedClass _, ScannedSourceFile f) in holderDecls)
                distinct.Add(f.ModuleName);

            ambiguous = distinct.Count > 1;
        }

        var point = new HookPoint
        {
            Holder = h.Holder,
            Member = h.Member,
            Mods = h.Mods,
            SiteCount = h.SiteCount,
            PatchKinds = h.Kinds,
            Status = HookStatus.Missing,
            HolderModuleName = holderModule,
            HolderModuleAmbiguous = ambiguous,
        };

        // Де-манглинг: мод пишет `_TimersPanel__setFireInVehicle`, исходник объявляет `__setFireInVehicle`.
        var candidates = new List<string>(3) { h.Member };
        string mangledPrefix = "_" + h.Holder + "__";
        if (h.Member.StartsWith(mangledPrefix, StringComparison.Ordinal))
            candidates.Add(h.Member[(1 + h.Holder.Length)..]);

        int us = h.Member.IndexOf("__", StringComparison.Ordinal);
        if (h.Member.Length > 0 && h.Member[0] == '_' && us > 0)
        {
            string tail = h.Member[us..];
            if (!candidates.Contains(tail, StringComparer.Ordinal))
                candidates.Add(tail);
        }

        // 1. Метод объявлен прямо в классе-держателе.
        if (holderDecls is not null)
        {
            foreach ((ScannedClass cls, ScannedSourceFile file) in holderDecls)
            {
                foreach (string cand in candidates)
                {
                    if (cls.Methods.TryGetValue(cand, out ScannedFunction? fn))
                    {
                        return point with
                        {
                            Status = HookStatus.Verified,
                            HolderModuleName = file.ModuleName,
                            ModuleName = file.ModuleName,
                            RelPath = file.RelPath,
                            VirtualPath = file.VirtualPath,
                            Line = fn.Line,
                            Signature = fn.Args.Trim(),
                            DeclaredName = cand,
                            DeclaringClass = cls.Name,
                        };
                    }
                }
            }
        }

        // 2. Метод унаследован. Так живут все as_*S: они объявлены в *Meta-базе.
        //    Подъём ОБЯЗАН быть ограничен родством имён: ослабленный вариант «искать по любому
        //    классу» в разведке дал ложные попадания (i18n.makeString разрешился в web_stubs).
        foreach (string cand in candidates)
        {
            foreach ((string clsName, List<(ScannedClass Cls, ScannedSourceFile File)> list) in classIndex)
            {
                if (!IsRelated(h.Holder, clsName))
                    continue;

                foreach ((ScannedClass cls, ScannedSourceFile file) in list)
                {
                    if (cls.Methods.TryGetValue(cand, out ScannedFunction? fn))
                    {
                        return point with
                        {
                            Status = HookStatus.Inherited,
                            ModuleName = file.ModuleName,
                            RelPath = file.RelPath,
                            VirtualPath = file.VirtualPath,
                            Line = fn.Line,
                            Signature = fn.Args.Trim(),
                            DeclaredName = cand,
                            DeclaringClass = cls.Name,
                        };
                    }
                }
            }
        }

        // 3. Держатель — модуль, а не класс (game.handleKeyEvent, i18n.makeString).
        if (moduleFuncs.TryGetValue(h.Holder, out ScannedSourceFile? mf) &&
            mf.ModuleFunctions.TryGetValue(h.Member, out ScannedFunction? mfn))
        {
            return point with
            {
                Status = HookStatus.ModuleLevel,
                HolderModuleName = mf.ModuleName,
                ModuleName = mf.ModuleName,
                RelPath = mf.RelPath,
                VirtualPath = mf.VirtualPath,
                Line = mfn.Line,
                Signature = mfn.Args.Trim(),
                DeclaredName = h.Member,
            };
        }

        return point;
    }

    /// <summary>Родство имён классов, ограничивающее подъём по базам (см. ловушку в описании класса).</summary>
    private static bool IsRelated(string holder, string candidate)
        => candidate.StartsWith(holder, StringComparison.Ordinal)
           || holder.StartsWith(candidate, StringComparison.Ordinal)
           || candidate.EndsWith("Meta", StringComparison.Ordinal);
}
