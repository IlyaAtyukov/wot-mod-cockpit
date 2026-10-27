# -*- coding: utf-8 -*-
"""РЕЦЕПТ 10. Безопасный монкипатч метода клиента.

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
НЕ в точку входа mod_<имя>.py: её build.py компилирует в байткод сам,
встроенным tools/py27c.py, а тот не поддерживает замыкания (cell/free
variables). Этот рецепт замыкание использует, и сборка упадёт с
Py27CompileError, не сославшись на рецепт. Тело мода едет в .mtmod
обычным .py и компилируется уже клиентом — там замыкания законны.

ЗАЧЕМ ПРАВИЛА: ваш мод не один. XVM, pmod, ModsListAPI патчат те же самые
методы, и порядок init() модов определяется сортировкой имён mod_*.pyc.
Патч, который «съедает» оригинал или падает, ломает всех, кто встал после вас.

ПЯТЬ ОБЯЗАТЕЛЬНЫХ ПРАВИЛ:
  1. Сохранить оригинал и ВСЕГДА его вызывать — даже если ваш код упал.
  2. Своя логика — в ОТДЕЛЬНОМ try/except, вокруг вызова оригинала — нет
     (исключение оригинала должно вести себя как без мода).
  3. Пробрасывать *args/**kwargs: сигнатуры меняются между патчами игры.
  4. Маркер на обёртке: не патчить дважды (модуль могут импортировать повторно).
  5. Уметь снять патч в fini() — и только если ПОСЛЕ вас никто не патчил
     (иначе вы затрёте чужую обёртку).

ГДЕ ЛОМАЕТСЯ: если целевого метода нет (игра обновилась) — НЕ падаем,
а пишем warning и выключаем эту часть мода. Мод без одной функции лучше,
чем закрытый клиент.

ПРИМЕР ЦЕЛИ ниже — Avatar.PlayerAvatar.showShotResults (проверено:
scripts/client/Avatar.py, метод существует, вызывается движком со списком
упакованных результатов; формат элемента подтверждён по Avatar.py:1477-1479:
vehicleID = r & 0xFFFFFFFF, flags = (r >> 32) & 0xFFFFFFFF). Мы только СЧИТАЕМ
свои пробития — эту же информацию игрок видит индикатором попаданий.
"""
import logging
import traceback

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

_PATCH_MARKER = '_my_mod_patched'      # ПОДСТАВЬ: уникальный для вашего мода

# (класс, имя метода) -> оригинал, чтобы снять патч
_installed = {}

_own_piercing_hits = 0


def patch_method(cls, method_name, make_wrapper):
    """Универсальная установка патча.

    cls          -- класс, например Avatar.PlayerAvatar;
    method_name  -- строка с именем метода;
    make_wrapper -- функция(original) -> wrapper(self, *args, **kwargs).
    """
    try:
        original = getattr(cls, method_name, None)
        if original is None:
            _logger.warning('%s %s.%s not found, patch skipped',
                            LOG_PREFIX, cls.__name__, method_name)
            return False
        if getattr(original, _PATCH_MARKER, False):
            _logger.info('%s %s.%s is already patched by us',
                         LOG_PREFIX, cls.__name__, method_name)
            return False
        wrapper = make_wrapper(original)
        setattr(wrapper, _PATCH_MARKER, True)
        wrapper.__name__ = method_name
        setattr(cls, method_name, wrapper)
        _installed[(cls, method_name)] = original
        _logger.info('%s patched %s.%s', LOG_PREFIX, cls.__name__, method_name)
        return True
    except Exception:
        _logger.error('%s cannot patch %s:\n%s',
                      LOG_PREFIX, method_name, traceback.format_exc())
        return False


def unpatch_all():
    """Звать из fini(). Снимаем ТОЛЬКО если сверху всё ещё наша обёртка."""
    for key in list(_installed.keys()):
        cls, method_name = key
        original = _installed.pop(key)
        try:
            current = getattr(cls, method_name, None)
            if current is not None and getattr(current, _PATCH_MARKER, False):
                setattr(cls, method_name, original)
            else:
                # Поверх нас лёг чужой мод. Снимать нельзя: сломаем его.
                _logger.warning('%s %s.%s was re-patched by another mod, left as is',
                                LOG_PREFIX, cls.__name__, method_name)
        except Exception:
            _logger.error('%s cannot unpatch %s:\n%s',
                          LOG_PREFIX, method_name, traceback.format_exc())


# =============================================================================
#  КОНКРЕТНЫЙ ПРИМЕР
# =============================================================================
def _make_shot_results_wrapper(original):

    def wrapper(self, results, *args, **kwargs):
        global _own_piercing_hits
        # --- НАША ЛОГИКА: полностью изолированный try -----------------------
        try:
            from constants import VEHICLE_HIT_FLAGS
            for packed in results:
                # ">>" имеет приоритет выше "&", скобки не нужны.
                # Пишем 0xFFFFFFFF, а не 4294967295L: суффикс L сделал бы файл
                # непарсибельным для ast.parse питона 3 (то есть для
                # tools/check_py2_syntax.py и tools/py27c.py).
                flags = packed >> 32 & 0xFFFFFFFF
                if flags & VEHICLE_HIT_FLAGS.IS_ANY_PIERCING_MASK:
                    _own_piercing_hits += 1
        except Exception:
            try:
                _logger.error('%s wrapper logic failed:\n%s',
                              LOG_PREFIX, traceback.format_exc())
            except Exception:
                pass
        # --- ОРИГИНАЛ ВЫЗЫВАЕТСЯ ВСЕГДА -------------------------------------
        return original(self, results, *args, **kwargs)

    return wrapper


def install():
    """Звать из init()."""
    try:
        import Avatar
        patch_method(Avatar.PlayerAvatar, 'showShotResults', _make_shot_results_wrapper)
    except Exception:
        _logger.error('%s install failed:\n%s', LOG_PREFIX, traceback.format_exc())


def get_own_piercing_hits():
    return _own_piercing_hits


def reset_counters():
    global _own_piercing_hits
    _own_piercing_hits = 0
