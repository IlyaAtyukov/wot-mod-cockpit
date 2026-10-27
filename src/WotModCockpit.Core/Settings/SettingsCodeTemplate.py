# -*- coding: utf-8 -*-
"""Настройки мода. Дизайнер кокпита создал этот файл ОДИН РАЗ и больше его не трогает.

ЧТО ГДЕ ЛЕЖИТ:
  * СОСТАВ ФОРМЫ (какие галочки и ползунки видит игрок) — в settings_schema.py.
    Тот файл дизайнер переписывает целиком при каждом сохранении формы, править
    его руками бессмысленно: правки затрутся.
  * ЗНАЧЕНИЯ игрока — в mods/configs/my_mod/settings.json. Каталог тот же, что
    у конфига мода (рецепт 09), а ФАЙЛ ОТДЕЛЬНЫЙ, и это принципиально: config.json
    держит параметры, которые задаёт разработчик и сливает со своими дефолтами,
    а settings.json переписывается ЦЕЛИКОМ при каждом «Применить» игрока.
    Один файл на двоих означал бы два источника умолчаний и потерю чужих ключей.
  * ЭТОТ файл — ваш. Дописывайте сюда что угодно, перегенерация его не тронет.

ПОЧЕМУ ЗНАЧЕНИЯ ДУБЛИРУЮТСЯ В СВОЙ JSON, А НЕ ЖИВУТ ТОЛЬКО В ModsSettingsAPI.
ModsSettingsAPI НИКОГДА не сливает старые значения игрока в новый шаблон:
api.setModTemplate при любой принятой правке делает
settings = getSettingsFromTemplate(template), то есть сбрасывает ВСЕ значения
на те, что стоят в шаблоне. Поэтому шаблон здесь строится ОТ СОХРАНЁННЫХ
значений (build_template(_values)), а не от заводских: сброс возвращает игрока
к его же выбору, и только новые контролы получают заводское значение.
Побочная выгода — мод полностью работоспособен, когда ModsSettingsAPI не
установлен: окна настроек нет, а значения читаются из своего JSON.

ЧТО ЗВАТЬ ИЗ ТЕЛА МОДА:
    from . import settings
    settings.register()                  # из onAccountShowGUI, НЕ из init()
    settings.get('delaySec')             # чтение значения
    settings.subscribe(_on_settings)     # уведомление о нажатии «Применить»
    settings.is_hotkey_down('actionKey')  # нажато ли сочетание контрола HotKey

ПОЧЕМУ register() ЗОВЁТСЯ ИМЕННО ИЗ onAccountShowGUI:
  * не раньше: порядок init() у модов задаётся сортировкой имён mod_*.pyc и
    меняется от установки любого другого мода;
  * не позже: при открытии окна настроек ModsSettingsAPI зовёт clearState(),
    который СТИРАЕТ шаблон и настройки всех модов, не зарегистрировавшихся
    в этой сессии.

ПОЧЕМУ ЗДЕСЬ НЕТ unregister(): в ModsSettingsApi 1.6.4 подписка есть
(self.onSettingsChanged += callback), а метода снятия обработчика в классе нет
вообще. _on_settings_changed переживёт fini() вашего мода и может быть вызван
после него — поэтому он обязан быть идемпотентным и не падать. Ровно поэтому же
register() защищён флагом _registered: повторный вызов добавил бы ВТОРУЮ
подписку, а снять её нечем.
"""
import json
import logging
import os
import traceback

import settings_schema

LOG_PREFIX = '[MyMod/settings]'
_logger = logging.getLogger('MyMod')

# Путь к своему конфигу ОТНОСИТЕЛЬНО корня игры. mods/configs/<мод>/ — соглашение
# модпаков (так делает и сам ModsSettingsAPI: _constants.USER_SETTINGS_PATH),
# а не требование движка: файл читается обычным open().
CONFIG_RELATIVE_PATH = os.path.join('mods', 'configs', 'my_mod', 'settings.json')

# Текущие значения. Заводские — единственным списком в settings_schema.DEFAULTS,
# второго источника правды нет.
_values = dict(settings_schema.DEFAULTS)
_registered = False
_listeners = []


def _game_root_candidates():
    """Корень установленной игры: клиент стартует из него, но полагаться на cwd нельзя."""
    roots = []
    try:
        roots.append(os.getcwd())
    except Exception:
        pass
    try:
        import ResMgr
        app = ResMgr.appDirectory()
        if app:
            # appDirectory() — это каталог Tanki.exe (…/WoT/win64), а не корень игры.
            roots.append(app)
            roots.append(os.path.dirname(app.rstrip('/\\')))
    except Exception:
        pass
    if not roots:
        roots.append('.')
    return roots


def config_path():
    """Полный путь к settings.json: первый кандидат, где файл РЕАЛЬНО есть."""
    roots = _game_root_candidates()
    for root in roots:
        try:
            path = os.path.join(root, CONFIG_RELATIVE_PATH)
            if os.path.isfile(path):
                return path
        except Exception:
            continue
    return os.path.join(roots[0], CONFIG_RELATIVE_PATH)


def _load():
    """Прочитать сохранённые значения. Любая беда -> заводские и строка в лог."""
    path = config_path()
    if not os.path.isfile(path):
        _logger.info('%s no saved settings at %s, using defaults', LOG_PREFIX, path)
        return
    try:
        handle = open(path, 'rb')
        try:
            raw = handle.read()
        finally:
            handle.close()
        # utf-8-sig: блокнот Windows пишет BOM, и json.loads на нём падает.
        saved = json.loads(raw.decode('utf-8-sig', 'replace'))
        if not isinstance(saved, dict):
            raise ValueError('settings root must be a JSON object')
        _apply(saved)
        _logger.info('%s settings loaded from %s', LOG_PREFIX, path)
    except Exception:
        _logger.error('%s broken settings at %s, using defaults:\n%s',
                      LOG_PREFIX, path, traceback.format_exc())


def _save():
    """Записать значения в свой JSON. Ошибка записи не должна ронять мод."""
    path = config_path()
    try:
        folder = os.path.dirname(path)
        if folder and not os.path.isdir(folder):
            os.makedirs(folder)
        handle = open(path, 'wb')
        try:
            handle.write(json.dumps(_values, ensure_ascii=False, indent=4,
                                    sort_keys=True).encode('utf-8'))
        finally:
            handle.close()
    except Exception:
        _logger.error('%s cannot save settings to %s:\n%s',
                      LOG_PREFIX, path, traceback.format_exc())


def _apply(new_settings):
    """Перелить пришедшие значения к себе, не теряя заводских.

    Ключа может НЕ БЫТЬ: если состав контролов изменили, а SETTINGS_VERSION не
    подняли, ModsSettingsAPI покажет СТАРУЮ форму и вернёт словарь по старому
    шаблону — без новых ключей. Поэтому идём по своим ключам, а не по чужим.
    """
    if not isinstance(new_settings, dict):
        return
    for key in list(_values.keys()):
        if key in new_settings:
            _values[key] = new_settings[key]


def _on_settings_changed(linkage, new_settings):
    """Игрок нажал «Применить» или «OK».

    onSettingsChanged — это Event.Event(), рассылка ШИРОКОВЕЩАТЕЛЬНАЯ: сюда
    приходят события ВСЕХ модов, поэтому первым делом фильтр по linkage.
    """
    try:
        if linkage != settings_schema.LINKAGE:
            return
        _apply(new_settings)
        _save()
        for listener in tuple(_listeners):
            try:
                listener(dict(_values))
            except Exception:
                _logger.error('%s settings listener failed:\n%s',
                              LOG_PREFIX, traceback.format_exc())
    except Exception:
        _logger.error('%s onSettingsChanged failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())


def subscribe(listener):
    """Звать listener(values) после каждого применения настроек."""
    if listener not in _listeners:
        _listeners.append(listener)


def unsubscribe(listener):
    """Снять подписку, поставленную subscribe(). Звать из fini()."""
    if listener in _listeners:
        _listeners.remove(listener)


def register():
    """Зарегистрировать окно настроек. True — окно доступно игроку.

    False — ModsSettingsAPI не установлен или сломался. Это ШТАТНЫЙ режим:
    значения при этом берутся из своего JSON, мод работает.
    """
    global _registered
    if _registered:
        return True

    _load()

    # WOT018: импорт чужого мода обязан быть обёрнут. Имя объявлено ДО try,
    # иначе после ImportError его просто не будет в области видимости.
    g_modsSettingsApi = None
    try:
        from gui.modsSettingsApi import g_modsSettingsApi
    except ImportError:
        _logger.info('%s ModsSettingsAPI is not installed, working on settings.json only',
                     LOG_PREFIX)
        return False
    except Exception:
        _logger.error('%s ModsSettingsAPI import failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())
        return False

    if g_modsSettingsApi is None:
        return False

    try:
        template = settings_schema.build_template(_values)
        saved = g_modsSettingsApi.setModTemplate(
            settings_schema.LINKAGE, template, _on_settings_changed)
        if saved is None:
            # API ловит внутри себя всё и логирует; None означает, что там
            # случилось исключение. Работаем на своих значениях.
            _logger.warning('%s setModTemplate returned None', LOG_PREFIX)
        else:
            # ВАЖНО: saved — ТОТ ЖЕ объект dict, что лежит в состоянии API.
            # Мутировать его нельзя, поэтому только копируем значения к себе.
            _apply(saved)
            _save()
        _registered = True
        _logger.info('%s registered in ModsSettingsAPI', LOG_PREFIX)
        return True
    except Exception:
        _logger.error('%s setModTemplate failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())
        return False


def get(name, default=None):
    """Чтение настройки. Работает и без установленного ModsSettingsAPI."""
    try:
        if name in _values:
            return _values[name]
        if name in settings_schema.DEFAULTS:
            return settings_schema.DEFAULTS[name]
    except Exception:
        pass
    return default


def values():
    """КОПИЯ всех значений. Именно копия: словарь внутри живёт своей жизнью."""
    return dict(_values)


def is_enabled():
    """Тумблер мода в окне настроек. True, если тумблера в форме нет."""
    return bool(get('enabled', True))


def is_hotkey_down(name):
    """Нажато ли сочетание, назначенное контролу HotKey.

    Помощник лежит здесь БЕЗУСЛОВНО, а не появляется вместе с первой горячей
    клавишей в форме: этот файл создаётся ОДИН раз и дальше принадлежит вам,
    поэтому контрол, добавленный завтра, помощника уже не получил бы никогда.

    Значение настройки HotKey — это СПИСОК кодов клавиш, и сам по себе он
    ничего не проверяет. checkKeyset у ModsSettingsAPI разворачивает спецкоды
    -1 ALT / -2 CTRL / -3 SHIFT в пары «левая-правая» и требует, чтобы нажаты
    были ВСЕ коды сразу. Без установленного ModsSettingsAPI делаем то же самое
    сами через BigWorld.isKeyDown, раскрывая спецкоды руками, — иначе мод с
    горячей клавишей переставал бы работать у игрока без чужого мода.
    """
    keys = get(name) or []
    if not keys:
        return False
    try:
        from gui.modsSettingsApi import g_modsSettingsApi
        return bool(g_modsSettingsApi.checkKeyset(keys))
    except ImportError:
        pass
    except Exception:
        _logger.error('%s checkKeyset failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())
        return False
    try:
        import BigWorld
        pairs = {-1: (56, 184), -2: (29, 157), -3: (42, 54)}
        for code in keys:
            if code in pairs:
                left, right = pairs[code]
                if not (BigWorld.isKeyDown(left) or BigWorld.isKeyDown(right)):
                    return False
            elif not BigWorld.isKeyDown(code):
                return False
        return True
    except Exception:
        _logger.error('%s isKeyDown failed:\n%s',
                      LOG_PREFIX, traceback.format_exc())
        return False
