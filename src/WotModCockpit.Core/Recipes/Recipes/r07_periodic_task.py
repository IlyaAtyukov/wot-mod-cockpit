# -*- coding: utf-8 -*-
"""РЕЦЕПТ 07. Периодическая задача через BigWorld.callback с корректной отменой.

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
НЕ в точку входа mod_<имя>.py: её build.py компилирует в байткод сам,
встроенным tools/py27c.py, а тот не поддерживает замыкания (cell/free
variables). Этот рецепт замыкание использует, и сборка упадёт с
Py27CompileError, не сославшись на рецепт. Тело мода едет в .mtmod
обычным .py и компилируется уже клиентом — там замыкания законны.

ПРОВЕРЕНО: BigWorld.callback(delay, func) -> id; BigWorld.cancelCallback(id).
Клиент использует эту пару повсеместно, например
gui/game_control/GameSessionController.py:321, gui/game_control/AOGAS.py:158,
и сам клиент строит на ней автосброс лога: game.py:autoFlushPythonLog().

ПРАВИЛА, КОТОРЫЕ НЕЛЬЗЯ НАРУШАТЬ:
  1. Таймера «повторяющегося» в движке НЕТ. Повтор делается перепланированием
     из тела колбэка.
  2. id надо СБРОСИТЬ в None в самом начале колбэка: он уже отработал, и
     cancelCallback по нему бросит исключение.
  3. Каждый выданный id обязан быть погашен в fini(). Незакрытый callback
     выстрелит на полуразобранном клиенте.
  4. Тело колбэка вызывается ДВИЖКОМ. Исключение оттуда всплывает в
     BigWorld и роняет кадр. Всё тело — в try/except.
  5. Не ставьте интервал 0: BigWorld.callback(0.0, f) выполнится в следующем
     кадре и при перепланировании съест кадр целиком.

ГДЕ ЛОМАЕТСЯ ЧАЩЕ ВСЕГО: две подряд подписки (init вызвали дважды, или мод
переустановили «на горячую») дают ДВА параллельных таймера, а погасить
получится только последний. Отсюда реестр _timers по имени.
"""
import logging
import traceback

import BigWorld

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

# name -> callback id
_timers = {}


def start_repeating(name, interval, func):
    """Запустить периодическую задачу.

    name     -- уникальное имя (ключ реестра), например 'stats_poll';
    interval -- период в секундах (float), > 0;
    func     -- функция без аргументов.
    Повторный запуск с тем же именем сначала гасит предыдущий таймер.
    """
    stop(name)
    interval = float(interval)
    if interval <= 0.0:
        interval = 0.1

    def _tick():
        # id уже отработал — сразу забываем его, иначе stop() попытается
        # отменить несуществующий callback.
        _timers.pop(name, None)
        try:
            func()
        except Exception:
            _logger.error('%s periodic task %s failed:\n%s',
                          LOG_PREFIX, name, traceback.format_exc())
        # Перепланируем ДАЖЕ если тело упало: иначе одна ошибка навсегда
        # убивает задачу. Если это не то, что нужно, — вынесите наружу.
        try:
            _timers[name] = BigWorld.callback(interval, _tick)
        except Exception:
            _logger.error('%s cannot reschedule %s:\n%s',
                          LOG_PREFIX, name, traceback.format_exc())

    try:
        _timers[name] = BigWorld.callback(interval, _tick)
    except Exception:
        _logger.error('%s cannot start %s:\n%s', LOG_PREFIX, name, traceback.format_exc())


def start_once(name, delay, func):
    """Одноразовая отложенная задача с тем же реестром."""
    stop(name)

    def _runner():
        _timers.pop(name, None)
        try:
            func()
        except Exception:
            _logger.error('%s delayed task %s failed:\n%s',
                          LOG_PREFIX, name, traceback.format_exc())

    try:
        _timers[name] = BigWorld.callback(float(delay), _runner)
    except Exception:
        _logger.error('%s cannot schedule %s:\n%s', LOG_PREFIX, name, traceback.format_exc())


def stop(name):
    """Погасить одну задачу по имени."""
    callback_id = _timers.pop(name, None)
    if callback_id is None:
        return
    try:
        BigWorld.cancelCallback(callback_id)
    except Exception:
        # Уже сработал или движок выгружается — это не ошибка.
        pass


def stop_all():
    """ОБЯЗАТЕЛЬНО звать из fini(); полезно и при выходе из боя."""
    for name in list(_timers.keys()):
        stop(name)


# --- пример ------------------------------------------------------------------
# def init():
#     start_repeating('log_pump', 0.2, lambda: BigWorld.flushPythonLog())
#
# def fini():
#     stop_all()
