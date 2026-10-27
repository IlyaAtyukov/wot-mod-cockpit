# -*- coding: utf-8 -*-
"""РЕЦЕПТ 13. Информация о своём танке и игроке в ангаре.

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
Точка входа mod_<имя>.py — стаб, который только пробрасывает вызовы сюда;
кода рецепта в ней быть не должно.

ПРОВЕРЕНО:
  scripts/client/CurrentVehicle.py:
      g_currentVehicle = _CurrentVehicle()          (строка 515)
      _CachedVehicle.isPresent()                    -> item is not None
      _CurrentVehicle.item                          -> itemsCache.items.getVehicle(invID)
      _CurrentVehicle.isInHangar() / isReadyToFight() / isBroken()
      _CachedVehicle.onChanged / onChangeStarted    -- Event(EventManager)
  scripts/client/gui/shared/gui_items/Vehicle.py:
      свойства name / userName / shortUserName / level
  scripts/client/gui/shared/utils/requesters/StatsRequester.py:
      credits / gold / freeXP / globalRating
      (доступ: itemsCache.items.stats.credits)

Всё это — данные СВОЕГО аккаунта, которые игрок видит в ангаре.

ГДЕ ЛОМАЕТСЯ:
  * до onAccountShowGUI g_currentVehicle пуст, item == None;
  * itemsCache.waitForSync == True означает, что данные ещё синхронизируются;
  * g_currentVehicle.onChanged — это Event.Event(): исключение из обработчика
    улетает в клиент, обработчик обязан быть в try/except;
  * отписка ОБЯЗАТЕЛЬНА: _CachedVehicle живёт всю сессию, ваш обработчик
    останется в списке и после выгрузки мода.
"""
import logging
import traceback

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

_subscribed = False


def get_current_vehicle_info():
    """Словарь про выбранный в ангаре танк. Никогда не бросает.

    Ключи: present (bool), name (тех. имя, 'ussr:R04_T-34'), user_name
    (локализованное), short_name, level (1-10), in_hangar, ready_to_fight.
    """
    info = {
        'present': False,
        'name': u'',
        'user_name': u'',
        'short_name': u'',
        'level': 0,
        'in_hangar': False,
        'ready_to_fight': False,
    }
    try:
        from CurrentVehicle import g_currentVehicle
        if not g_currentVehicle.isPresent():
            return info
        item = g_currentVehicle.item
        if item is None:
            return info
        info['present'] = True
        info['name'] = unicode(getattr(item, 'name', u''))
        info['user_name'] = unicode(getattr(item, 'userName', u''))
        info['short_name'] = unicode(getattr(item, 'shortUserName', u''))
        info['level'] = int(getattr(item, 'level', 0) or 0)
        info['in_hangar'] = bool(g_currentVehicle.isInHangar())
        info['ready_to_fight'] = bool(g_currentVehicle.isReadyToFight())
    except Exception:
        _logger.error('%s cannot read current vehicle:\n%s',
                      LOG_PREFIX, traceback.format_exc())
    return info


def get_account_info():
    """Ник и валюта своего аккаунта — ровно то, что видно в шапке ангара."""
    info = {'name': u'', 'credits': 0, 'gold': 0, 'free_xp': 0}
    try:
        import BigWorld
        player = BigWorld.player()
        info['name'] = unicode(getattr(player, 'name', u'') or u'')
    except Exception:
        pass
    try:
        from helpers import dependency
        from skeletons.gui.shared import IItemsCache
        if not dependency.hasInstance(IItemsCache):
            return info
        cache = dependency.instance(IItemsCache)
        if getattr(cache, 'waitForSync', False):
            # Данные ещё едут с сервера — вернём то, что есть, но не соврём.
            return info
        stats = cache.items.stats
        info['credits'] = int(getattr(stats, 'credits', 0) or 0)
        info['gold'] = int(getattr(stats, 'gold', 0) or 0)
        info['free_xp'] = int(getattr(stats, 'freeXP', 0) or 0)
    except Exception:
        _logger.error('%s cannot read account stats:\n%s',
                      LOG_PREFIX, traceback.format_exc())
    return info


# =============================================================================
#  РЕАКЦИЯ НА СМЕНУ ТАНКА В АНГАРЕ
# =============================================================================
def _on_vehicle_changed(*args, **kwargs):
    """Event.Event() -> обязателен try/except. Аргументы игнорируем:
    в разных версиях клиента их число отличается."""
    try:
        info = get_current_vehicle_info()
        if info['present']:
            _logger.info('%s vehicle changed to %s (lvl %d)',
                         LOG_PREFIX, info['user_name'].encode('utf-8'), info['level'])
    except Exception:
        _logger.error('%s onChanged handler failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())


def subscribe():
    """Звать из onAccountShowGUI (не из init: модуль ещё не готов)."""
    global _subscribed
    if _subscribed:
        return
    try:
        from CurrentVehicle import g_currentVehicle
        g_currentVehicle.onChanged += _on_vehicle_changed
        _subscribed = True
    except Exception:
        _logger.error('%s subscribe failed:\n%s', LOG_PREFIX, traceback.format_exc())


def unsubscribe():
    """Звать из fini() и из onAccountBecomeNonPlayer()."""
    global _subscribed
    if not _subscribed:
        return
    try:
        from CurrentVehicle import g_currentVehicle
        g_currentVehicle.onChanged -= _on_vehicle_changed
    except Exception:
        pass
    _subscribed = False
