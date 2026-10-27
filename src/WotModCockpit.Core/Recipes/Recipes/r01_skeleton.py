# -*- coding: utf-8 -*-
"""РЕЦЕПТ 01. Скелет мода: init/fini + шесть событий загрузчика.

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
НЕ в точку входа mod_<имя>.py: её build.py компилирует в байткод сам,
встроенным tools/py27c.py, а тот не поддерживает замыкания (cell/free
variables). Декоратор _safe ниже — замыкание, и сборка упадёт с
Py27CompileError, не сославшись на рецепт. Тело мода едет в .mtmod
обычным .py и компилируется уже клиентом — там замыкания законны.

ЧТО ЗНАТЬ ПРО ЗАГРУЗЧИК (scripts/client/gui/mods/__init__.py):
  * он импортирует ТОЛЬКО mod_*.pyc из scripts/client/gui/mods/;
  * зовёт init() при старте и fini() при выходе;
  * рассылает ровно шесть событий: onAccountShowGUI(ctx), onAccountBecomePlayer(),
    onAccountBecomeNonPlayer(), onAvatarBecomePlayer(), onConnected(), onDisconnected();
  * внутри _callModMethod ловит ТОЛЬКО AttributeError.

ГДЕ ЛОМАЕТСЯ: любое другое исключение из init() всплывает в game.init(), где стоит
`except Exception: LOG_CURRENT_EXCEPTION(); BigWorld.quit()` -> КЛИЕНТ ЗАКРЫВАЕТСЯ,
и все моды, чья очередь ещё не наступила (XVM, ModsListAPI, pmod), init() не получат.
Поэтому КАЖДАЯ функция, которую зовёт игра, обёрнута в @_safe.
"""
import logging
import traceback

# --- ПОДСТАВЬ СВОЁ -----------------------------------------------------------
MOD_ID = 'ru.local.my_mod'      # тот же, что в <id> src/meta.xml
MOD_NAME = 'My Mod'             # человекочитаемое имя
MOD_VERSION = '1.0.0'           # та же, что в <version> src/meta.xml
LOG_PREFIX = '[MyMod]'          # тег для grep и для фильтра WoT Mod Cockpit
# -----------------------------------------------------------------------------

_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ имя логгера

_active = False        # мод реально запустился (init прошёл до конца)
_in_battle = False     # мы сейчас в бою


def _safe(func):
    """Наружу не выпускаем НИ ОДНОГО исключения.

    Это не «на всякий случай»: исключение из init() гасит клиент, а исключение
    из обработчика Event.Event() (а все события клиента именно такие, не
    SafeEvent) прилетает обратно в вызвавший код игры.
    """

    def wrapper(*args, **kwargs):
        try:
            return func(*args, **kwargs)
        except Exception:
            try:
                _logger.error('%s unhandled exception in %s():\n%s',
                              LOG_PREFIX, func.__name__, traceback.format_exc())
            except Exception:
                pass
        return None

    wrapper.__name__ = func.__name__
    wrapper.__doc__ = func.__doc__
    return wrapper


# =============================================================================
#  ЖИЗНЕННЫЙ ЦИКЛ
# =============================================================================
@_safe
def init():
    """Вызывается один раз при старте клиента, ДО появления GUI аккаунта.

    Здесь ещё нет ни ангара, ни BigWorld.player(). Всё «тяжёлое» (подписки на
    сервисы, монкипатчи) ставим здесь, всё «ангарное» — по onAccountShowGUI.
    """
    global _active
    _logger.info('%s init: %s v%s', LOG_PREFIX, MOD_NAME, MOD_VERSION)
    _subscribe()
    _active = True


@_safe
def fini():
    """Вызывается при штатном выходе из клиента.

    ОБЯЗАТЕЛЬНО симметрично init(): снять подписки, погасить BigWorld.callback,
    снять монкипатчи. Клиент всё равно закрывается, но незакрытый callback
    успеет выстрелить на полуразобранном клиенте и насорить в лог.
    """
    global _active
    _unsubscribe()
    _active = False
    _logger.info('%s fini', LOG_PREFIX)


# =============================================================================
#  ШЕСТЬ СОБЫТИЙ ЗАГРУЗЧИКА (имена менять нельзя)
# =============================================================================
@_safe
def onAccountShowGUI(ctx):
    """Появился GUI аккаунта = игрок вошёл в ангар. Самое частое место для работы."""
    _logger.info('%s onAccountShowGUI', LOG_PREFIX)


@_safe
def onAccountBecomePlayer():
    """Сущность Account стала играющей: аккаунт готов, ангар ещё может не быть."""
    _logger.info('%s onAccountBecomePlayer', LOG_PREFIX)


@_safe
def onAccountBecomeNonPlayer():
    """Аккаунт перестал быть играющим: уход в бой или разлогин."""
    _logger.info('%s onAccountBecomeNonPlayer', LOG_PREFIX)


@_safe
def onAvatarBecomePlayer():
    """Сущность Avatar стала играющей = мы в бою.

    ВНИМАНИЕ: на этот момент BigWorld.player().arena уже есть, но список
    техники ещё может быть неполным. Данные боя лучше читать по
    g_playerEvents.onAvatarReady (см. рецепт 05).
    """
    global _in_battle
    _in_battle = True
    _logger.info('%s onAvatarBecomePlayer', LOG_PREFIX)


@_safe
def onConnected():
    """Соединение с игровым сервером установлено."""
    _logger.info('%s onConnected', LOG_PREFIX)


@_safe
def onDisconnected():
    """Соединение потеряно/закрыто. Сбрасываем состояние, привязанное к сессии."""
    global _in_battle
    _in_battle = False
    _logger.info('%s onDisconnected', LOG_PREFIX)


# =============================================================================
#  ПОДПИСКИ (заполняется рецептами 05/06)
# =============================================================================
_subscribed = False


def _subscribe():
    global _subscribed
    if _subscribed:
        return
    # сюда добавляются g_playerEvents.onXxx += handler
    _subscribed = True


def _unsubscribe():
    global _subscribed
    if not _subscribed:
        return
    # сюда добавляются g_playerEvents.onXxx -= handler (ровно те же объекты!)
    _subscribed = False
