# -*- coding: utf-8 -*-
"""РЕЦЕПТ 04. Сообщение в бою (боевой лог / чат боя).

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
НЕ в точку входа mod_<имя>.py: её build.py компилирует в байткод сам,
встроенным tools/py27c.py, а тот не поддерживает замыкания (cell/free
variables). Этот рецепт замыкание использует, и сборка упадёт с
Py27CompileError, не сославшись на рецепт. Тело мода едет в .mtmod
обычным .py и компилируется уже клиентом — там замыкания законны.

ПРОВЕРЕНО:
  scripts/client/messenger/MessengerEntry.py -> g_instance = MessengerEntry()
  scripts/client/messenger/gui/entry_decorator.py:
      def addClientMessage(self, message, isCurrentPlayer=False)
  scripts/client/messenger/gui/Scaleform/battle_entry.py:
      addClientMessage -> view.addMessage(message, fillColor=...)
  Так же это делает сам клиент:
      gui/battle_control/requests/controller.py:30
      gui/battle_control/controllers/game_restrictions_msgs_ctrl.py:92
      gui/shared/denunciator.py:126

Сообщение ЛОКАЛЬНОЕ: на сервер не уходит, другие игроки его не видят.

ГДЕ ЛОМАЕТСЯ:
  * entry_decorator отдаёт вызов «текущей области» (battle/lobby/login).
    В ангаре сообщение уйдёт в ЛОББИ-чат, а не в боевой. Проверяйте,
    что вы в бою (BigWorld.player() — это Avatar);
  * если боевой GUI ещё не создан, battle_entry.__view() вернёт None и
    сообщение просто исчезнет БЕЗ ошибки. Отсюда задержка 2 с после
    onAvatarReady;
  * htmlTemplates.format требует ключ, объявленный в messenger-шаблонах.
    'battleErrorMessage' существует (используется клиентом), но красит текст
    в «ошибочный» цвет. Для нейтрального текста передавайте строку как есть.
"""
import logging
import traceback

import BigWorld

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

_delayed_id = None


def is_in_battle():
    """True, если текущий игрок — Avatar (то есть мы в бою), а не Account."""
    try:
        player = BigWorld.player()
        return player is not None and hasattr(player, 'arena')
    except Exception:
        return False


def push_battle_message(text, as_current_player=False):
    """Написать локальную строку в боевой лог.

    as_current_player=True красит плашку в «свой» цвет (FILL_COLORS.BROWN).
    """
    try:
        from messenger import MessengerEntry
        MessengerEntry.g_instance.gui.addClientMessage(
            text, isCurrentPlayer=bool(as_current_player))
        return True
    except Exception:
        _logger.error('%s addClientMessage failed:\n%s', LOG_PREFIX, traceback.format_exc())
        return False


def push_battle_message_error_style(text):
    """Вариант с оформлением «ошибка боя» — ровно так клиент рисует свои алерты."""
    try:
        from messenger import MessengerEntry, g_settings
        formatted = g_settings.htmlTemplates.format('battleErrorMessage',
                                                    ctx={'error': text})
        MessengerEntry.g_instance.gui.addClientMessage(formatted)
        return True
    except Exception:
        # Шаблон мог пропасть в новом патче — падаем на простой текст.
        return push_battle_message(text)


def push_battle_message_delayed(text, delay=2.0):
    """Отложенная отправка: даём боевому GUI создаться."""
    global _delayed_id
    cancel_pending()

    def _runner():
        # Тело колбэка ловит всё САМО. BigWorld.callback движок зовёт из своего
        # игрового цикла, вне какого-либо try этого мода: исключение отсюда уйдёт
        # в клиент и попадёт в лог как ошибка без внятного стека.
        global _delayed_id
        _delayed_id = None
        try:
            if is_in_battle():
                push_battle_message(text)
        except Exception:
            _logger.error('%s delayed battle message failed:\n%s',
                          LOG_PREFIX, traceback.format_exc())

    try:
        _delayed_id = BigWorld.callback(float(delay), _runner)
    except Exception:
        _delayed_id = None


def cancel_pending():
    """Звать из fini() и при выходе из боя."""
    global _delayed_id
    if _delayed_id is None:
        return
    try:
        BigWorld.cancelCallback(_delayed_id)
    except Exception:
        pass
    _delayed_id = None
