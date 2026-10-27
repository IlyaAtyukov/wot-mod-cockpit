# -*- coding: utf-8 -*-
"""РЕЦЕПТ 16. Точно знать, где мы: ангар, загрузка боя, бой, логин.

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
Точка входа mod_<имя>.py — стаб, который только пробрасывает вызовы сюда;
кода рецепта в ней быть не должно.

ЗАЧЕМ: шести событий загрузчика мало. onAccountShowGUI приходит и после боя,
и после смены аккаунта; onAvatarBecomePlayer срабатывает раньше, чем боевой
GUI создан. IAppLoader даёт настоящую смену GUI-пространства.

ПРОВЕРЕНО:
  scripts/client/skeletons/gui/app_loader.py:
      class GuiGlobalSpaceID:
          UNDEFINED=0, WAITING=1, LOGIN=2, LOBBY=3, BATTLE_LOADING=4, BATTLE=5
      class IAppLoader:
          onGUISpaceLeft, onGUISpaceBeforeEnter, onGUISpaceEntered, onGUIInitialized
          def getSpaceID(self)
  Клиент подписывается ровно так же:
      gui/sounds/ambients.py:476   self.appLoader.onGUISpaceEntered += ...
      gui/sounds/ambients.py:482   self.appLoader.onGUISpaceEntered -= ...
      gui/Scaleform/framework/tooltip_mgr.py:193/203
      SoundGroups.py:359

ГДЕ ЛОМАЕТСЯ:
  * IAppLoader появляется в контейнере не сразу: в init() мода его может
    не быть. Подписываемся отложенно — по первому onAccountBecomePlayer
    или через BigWorld.callback;
  * onGUISpaceEntered — Event, исключение из обработчика уходит в клиент;
  * отписка обязательна: appLoader живёт всю сессию.
"""
import logging
import traceback

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

_subscribed = False


def _app_loader():
    try:
        from helpers import dependency
        from skeletons.gui.app_loader import IAppLoader
        if not dependency.hasInstance(IAppLoader):
            return None
        return dependency.instance(IAppLoader)
    except Exception:
        return None


def current_space_id():
    """Текущее GUI-пространство или GuiGlobalSpaceID.UNDEFINED (0)."""
    loader = _app_loader()
    if loader is None:
        return 0
    try:
        return loader.getSpaceID()
    except Exception:
        return 0


def is_lobby():
    try:
        from skeletons.gui.app_loader import GuiGlobalSpaceID
        return current_space_id() == GuiGlobalSpaceID.LOBBY
    except Exception:
        return False


def is_battle():
    try:
        from skeletons.gui.app_loader import GuiGlobalSpaceID
        return current_space_id() == GuiGlobalSpaceID.BATTLE
    except Exception:
        return False


def _on_space_entered(spaceID):
    """Вошли в пространство. ПОДСТАВЬ свою логику."""
    try:
        from skeletons.gui.app_loader import GuiGlobalSpaceID
        if spaceID == GuiGlobalSpaceID.LOBBY:
            _logger.info('%s entered LOBBY', LOG_PREFIX)
        elif spaceID == GuiGlobalSpaceID.BATTLE:
            _logger.info('%s entered BATTLE', LOG_PREFIX)
        elif spaceID == GuiGlobalSpaceID.BATTLE_LOADING:
            _logger.info('%s battle is loading', LOG_PREFIX)
    except Exception:
        _logger.error('%s onGUISpaceEntered handler failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())


def _on_space_left(spaceID):
    try:
        _logger.info('%s left space %s', LOG_PREFIX, spaceID)
        # ПОДСТАВЬ: погасить свои таймеры этого пространства
    except Exception:
        _logger.error('%s onGUISpaceLeft handler failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())


def subscribe():
    """Звать из onAccountBecomePlayer (в init() сервиса ещё может не быть)."""
    global _subscribed
    if _subscribed:
        return True
    loader = _app_loader()
    if loader is None:
        _logger.info('%s IAppLoader is not ready yet', LOG_PREFIX)
        return False
    try:
        loader.onGUISpaceEntered += _on_space_entered
        loader.onGUISpaceLeft += _on_space_left
        _subscribed = True
        return True
    except Exception:
        _logger.error('%s subscribe to app loader failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())
        return False


def unsubscribe():
    """Звать из fini()."""
    global _subscribed
    if not _subscribed:
        return
    loader = _app_loader()
    if loader is not None:
        try:
            loader.onGUISpaceEntered -= _on_space_entered
        except Exception:
            pass
        try:
            loader.onGUISpaceLeft -= _on_space_left
        except Exception:
            pass
    _subscribed = False
