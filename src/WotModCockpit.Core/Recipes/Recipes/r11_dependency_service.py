# -*- coding: utf-8 -*-
"""РЕЦЕПТ 11. Получение сервиса через helpers.dependency и проверка готовности.

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
Точка входа mod_<имя>.py — стаб, который только пробрасывает вызовы сюда;
кода рецепта в ней быть не должно.

ПРОВЕРЕНО: scripts/client/helpers/dependency.py = реэкспорт
scripts/common/dependency_injection_container.py, где есть:
    def hasInstance(class_)        -- есть ли сервис в контейнере
    def instance(class_)           -- получить (бросает DependencyError, если
                                      менеджер ещё не создан)
    def getInstanceIfHas(class_)   -- instance() или None
    def descriptor(class_)         -- дескриптор для поля класса

Интерфейсы («скелетоны») лежат в scripts/client/skeletons/. Проверенные:
    skeletons.gui.system_messages.ISystemMessages
    skeletons.gui.shared.IItemsCache            (свойство .items)
    skeletons.gui.lobby_context.ILobbyContext   (.getServerSettings())
    skeletons.gui.battle_session.IBattleSessionProvider
                                                (.arenaVisitor, .getArenaDP())
    skeletons.gui.app_loader.IAppLoader         (.getSpaceID(), onGUISpaceEntered)

ПОЧЕМУ НЕ dependency.descriptor В МОДЕ: дескриптор разрешается при обращении
к атрибуту, и если контейнер ещё не сконфигурирован — бросит DependencyError
в самом неожиданном месте. В моде надёжнее явный вызов с проверкой.

ГДЕ ЛОМАЕТСЯ:
  * в init() мода контейнер ЕЩЁ НЕ НАПОЛНЕН: gui.shared.personality.init()
    зовёт моды до/во время сборки сервисов. Сервисы берите не раньше
    onAccountShowGUI (ангарные) и onAvatarBecomePlayer (боевые);
  * hasInstance() сама бросает DependencyError, если менеджера нет вообще —
    отсюда try/except вокруг неё, а не только вокруг instance();
  * IBattleSessionProvider живёт только в бою: в ангаре его нет.
"""
import logging
import traceback

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ


def get_service(interface):
    """Вернуть сервис или None. Никогда не бросает."""
    try:
        from helpers import dependency
        if not dependency.hasInstance(interface):
            return None
        return dependency.instance(interface)
    except Exception:
        return None


def get_items_cache():
    """IItemsCache — инвентарь/статистика аккаунта. Только в ангаре."""
    try:
        from skeletons.gui.shared import IItemsCache
    except Exception:
        return None
    return get_service(IItemsCache)


def get_lobby_context():
    """ILobbyContext — серверные настройки лобби."""
    try:
        from skeletons.gui.lobby_context import ILobbyContext
    except Exception:
        return None
    return get_service(ILobbyContext)


def get_battle_session():
    """IBattleSessionProvider — данные текущего боя. Только в бою."""
    try:
        from skeletons.gui.battle_session import IBattleSessionProvider
    except Exception:
        return None
    return get_service(IBattleSessionProvider)


def get_app_loader():
    """IAppLoader — какое GUI-пространство сейчас активно."""
    try:
        from skeletons.gui.app_loader import IAppLoader
    except Exception:
        return None
    return get_service(IAppLoader)


# --- проверка готовности -----------------------------------------------------
def is_items_cache_ready():
    """Сервис есть И синхронизирован с сервером.

    IItemsCache.waitForSync == True означает, что данные ещё едут; читать
    items.stats в этот момент можно, но значения будут прошлыми.
    """
    cache = get_items_cache()
    if cache is None:
        return False
    try:
        if getattr(cache, 'waitForSync', False):
            return False
        return cache.items is not None
    except Exception:
        return False


def is_in_lobby():
    """True, если активно ангарное GUI-пространство.

    GuiGlobalSpaceID (skeletons/gui/app_loader.py):
    UNDEFINED=0, WAITING=1, LOGIN=2, LOBBY=3, BATTLE_LOADING=4, BATTLE=5.
    """
    loader = get_app_loader()
    if loader is None:
        return False
    try:
        from skeletons.gui.app_loader import GuiGlobalSpaceID
        return loader.getSpaceID() == GuiGlobalSpaceID.LOBBY
    except Exception:
        return False


def with_service(interface, action, what='service'):
    """Выполнить action(service), если сервис есть. Всё — под try/except."""
    service = get_service(interface)
    if service is None:
        _logger.info('%s %s is not available yet', LOG_PREFIX, what)
        return None
    try:
        return action(service)
    except Exception:
        _logger.error('%s action on %s failed:\n%s',
                      LOG_PREFIX, what, traceback.format_exc())
        return None
