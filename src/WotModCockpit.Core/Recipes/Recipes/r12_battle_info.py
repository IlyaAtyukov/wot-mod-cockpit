# -*- coding: utf-8 -*-
"""РЕЦЕПТ 12. Информация о текущем бою: карта, режим, состав команд.

КУДА КЛАСТЬ: тело мода — res/scripts/client/gui/mods/<пакет>/main.py.
Точка входа mod_<имя>.py — стаб, который только пробрасывает вызовы сюда;
кода рецепта в ней быть не должно.

ТОЛЬКО ТО, ЧТО ИГРОК И ТАК ВИДИТ на загрузочном экране и по Tab.
Никаких засветок, позиций и перезарядки противника.

ПРОВЕРЕНО:
  scripts/client/Avatar.py:286  self.arena = ClientArena.ClientArena(...)
  scripts/client_common/ClientArena.py:
      self.arenaType = ArenaType.g_cache.get(arenaTypeID, None)
      self.bonusType = arenaBonusType
      self.guiType   = arenaGuiType
      self.vehicles  -- dict {vehicleID: {'team': int, 'name': str,
                                          'vehicleType': ..., 'isAlive': ...}}
      self.period    -- фаза боя
  Avatar.py:1485  self.team == arenaVehicles[vehicleID]['team']  -- у игрока есть .team
  arenaType.name  -- НА КЛИЕНТЕ УЖЕ ЛОКАЛИЗОВАНО. Проверено:
                     common/ArenaType.py:302 --
                       if IS_CLIENT or IS_WEB:
                           cfg['name'] = i18n.makeString(_readString('name', ...))
                     то есть ключ '#arenas:100_thepit/name' разворачивается
                     ещё при сборке кэша. makeString ниже оставлен страховкой
                     на случай нелокализованного значения (от обычной строки
                     он возвращает её же) — вызывать его не обязательно.
  arenaType.geometryName -- техническое имя карты
  constants.ARENA_GUI_TYPE_LABEL.LABELS -- словарь guiType -> метка режима

ГДЕ ЛОМАЕТСЯ:
  * BigWorld.player() в ангаре — это Account, у него НЕТ .arena. Всегда
    проверяйте наличие атрибута, а не «мы вроде в бою»;
  * сразу по onAvatarBecomePlayer список arena.vehicles ещё НЕПОЛНЫЙ:
    техника доезжает событиями VEHICLE_ADDED. Читайте по onAvatarReady,
    а лучше с задержкой 2 с (см. рецепт 07);
  * arenaType может быть None (неизвестный ID карты после патча);
  * в реплеях всё это тоже работает — если не нужно, проверяйте
    BattleReplay.g_replayCtrl.isPlaying.
"""
import logging
import traceback

import BigWorld

LOG_PREFIX = '[MyMod]'                 # ПОДСТАВЬ
_logger = logging.getLogger('MyMod')   # ПОДСТАВЬ


def get_arena():
    """ClientArena текущего боя или None."""
    try:
        player = BigWorld.player()
        if player is None:
            return None
        return getattr(player, 'arena', None)
    except Exception:
        return None


def is_replay():
    """True, если сейчас проигрывается реплей."""
    try:
        import BattleReplay
        return bool(BattleReplay.g_replayCtrl.isPlaying)
    except Exception:
        return False


def collect_battle_info():
    """Словарь с описанием боя. Всегда возвращает dict, никогда не бросает.

    Ключи: map (лок. имя), geometry (тех. имя), battle_type (метка режима),
    gui_type (int), bonus_type (int), allies (без самого игрока), enemies,
    my_team, is_replay.
    """
    info = {
        'map': u'?',
        'geometry': u'?',
        'battle_type': u'?',
        'gui_type': 0,
        'bonus_type': 0,
        'allies': 0,
        'enemies': 0,
        'my_team': 0,
        'is_replay': is_replay(),
    }
    arena = get_arena()
    if arena is None:
        return info

    # --- карта ---------------------------------------------------------------
    try:
        arena_type = getattr(arena, 'arenaType', None)
        if arena_type is not None:
            try:
                from helpers import i18n
                info['map'] = i18n.makeString(arena_type.name)
            except Exception:
                info['map'] = unicode(getattr(arena_type, 'name', u'?'))
            info['geometry'] = unicode(getattr(arena_type, 'geometryName', u'?'))
    except Exception:
        _logger.error('%s cannot read arenaType:\n%s', LOG_PREFIX, traceback.format_exc())

    # --- режим ---------------------------------------------------------------
    try:
        from constants import ARENA_GUI_TYPE_LABEL
        gui_type = getattr(arena, 'guiType', 0)
        info['gui_type'] = gui_type
        info['bonus_type'] = getattr(arena, 'bonusType', 0)
        info['battle_type'] = unicode(ARENA_GUI_TYPE_LABEL.LABELS.get(gui_type, u'special'))
    except Exception:
        pass

    # --- состав команд (то же, что показывает Tab) ---------------------------
    try:
        player = BigWorld.player()
        my_team = getattr(player, 'team', 0)
        my_vehicle_id = getattr(player, 'playerVehicleID', None)
        info['my_team'] = my_team
        allies = 0
        enemies = 0
        vehicles = getattr(arena, 'vehicles', None) or {}
        for vehicle_id, data in vehicles.items():
            try:
                if data.get('team') == my_team:
                    if vehicle_id != my_vehicle_id:
                        allies += 1
                else:
                    enemies += 1
            except Exception:
                continue
        info['allies'] = allies
        info['enemies'] = enemies
    except Exception:
        _logger.error('%s cannot read arena.vehicles:\n%s',
                      LOG_PREFIX, traceback.format_exc())
    return info


def format_battle_info():
    """Готовая строка для боевого лога (рецепт 04)."""
    info = collect_battle_info()
    return u'Карта «%s», режим «%s», союзников %d, противников %d' % (
        info['map'], info['battle_type'], info['allies'], info['enemies'])
