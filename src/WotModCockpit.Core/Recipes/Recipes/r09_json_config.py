# -*- coding: utf-8 -*-
"""РЕЦЕПТ 09. Свой JSON-конфиг в mods/configs/<mod>/config.json.

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
НЕ в точку входа mod_<имя>.py: её build.py компилирует в байткод сам,
встроенным tools/py27c.py, а тот не поддерживает замыкания (cell/free
variables). Этот рецепт замыкание использует, и сборка упадёт с
Py27CompileError, не сославшись на рецепт. Тело мода едет в .mtmod
обычным .py и компилируется уже клиентом — там замыкания законны.

mods/configs/<имя>/ — соглашение модпаков (так делают pmod, крупные модпаки,
ModsSettingsAPI: scripts/client/gui/modsSettingsApi/_constants.py ->
USER_SETTINGS_PATH = os.path.join('mods', 'configs', 'modsSettingsApi.json')),
а НЕ требование движка. Никакого API «дай мне мой конфиг» в клиенте нет:
файл читается обычным open() относительно корня игры.

ГДЕ ЛОМАЕТСЯ:
  * корень игры. Клиент стартует из корня (BattleReplay.py хранит
    __replayDir='./replays', официальный мод Lesta делает
    os.path.join(os.getcwd(), 'replays')), НО ResMgr.appDirectory() — это
    каталог Tanki.exe, то есть .../WoT/win64/. Проверяем оба и берём тот,
    где есть подкаталог mods/;
  * битый JSON. Игрок правит файл в блокноте и ставит лишнюю запятую.
    Мод ОБЯЗАН пережить это на дефолтах и написать одну строку в лог;
  * BOM. Блокнот Windows пишет UTF-8 с BOM, json.loads на нём падает —
    срезаем 'utf-8-sig';
  * json.loads в py2 отдаёт unicode-ключи. Для .get() это неважно,
    но если сравниваете ключи со str-константами — помните об этом.
"""
import json
import logging
import os
import traceback

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ

# ПОДСТАВЬ: имя каталога конфигов (обычно = имя пакета мода).
CONFIG_RELATIVE_PATH = os.path.join('mods', 'configs', 'my_mod', 'config.json')

# ПОДСТАВЬ: все ключи со значениями по умолчанию.
# Мод ОБЯЗАН работать вообще без файла — это его рабочий режим по умолчанию.
DEFAULT_CONFIG = {
    'enabled': True,
    'debug': False,
    'greeting': {
        'enabled': True,
        'delay_sec': 3.0,
        'text': u'{mod} загружен',
    },
}

_config = None


def _game_root_candidates():
    """Возможные корни установленной игры, в порядке правдоподобия."""
    candidates = []

    def _add(value):
        if not value:
            return
        try:
            value = value.rstrip('/\\')
        except Exception:
            return
        if value and value not in candidates:
            candidates.append(value)

    try:
        _add(os.getcwd())
    except Exception:
        pass
    try:
        import ResMgr
        app_dir = ResMgr.appDirectory()
        _add(app_dir)
        if app_dir:
            _add(os.path.dirname(app_dir.rstrip('/\\')))
    except Exception:
        pass
    if not candidates:
        candidates.append('.')
    return candidates


def config_path():
    """Путь к config.json: первый кандидат, где файл РЕАЛЬНО есть."""
    roots = _game_root_candidates()
    for root in roots:
        try:
            path = os.path.join(root, CONFIG_RELATIVE_PATH)
            if os.path.isfile(path):
                return path
        except Exception:
            continue
    return os.path.join(roots[0], CONFIG_RELATIVE_PATH)


def _deep_merge(base, override):
    """Рекурсивно наложить override на base, не мутируя base.

    Словари сливаются по ключам, остальное заменяется целиком. Так добавление
    нового ключа в DEFAULT_CONFIG не ломает старые пользовательские файлы.
    """
    result = dict(base)
    if not isinstance(override, dict):
        return result
    for key, value in override.items():
        if isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = _deep_merge(result[key], value)
        else:
            result[key] = value
    return result


def load():
    """Прочитать конфиг. Любая ошибка -> дефолты + одна строка в лог."""
    global _config
    _config = dict(DEFAULT_CONFIG)
    path = config_path()
    if not os.path.isfile(path):
        _logger.info('%s config not found at %s, using defaults', LOG_PREFIX, path)
        return _config
    try:
        handle = open(path, 'rb')
        try:
            raw = handle.read()
        finally:
            handle.close()
        # BOM + любые кодировки: декодируем сами, json.loads даём unicode.
        text = raw.decode('utf-8-sig', 'replace')
        user = json.loads(text)
        if not isinstance(user, dict):
            raise ValueError('config root must be a JSON object')
        _config = _deep_merge(DEFAULT_CONFIG, user)
        _logger.info('%s config loaded from %s', LOG_PREFIX, path)
    except Exception:
        _config = dict(DEFAULT_CONFIG)
        _logger.error('%s broken config at %s, falling back to defaults:\n%s',
                      LOG_PREFIX, path, traceback.format_exc())
    return _config


def get(section, key, default=None):
    """cfg('greeting', 'delay_sec', 3.0) — чтение с двойным дефолтом."""
    if _config is None:
        load()
    try:
        block = _config.get(section)
        if isinstance(block, dict) and key in block:
            return block[key]
        block = DEFAULT_CONFIG.get(section)
        if isinstance(block, dict) and key in block:
            return block[key]
    except Exception:
        pass
    return default


def get_root(key, default=None):
    """Чтение ключа верхнего уровня, например 'enabled'."""
    if _config is None:
        load()
    try:
        if key in _config:
            return _config[key]
        if key in DEFAULT_CONFIG:
            return DEFAULT_CONFIG[key]
    except Exception:
        pass
    return default
