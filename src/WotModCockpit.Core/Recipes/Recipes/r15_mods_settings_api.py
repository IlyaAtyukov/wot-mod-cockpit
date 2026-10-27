# -*- coding: utf-8 -*-
"""РЕЦЕПТ 15. Настройки мода через ModsSettingsAPI (окно с галочками в ангаре).

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
Точка входа mod_<имя>.py — стаб, который только пробрасывает вызовы сюда;
кода рецепта в ней быть не должно.

ВНЕШНЯЯ ЗАВИСИМОСТЬ: izeberg.modssettingsapi (у пользователя стоит 1.6.4).
Мод обязан работать и без неё — тогда настройки берутся из JSON (рецепт 09).

ПРОВЕРЕНО декомпиляцией
izeberg.modssettingsapi_1.6.4/res/scripts/client/gui/modsSettingsApi/:
    __init__.py: g_modsSettingsApi = ModsSettingsApi()
    api.py:
        setModTemplate(linkage, template, callback, buttonHandler=None) -> dict
            кладёт шаблон в state['templates'][linkage],
            settings = getSettingsFromTemplate(template),
            подписывает callback на onSettingsChanged,
            ВОЗВРАЩАЕТ текущий словарь настроек;
        getModSettings(linkage, template) -> dict | None
        registerCallback(linkage, callback, buttonHandler=None)
        saveModData(linkage, version, data) / getModData(linkage, version, default)
        updateModSettings(linkage, newSettings) -> зовёт onSettingsChanged(linkage, newSettings)
    onSettingsChanged — Event.Event(), сигнатура callback: (linkage, newSettings)
    getSettingsFromTemplate читает 'enabled' и колонки 'column1'/'column2',
    из каждого компонента берёт пару varName -> value.
    compareTemplates: если в обоих шаблонах есть 'settingsVersion', сравнивает
    его как число, иначе сравнивает JSON целиком.
    Ключи шаблона, которые понимает SWF (вытащены строками из
    modsSettingsWindow.swf): modDisplayName, enabled, column1, column2, linkage.
    templates.py: createLabel/createCheckbox/createSlider/createDropdown/
    createInput/createNumericStepper/createHotkey/createColorChoice/
    createRadioButtonGroup/createRangeSlider/createEmpty.
    COMPONENT_TYPE: Empty, Label, CheckBox, RadioButtonGroup, Dropdown, Slider,
    TextInput, NumericStepper, HotKey, ColorChoice, RangeSlider.

ПОЧЕМУ У ЭТОГО РЕЦЕПТА НЕТ unregister() — единственного во всём каталоге.
Отписаться от onSettingsChanged НЕЧЕМ: в ModsSettingsAPI 1.6.4 подписка есть
(api.py:108 внутри setModTemplate и api.py:129 внутри registerCallback, оба —
`self.onSettingsChanged += callback`), а метода снятия обработчика в классе
ModsSettingsApi НЕТ вообще — в его публичной поверхности только setModTemplate,
getModSettings, registerCallback, getModData, saveModData, updateModSettings,
checkKeyset, compareTemplates, getSettingsFromTemplate, getSettingsFromColumn,
generateSettingsData и хоткейные onHotkey*. Ни unregisterCallback, ни доступа
к самому Event для `-=` наружу не отдано.
Следствия, с которыми надо жить:
  * _on_settings_changed переживёт fini() вашего мода и может быть вызван после
    него. Поэтому он обязан быть идемпотентным и не падать на разобранном
    состоянии — у нас он только переливает значения в _settings;
  * register() защищён флагом _registered именно поэтому: повторный вызов
    добавил бы ВТОРУЮ подписку тем же callback'ом, а снять её нечем.
Во всех остальных подписочных рецептах (R05, R06, R08, R13, R14, R16) пара
subscribe/unsubscribe есть — здесь её нет не по забывчивости.

ГДЕ ЛОМАЕТСЯ:
  * ВСЕГДА поднимайте settingsVersion, когда меняете состав компонентов.
    Иначе compareTemplates сравнит JSON, увидит различие и СБРОСИТ настройки
    игрока к значениям из нового шаблона;
  * callback зовётся при КАЖДОМ применении настроек, включая первое —
    он обязан быть идемпотентным и безопасным (это Event.Event());
  * setModTemplate может вернуть None, если внутри API случилось исключение
    (он ловит всё и логирует). Проверяйте результат;
  * linkage — ваш уникальный ключ, менять его нельзя: настройки привязаны к нему;
  * регистрируйтесь по onAccountShowGUI, а не в init(): порядок загрузки модов
    определяется сортировкой имён mod_*.pyc.
"""
import logging
import traceback

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

LINKAGE = 'ru.local.my_mod'            # ПОДСТАВЬ: уникальный и НЕИЗМЕННЫЙ
SETTINGS_VERSION = 1                   # ПОДСТАВЬ: поднимать при смене состава

_settings = {
    'enabled': True,
    'showGreeting': True,
    'delaySec': 3,
    'greetingText': u'Привет, танкист!',
}

_registered = False


def _build_template():
    """Шаблон окна настроек. ПОДСТАВЬ свои компоненты."""
    from gui.modsSettingsApi import templates
    return {
        'modDisplayName': u'My Mod',
        'settingsVersion': SETTINGS_VERSION,
        'enabled': _settings['enabled'],
        'column1': [
            templates.createLabel(u'Основное'),
            templates.createCheckbox(
                u'Показывать приветствие', 'showGreeting',
                _settings['showGreeting'],
                tooltip=u'{HEADER}Приветствие{/HEADER}{BODY}Системное сообщение при входе в ангар{/BODY}'),
            templates.createNumericStepper(
                u'Задержка, с', 'delaySec', _settings['delaySec'], 0, 10, 1),
        ],
        'column2': [
            templates.createLabel(u'Текст'),
            templates.createInput(u'Текст приветствия', 'greetingText',
                                  _settings['greetingText']),
        ],
    }


def _on_settings_changed(linkage, new_settings):
    """Игрок нажал «Применить». Event.Event() -> обязателен try/except."""
    try:
        if linkage != LINKAGE:
            return          # событие широковещательное: приходит от всех модов
        _apply(new_settings)
        _logger.info('%s settings applied: %s', LOG_PREFIX, _settings)
    except Exception:
        _logger.error('%s onSettingsChanged failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())


def _apply(new_settings):
    """Перелить значения в свой словарь, не теряя дефолтов при пропаже ключа."""
    if not isinstance(new_settings, dict):
        return
    for key in list(_settings.keys()):
        if key in new_settings:
            _settings[key] = new_settings[key]


def register():
    """Звать из onAccountShowGUI. True — окно настроек доступно."""
    global _registered
    if _registered:
        return True
    try:
        from gui.modsSettingsApi import g_modsSettingsApi
    except ImportError:
        _logger.info('%s ModsSettingsAPI is not installed, using JSON config',
                     LOG_PREFIX)
        return False
    except Exception:
        _logger.error('%s ModsSettingsAPI import failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())
        return False
    try:
        template = _build_template()
        saved = g_modsSettingsApi.setModTemplate(LINKAGE, template, _on_settings_changed)
        if saved is None:
            # API проглотило исключение внутри себя — работаем на дефолтах,
            # но окно уже могло зарегистрироваться.
            _logger.warning('%s setModTemplate returned None', LOG_PREFIX)
        else:
            _apply(saved)
        _registered = True
        _logger.info('%s registered in ModsSettingsAPI', LOG_PREFIX)
        return True
    except Exception:
        _logger.error('%s setModTemplate failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())
        return False


def get(key, default=None):
    """Чтение настройки. Работает и когда ModsSettingsAPI не установлен."""
    try:
        return _settings.get(key, default)
    except Exception:
        return default


def is_enabled():
    return bool(_settings.get('enabled', True))
