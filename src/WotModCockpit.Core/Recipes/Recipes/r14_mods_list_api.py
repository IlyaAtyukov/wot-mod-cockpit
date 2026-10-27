# -*- coding: utf-8 -*-
"""РЕЦЕПТ 14. Регистрация мода в ModsListAPI (кнопка «Моды» в ангаре).

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
Точка входа mod_<имя>.py — стаб, который только пробрасывает вызовы сюда;
кода рецепта в ней быть не должно.

ЭТО ВНЕШНЯЯ ЗАВИСИМОСТЬ: me.poliroid.modslistapi. Её может не быть у игрока —
мод обязан работать и без неё.

ПРОВЕРЕНО декомпиляцией установленного пакета
me.poliroid.modslistapi_1.6.01/res/scripts/client/gui/modsListApi/:
    __init__.py -> g_modsListApi = ModsListApiRepresentation()
    controller.py -> ApiLogicController.addModification(
        self, id, name=None, description=None, icon=None, enabled=None,
        login=None, lobby=None, callback=None)
    Все шесть параметров, кроме icon, ОБЯЗАТЕЛЬНЫ: если любой из
    name/description/enabled/login/lobby/callback равен None, метод пишет
    'method @addModification required mandatory parameters' и НИЧЕГО не делает.
    Есть также updateModification(...), removeModification(id),
    alertModification(id), clearModificationAlert(id) — те же имена параметров.

ГДЕ ЛОМАЕТСЯ:
  * порядок init() модов определяется сортировкой имён mod_*.pyc. Если ваш
    mod_a... грузится раньше mod_modslistapi..., импорт в init() упадёт
    ImportError. Регистрируйтесь по onAccountShowGUI, а не в init();
  * повторный addModification с тем же id сам уходит в updateModification —
    это безопасно;
  * callback вызывается при клике по строке мода в попапе. Исключение оттуда
    попадёт в код ModsListAPI. Оборачивайте.
"""
import logging
import traceback

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

# ПОДСТАВЬ паспорт мода
MOD_ID = 'ru.local.my_mod'
MOD_NAME = u'My Mod'
MOD_DESCRIPTION = u'Что делает мод — одна-две строки для попапа.'
# Путь во VFS игры. None -> ModsListAPI подставит иконку по умолчанию.
MOD_ICON = None

_registered = False


def _on_click(*args, **kwargs):
    """Клик по строке мода в списке. ПОДСТАВЬ свою реакцию (открыть настройки)."""
    try:
        _logger.info('%s clicked in mods list', LOG_PREFIX)
        # например: from gui.modsSettingsApi import g_modsSettingsApi ... (рецепт 15)
    except Exception:
        _logger.error('%s mods list callback failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())


def register(enabled=True):
    """Звать из onAccountShowGUI. Возвращает True, если API нашлось."""
    global _registered
    if _registered:
        return True
    try:
        from gui.modsListApi import g_modsListApi
    except ImportError:
        # ModsListAPI не установлен — это НОРМАЛЬНАЯ ситуация, не ошибка.
        _logger.info('%s ModsListAPI is not installed, skipping registration', LOG_PREFIX)
        return False
    except Exception:
        _logger.error('%s ModsListAPI import failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())
        return False
    try:
        g_modsListApi.addModification(
            id=MOD_ID,
            name=MOD_NAME,
            description=MOD_DESCRIPTION,
            icon=MOD_ICON,
            enabled=bool(enabled),
            login=False,          # показывать на экране логина
            lobby=True,           # показывать в ангаре
            callback=_on_click)
        _registered = True
        _logger.info('%s registered in ModsListAPI', LOG_PREFIX)
        return True
    except Exception:
        _logger.error('%s addModification failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())
        return False


def set_enabled(enabled):
    """Перерисовать переключатель в списке (например после смены настроек)."""
    if not _registered:
        return
    try:
        from gui.modsListApi import g_modsListApi
        g_modsListApi.updateModification(
            id=MOD_ID,
            name=MOD_NAME,
            description=MOD_DESCRIPTION,
            icon=MOD_ICON,
            enabled=bool(enabled),
            login=False,
            lobby=True,
            callback=_on_click)
    except Exception:
        _logger.error('%s updateModification failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())


def unregister():
    """Звать из fini(). Без этого строка мода останется висеть в списке."""
    global _registered
    if not _registered:
        return
    try:
        from gui.modsListApi import g_modsListApi
        g_modsListApi.removeModification(MOD_ID)
    except Exception:
        pass
    _registered = False
