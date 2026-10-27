# -*- coding: utf-8 -*-
"""РЕЦЕПТ 02. Логирование в python.log правильными средствами + пампинг лога.

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
Точка входа mod_<имя>.py — стаб, который только пробрасывает вызовы сюда;
кода рецепта в ней быть не должно.

ПРОВЕРЕНО ПО ИСХОДНИКАМ:
  * scripts/common/debug_utils.py: LOG_NOTE/LOG_WARNING/LOG_ERROR/LOG_CURRENT_EXCEPTION
    обёрнуты @_LogWrapper(LOG_LEVEL.RELEASE) -> на боевом клиенте РАБОТАЮТ.
    LOG_DEBUG обёрнут @_LogWrapper(LOG_LEVEL.SVR_RELEASE=4), а _logLevel=RELEASE=5,
    и _LogWrapper.__call__ проверяет `if self.__lvl >= _logLevel` -> LOG_DEBUG
    на релизном клиенте ПУСТАЯ ЗАГЛУШКА. НЕ ИСПОЛЬЗУЙТЕ ЕЁ.
  * scripts/client/game.py:
        def autoFlushPythonLog():
            BigWorld.flushPythonLog(); BigWorld.callback(5.0, autoFlushPythonLog)
    вызывается ТОЛЬКО внутри `if constants.IS_DEVELOPMENT:` -> на боевом реалме
    клиент лог на диск сам не сбрасывает, и внешний tail видит «замерший» файл.
    Значит, пампить надо самому.

ГДЕ ЛОМАЕТСЯ: если передать в лог py2-строку в cp1251 (например путь из
os.getcwd() на русской Windows), BWLogRedirectionHandler.emit сделает
msg.encode('utf-8') -> UnicodeDecodeError -> logging проглотит его в
handleError и ЗАПИСЬ ПОТЕРЯЕТСЯ ЦЕЛИКОМ. Пути прогоняйте через _fs_text().
"""
import logging
import traceback

import BigWorld

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

_LOG_PUMP_INTERVAL = 0.2               # секунды; клиент в dev-режиме берёт 5.0
_pump_id = None


def _fs_text(value):
    """Байтовую строку файловой системы -> unicode, не теряя запись в логе."""
    if isinstance(value, unicode):
        return value
    if not isinstance(value, str):
        return unicode(value)
    for enc in ('utf-8', 'cp1251', 'latin-1'):
        try:
            return value.decode(enc)
        except Exception:
            continue
    return repr(value).decode('ascii', 'replace')


# --- обычное логирование -----------------------------------------------------
def log_info(message):
    """INFO: попадает в python.log как 'INFO: [MyMod] ...'."""
    try:
        _logger.info('%s %s', LOG_PREFIX, message)
    except Exception:
        pass


def log_error(message):
    """ERROR + полный traceback. Зовите ИЗ except-блока."""
    try:
        _logger.error('%s %s\n%s', LOG_PREFIX, message, traceback.format_exc())
    except Exception:
        pass


def log_via_debug_utils(message):
    """Альтернатива: движковый логгер. Виден в python.log как 'NOTE: ...'.

    Полезен, когда stdlib-logging ещё не сконфигурирован (очень ранний init).
    """
    try:
        import debug_utils
        debug_utils.LOG_NOTE(LOG_PREFIX + ' ' + message)
    except Exception:
        pass


# --- пампинг python.log ------------------------------------------------------
def start_log_pump():
    """Периодически сбрасывать python.log на диск, чтобы tail видел свежие строки."""
    global _pump_id
    if _pump_id is not None:
        return
    _tick()


def stop_log_pump():
    """ОБЯЗАТЕЛЬНО звать из fini(): иначе callback переживёт выгрузку мода."""
    global _pump_id
    if _pump_id is None:
        return
    try:
        BigWorld.cancelCallback(_pump_id)
    except Exception:
        pass
    _pump_id = None


def _tick():
    global _pump_id
    _pump_id = None
    try:
        BigWorld.flushPythonLog()
    except Exception:
        # Если метода вдруг нет — молча выключаемся, лог всё равно не потеряется,
        # просто будет обновляться реже. Ронять клиент из-за лога нельзя.
        return
    try:
        _pump_id = BigWorld.callback(_LOG_PUMP_INTERVAL, _tick)
    except Exception:
        _pump_id = None
