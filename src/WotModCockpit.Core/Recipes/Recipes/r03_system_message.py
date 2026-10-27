# -*- coding: utf-8 -*-
"""РЕЦЕПТ 03. Системное сообщение в ангаре (центр уведомлений).

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
НЕ в точку входа mod_<имя>.py: её build.py компилирует в байткод сам,
встроенным tools/py27c.py, а тот не поддерживает замыкания (cell/free
variables). Этот рецепт замыкание использует, и сборка упадёт с
Py27CompileError, не сославшись на рецепт. Тело мода едет в .mtmod
обычным .py и компилируется уже клиентом — там замыкания законны.

ПРОВЕРЕНО: scripts/client/gui/SystemMessages.py
    SM_TYPE = Enumeration('System message type', [...])
    def pushMessage(text, type=SM_TYPE.Information, priority=None,
                    messageData=None, savedData=None)
    _getSystemMessages() = dependency.instance(ISystemMessages)

Имена SM_TYPE, реально встречающиеся в коде клиента (проверено grep'ом):
    SM_TYPE.Information, SM_TYPE.Warning, SM_TYPE.Error,
    SM_TYPE.InformationHeader, SM_TYPE.WarningHeader, SM_TYPE.ErrorHeader,
    SM_TYPE.ErrorSimple.
Остальные (PurchaseForGold, Selling, PrimeTime...) существуют, но заточены
под свои messageData и в чужом моде смысла не имеют.

ГДЕ ЛОМАЕТСЯ:
  * до входа в ангар сервиса ISystemMessages в контейнере ещё нет ->
    dependency.instance бросит DependencyError. Зовите не раньше
    onAccountShowGUI, и всё равно оборачивайте в try/except;
  * SM_TYPE.<Имя> — атрибут Enumeration; опечатка в имени даст исключение
    на ИМПОРТЕ константы, а не в момент показа. Поэтому берём через getattr.
"""
import logging
import traceback

import BigWorld

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

_delayed_id = None


def push_system_message(text, kind='Information'):
    """Показать сообщение в центре уведомлений ангара.

    text -- unicode или utf-8 str;
    kind -- 'Information' | 'Warning' | 'Error' | 'InformationHeader' |
            'WarningHeader' | 'ErrorHeader' | 'ErrorSimple'.
    Возвращает True, если сообщение ушло.
    """
    try:
        from gui import SystemMessages
        sm_type = getattr(SystemMessages.SM_TYPE, kind, None)
        if sm_type is None:
            sm_type = SystemMessages.SM_TYPE.Information
        SystemMessages.pushMessage(text, type=sm_type)
        return True
    except Exception:
        _logger.error('%s pushMessage failed:\n%s', LOG_PREFIX, traceback.format_exc())
        return False


def push_when_hangar_ready(text, delay=3.0, kind='Information'):
    """То же самое, но с задержкой: сразу по onAccountShowGUI ангар ещё строится.

    Задержка 2-3 с — эмпирическое значение: сообщение, отправленное в тот же
    кадр, что и onAccountShowGUI, у части игроков теряется, потому что панель
    уведомлений ещё не создана.
    """
    global _delayed_id
    cancel_pending()

    def _runner():
        # Тело колбэка ловит всё САМО. BigWorld.callback движок зовёт из своего
        # игрового цикла, вне какого-либо try этого мода: исключение отсюда уйдёт
        # в клиент и попадёт в лог как ошибка без внятного стека.
        global _delayed_id
        _delayed_id = None
        try:
            push_system_message(text, kind)
        except Exception:
            _logger.error('%s delayed system message failed:\n%s',
                          LOG_PREFIX, traceback.format_exc())

    try:
        _delayed_id = BigWorld.callback(float(delay), _runner)
    except Exception:
        _delayed_id = None
        push_system_message(text, kind)


def cancel_pending():
    """Обязательно звать из fini() и из onAccountBecomeNonPlayer()."""
    global _delayed_id
    if _delayed_id is None:
        return
    try:
        BigWorld.cancelCallback(_delayed_id)
    except Exception:
        pass
    _delayed_id = None


# --- пример использования ----------------------------------------------------
# def onAccountShowGUI(ctx):
#     push_when_hangar_ready(u'MyMod v1.0.0 загружен', delay=3.0, kind='Information')
#
# def onAccountBecomeNonPlayer():
#     cancel_pending()
#
# def fini():
#     cancel_pending()
