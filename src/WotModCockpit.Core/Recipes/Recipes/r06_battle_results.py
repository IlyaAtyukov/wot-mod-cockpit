# -*- coding: utf-8 -*-
"""РЕЦЕПТ 06. Результаты боя (событие конца боя).

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
Точка входа mod_<имя>.py — стаб, который только пробрасывает вызовы сюда;
кода рецепта в ней быть не должно.

ПРОВЕРЕНО:
  scripts/client/Avatar.py:2354 PlayerAvatar.receiveBattleResults():
      self.__battleResults = cPickle.loads(zlib.decompress(data))
      g_playerEvents.onBattleResultsReceived(
          True, BattleResultsCache.convertToFullForm(self.__battleResults))
  То есть сигнатура обработчика: handler(isPlayerVehicle, results).

СТРУКТУРА results (scripts/common/battle_results/battle_results_common.py,
подтверждено по gui/battle_results/reusable/*):
    results['common']   -- ENTRY_TYPE.COMMON:
        'arenaTypeID' (int), 'winnerTeam' (int), 'bonusType', 'guiType',
        'duration', 'arenaCreateTime'
    results['personal']['avatar'] -- ENTRY_TYPE.ACCOUNT_SELF:
        'team' (int), 'xp', ...
    results['players'], results['vehicles'], results['avatars']

winnerTeam: 0 — ничья, иначе номер победившей команды.

ГДЕ ЛОМАЕТСЯ:
  * событие приходит ПОСЛЕ возврата в ангар, а не в бою;
  * ключи внутри results меняются от патча к патчу — читайте только через
    .get() с дефолтом, никогда через [] по цепочке;
  * Event.Event() пробрасывает исключение обратно в Avatar.receiveBattleResults,
    где стоит `except Exception: LOG_CURRENT_EXCEPTION()`. Клиент не упадёт,
    но self.base.confirmBattleResultsReceiving() НЕ ВЫЗОВЕТСЯ — сервер решит,
    что игрок результатов не получил. Обработчик обязан быть безопасным.
"""
import logging
import traceback

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

_subscribed = False


def _on_battle_results(isPlayerVehicle, results):
    """Обработчик g_playerEvents.onBattleResultsReceived."""
    try:
        if not isPlayerVehicle or not isinstance(results, dict):
            return
        common = results.get('common') or {}
        personal = results.get('personal') or {}
        avatar = personal.get('avatar') or {}

        my_team = avatar.get('team', 0)
        winner = common.get('winnerTeam', 0)
        duration = common.get('duration', 0)

        if winner == 0:
            outcome = u'ничья'
        elif winner == my_team:
            outcome = u'победа'
        else:
            outcome = u'поражение'

        map_name = _arena_name(common.get('arenaTypeID', 0))

        _logger.info('%s battle result: %s, map=%s, duration=%ss',
                     LOG_PREFIX, outcome.encode('utf-8'), map_name.encode('utf-8'),
                     duration)
        # ПОДСТАВЬ: своя реакция. Например системное сообщение (рецепт 03):
        # from gui import SystemMessages
        # SystemMessages.pushMessage(u'Бой на карте %s: %s' % (map_name, outcome))
    except Exception:
        _logger.error('%s onBattleResultsReceived handler failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())


def _arena_name(arena_type_id):
    """Локализованное имя карты по arenaTypeID.

    ВНИМАНИЕ: на клиенте ArenaType.g_cache[arenaTypeID].name УЖЕ локализовано.
    common/ArenaType.py:302:
        if IS_CLIENT or IS_WEB:
            cfg['name'] = i18n.makeString(_readString('name', section, defaultXml))
    То есть ключ '#arenas:100_thepit/name' разворачивается при сборке кэша, а
    makeString ниже — вторая, холостая прогонка. Оставлена страховкой: от обычной
    строки makeString возвращает её же, а на сервере/в нелокализованном значении
    ключ всё-таки развернётся. Вызов можно убрать без потери поведения.
    """
    try:
        import ArenaType
        from helpers import i18n
        arena_type = ArenaType.g_cache.get(arena_type_id)
        if arena_type is None:
            return u'?'
        return i18n.makeString(arena_type.name)
    except Exception:
        return u'?'


def subscribe():
    global _subscribed
    if _subscribed:
        return
    try:
        from PlayerEvents import g_playerEvents
        g_playerEvents.onBattleResultsReceived += _on_battle_results
        _subscribed = True
    except Exception:
        _logger.error('%s subscribe failed:\n%s', LOG_PREFIX, traceback.format_exc())


def unsubscribe():
    global _subscribed
    if not _subscribed:
        return
    try:
        from PlayerEvents import g_playerEvents
        g_playerEvents.onBattleResultsReceived -= _on_battle_results
    except Exception:
        _logger.error('%s unsubscribe failed:\n%s', LOG_PREFIX, traceback.format_exc())
    _subscribed = False
