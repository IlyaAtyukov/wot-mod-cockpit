# -*- coding: utf-8 -*-
"""РЕЦЕПТ 05. Подписка на вход в бой и выход из боя (с парной отпиской).

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
НЕ в точку входа mod_<имя>.py: её build.py компилирует в байткод сам,
встроенным tools/py27c.py, а тот не поддерживает замыкания (cell/free
variables). Этот рецепт замыкание использует, и сборка упадёт с
Py27CompileError, не сославшись на рецепт. Тело мода едет в .mtmod
обычным .py и компилируется уже клиентом — там замыкания законны.

ПРОВЕРЕНО: scripts/client/PlayerEvents.py, класс _PlayerEvents, синглтон
g_playerEvents. Нужные события существуют:
    onAvatarBecomePlayer      -- Avatar стал играющим (бой начал грузиться)
    onAvatarReady             -- клиент готов, вызывается БЕЗ аргументов
    onAvatarBecomeNonPlayer   -- вышли из боя
    onArenaPeriodChange(period, endTime, length, additionalInfo)
    onArenaStateChange
    onAvatarVehicleLeaveWorld

ГДЕ ЛОМАЕТСЯ — ЭТО ГЛАВНОЕ:
    scripts/common/Event.py, Event.__call__:
        for delegate in self[:]:
            try: delegate(*args, **kwargs)
            except: LOG_CURRENT_EXCEPTION(); raise      <-- RAISE!
    Это Event.Event(), а НЕ SafeEvent. Исключение из вашего обработчика
    ПРОБРАСЫВАЕТСЯ обратно в код клиента, который зажёг событие. Поэтому
    каждый обработчик обязан быть в try/except.

ВТОРОЙ ПОДВОДНЫЙ КАМЕНЬ: Event.__iadd__ кладёт в список сам объект-делегат,
__isub__ ищет его через `in`/`remove`. Отписываться надо ТЕМ ЖЕ объектом.
Связанный метод (self.method) в py2 создаётся заново на каждом обращении,
но сравнивается по __eq__ — с ним всё в порядке. А вот functools.partial или
lambda, созданные заново, НЕ снимутся: держите ссылку в переменной модуля.
"""
import logging
import traceback

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

_subscribed = False
_in_battle = False


def _safe(func):
    def wrapper(*args, **kwargs):
        try:
            return func(*args, **kwargs)
        except Exception:
            try:
                _logger.error('%s exception in %s():\n%s',
                              LOG_PREFIX, func.__name__, traceback.format_exc())
            except Exception:
                pass
        return None

    wrapper.__name__ = func.__name__
    return wrapper


# =============================================================================
#  ОБРАБОТЧИКИ
# =============================================================================
@_safe
def _on_avatar_ready():
    """Бой загружен, игрок в танке. Здесь arena.vehicles уже заполнен."""
    global _in_battle
    _in_battle = True
    _logger.info('%s battle started', LOG_PREFIX)
    # ПОДСТАВЬ свою логику начала боя


@_safe
def _on_avatar_become_non_player():
    """Вышли из боя (в ангар, в реплей-меню, при дисконнекте)."""
    global _in_battle
    if not _in_battle:
        return
    _in_battle = False
    _logger.info('%s battle finished', LOG_PREFIX)
    # ПОДСТАВЬ: сбросить счётчики боя, погасить свои BigWorld.callback


@_safe
def _on_arena_period_change(period, periodEndTime, periodLength, periodAdditionalInfo):
    """Смена фазы боя. period — константа constants.ARENA_PERIOD:
    IDLE=0, WAITING=1, PREBATTLE=2, BATTLE=3, AFTERBATTLE=4.
    Сигнатура подтверждена по ClientArena.__onPeriodChange -> onPeriodChange(*periodInfo).
    """
    from constants import ARENA_PERIOD
    if period == ARENA_PERIOD.BATTLE:
        _logger.info('%s countdown finished, battle is live', LOG_PREFIX)


# =============================================================================
#  ПОДПИСКА / ОТПИСКА  (симметрия обязательна)
# =============================================================================
def subscribe():
    """Звать из init(). Импорт ЛЕНИВЫЙ: PlayerEvents тянет за собой Event."""
    global _subscribed
    if _subscribed:
        return
    try:
        from PlayerEvents import g_playerEvents
        g_playerEvents.onAvatarReady += _on_avatar_ready
        g_playerEvents.onAvatarBecomeNonPlayer += _on_avatar_become_non_player
        g_playerEvents.onArenaPeriodChange += _on_arena_period_change
        _subscribed = True
        _logger.info('%s subscribed to player events', LOG_PREFIX)
    except Exception:
        _logger.error('%s subscribe failed:\n%s', LOG_PREFIX, traceback.format_exc())


def unsubscribe():
    """Звать из fini(). Каждый «-=» стоит ОТДЕЛЬНО и в своём try: если один
    атрибут исчез после патча игры, остальные обработчики всё равно обязаны
    сняться.

    Почему тремя строками, а не циклом по кортежу имён с getattr: имя события,
    взятое из переменной, невидимо для статического анализа — ни preflight
    кокпита (правило WOT009 «подписка без парной отписки»), ни человек при
    беглом чтении не свяжут такой «-=» с конкретным «+=». Изоляцию исключений
    три отдельных try дают ровно ту же, а забытую четвёртую подписку кокпит
    в этой форме заметит.
    """
    global _subscribed
    if not _subscribed:
        return
    try:
        from PlayerEvents import g_playerEvents
    except Exception:
        _subscribed = False
        return
    try:
        g_playerEvents.onAvatarReady -= _on_avatar_ready
    except Exception:
        _logger.error('%s cannot unsubscribe onAvatarReady:\n%s',
                      LOG_PREFIX, traceback.format_exc())
    try:
        g_playerEvents.onAvatarBecomeNonPlayer -= _on_avatar_become_non_player
    except Exception:
        _logger.error('%s cannot unsubscribe onAvatarBecomeNonPlayer:\n%s',
                      LOG_PREFIX, traceback.format_exc())
    try:
        g_playerEvents.onArenaPeriodChange -= _on_arena_period_change
    except Exception:
        _logger.error('%s cannot unsubscribe onArenaPeriodChange:\n%s',
                      LOG_PREFIX, traceback.format_exc())
    _subscribed = False
