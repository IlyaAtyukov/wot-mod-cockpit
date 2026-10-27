# -*- coding: utf-8 -*-
"""РЕЦЕПТ 08. Хоткей: перехват нажатия клавиши. ЧТО РЕАЛЬНО РАБОТАЕТ В 1.44.

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
Точка входа mod_<имя>.py — стаб, который только пробрасывает вызовы сюда;
кода рецепта в ней быть не должно.

ПРОВЕРЕНО ПО scripts/client/game.py:348 handleKeyEvent(event) — единственная
точка, куда движок отдаёт клавиатуру. Порядок внутри (сокращённо):
    ...
    if not isRepeat:
        InputHandler.g_instance.handleKeyEvent(event)   # <- ТОЧКА 1
        if not guiHandled and GUI.handleKeyEvent(event): return True
    ...
    if not isRepeat:
        if MessengerEntry.g_instance.gui.handleKey(event): return True
    inputHandler = getattr(BigWorld.player(), 'inputHandler', None)
    if inputHandler is not None:
        if inputHandler.handleKeyEvent(event): return True
    for handler in g_keyEventHandlers:                   # <- ТОЧКА 2
        try:
            if handler(event): return True
        except Exception:
            LOG_CURRENT_EXCEPTION()
    return False

ДВЕ ЛЕГАЛЬНЫЕ ТОЧКИ, обе существуют в 1.44:

ТОЧКА 2 — gui.g_keyEventHandlers (РЕКОМЕНДУЕТСЯ).
    Обычный set(). Клиент сам оборачивает вызов в try/except, так что даже
    кривой обработчик не уронит ввод. Обработчик получает event и возвращает
    True, если событие «съедено».
    МИНУС: до вас клавишу уже посмотрят GUI, чат и inputHandler игрока —
    занятые игрой клавиши (W/A/S/D, Tab, Enter в чате) до вас не дойдут.
    Это не баг, а ровно то, что нужно моду: не воевать с управлением.

ТОЧКА 1 — gui.InputHandler.g_instance.onKeyDown / onKeyUp.
    scripts/client/gui/InputHandler.py: класс _InputHandler с onKeyDown/onKeyUp
    типа Event.Event(). Срабатывает РАНЬШЕ GUI, видит все клавиши.
    МИНУС и ОПАСНОСТЬ: это Event.Event(), а не SafeEvent — исключение из
    обработчика улетает прямо в game.handleKeyEvent. И «съесть» клавишу
    отсюда нельзя: возвращаемое значение игнорируется.

ЧЕГО ДЕЛАТЬ НЕЛЬЗЯ: подменять сам game.handleKeyEvent — это глобальная функция,
её патчат сразу несколько модов, и последний затирает предыдущих.

ГДЕ ЛОМАЕТСЯ:
  * повторные нажатия (isRepeat) до g_keyEventHandlers не доходят —
    удержание клавиши даст ровно одно событие KeyDown;
  * коды клавиш — из scripts/client/Keys.py (KEY_F1=59, KEY_G=34 и т.д.),
    это НЕ коды Windows;
  * модификаторы читаются методами события: isCtrlDown/isAltDown/isShiftDown
    (подтверждено использованием в клиенте).
"""
import logging
import traceback

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

# ПОДСТАВЬ: клавиша и модификаторы. Имена — из scripts/client/Keys.py.
HOTKEY_NAME = 'KEY_F9'
NEED_CTRL = False
NEED_ALT = False
NEED_SHIFT = False

_registered = False


def _on_hotkey():
    """ПОДСТАВЬ свою реакцию на хоткей."""
    _logger.info('%s hotkey pressed', LOG_PREFIX)


def _handle_key_event(event):
    """Обработчик для gui.g_keyEventHandlers.

    Возврат True = событие съедено, дальше по цепочке не пойдёт.
    Возврат False/None = пропускаем дальше. По умолчанию НЕ съедаем ничего,
    кроме своей комбинации: съеденная чужая клавиша ломает игру молча.
    """
    try:
        import Keys
        wanted = getattr(Keys, HOTKEY_NAME, None)
        if wanted is None:
            return False
        if not event.isKeyDown():
            return False
        if event.key != wanted:
            return False
        if NEED_CTRL and not event.isCtrlDown():
            return False
        if NEED_ALT and not event.isAltDown():
            return False
        if NEED_SHIFT and not event.isShiftDown():
            return False
        _on_hotkey()
        return True
    except Exception:
        _logger.error('%s key handler failed:\n%s', LOG_PREFIX, traceback.format_exc())
        return False


def register():
    """Звать из init()."""
    global _registered
    if _registered:
        return
    try:
        from gui import g_keyEventHandlers
        g_keyEventHandlers.add(_handle_key_event)
        _registered = True
        _logger.info('%s hotkey %s registered', LOG_PREFIX, HOTKEY_NAME)
    except Exception:
        _logger.error('%s cannot register hotkey:\n%s', LOG_PREFIX, traceback.format_exc())


def unregister():
    """Звать из fini(). discard, а не remove: не бросает, если уже удалён."""
    global _registered
    if not _registered:
        return
    try:
        from gui import g_keyEventHandlers
        g_keyEventHandlers.discard(_handle_key_event)
    except Exception:
        _logger.error('%s cannot unregister hotkey:\n%s', LOG_PREFIX, traceback.format_exc())
    _registered = False


# =============================================================================
#  ВАРИАНТ «РАНЬШЕ GUI» — только если точки 2 не хватило
# =============================================================================
_early_registered = False


def _early_key_down(event):
    # ОБЯЗАТЕЛЬНО без исключений наружу: это Event.Event(), он делает raise.
    try:
        import Keys
        wanted = getattr(Keys, HOTKEY_NAME, None)
        if wanted is not None and event.key == wanted:
            _on_hotkey()
    except Exception:
        _logger.error('%s early key handler failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())


def register_early():
    global _early_registered
    if _early_registered:
        return
    try:
        from gui import InputHandler
        InputHandler.g_instance.onKeyDown += _early_key_down
        _early_registered = True
    except Exception:
        _logger.error('%s cannot register early hotkey:\n%s',
                      LOG_PREFIX, traceback.format_exc())


def unregister_early():
    global _early_registered
    if not _early_registered:
        return
    try:
        from gui import InputHandler
        InputHandler.g_instance.onKeyDown -= _early_key_down
    except Exception:
        pass
    _early_registered = False
