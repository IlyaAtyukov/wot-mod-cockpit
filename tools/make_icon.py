#!/usr/bin/env python3
# -*- coding: utf-8 -*-
u"""Генератор ИКОНКИ кокпита: .ico для exe и панели задач плюс .png для документации.

ПОЧЕМУ ГЕНЕРАТОР, А НЕ ФАЙЛ В РЕПОЗИТОРИИ. Иконка — часть облика приложения, и
её приходится трогать: подправить цвет под тему, добавить размер под новый DPI.
Двоичный файл, который никто не умеет пересобрать, через полгода становится
неприкасаемым. Здесь же видно, из чего он сложен, и любой размер добавляется
одной строкой.

ЗАВИСИМОСТЕЙ НЕТ. PNG пишется вручную (zlib есть в стандартной библиотеке),
ICO — это простой контейнер из заголовка и списка картинок. Pillow не нужен.

ЧТО НАРИСОВАНО. Тёмная скруглённая плитка, на ней ПРИБОР: круговая шкала с
разрывом внизу, подсвеченный сектор на высоких значениях и стрелка. Кокпит — это
приборная панель, и иконка говорит ровно это.

ПОЧЕМУ НЕ БРОНЯ. Первые два наброска рисовали бронеплиту с попаданием — то же,
о чём сами моды. На крупных размерах читалось, на шестнадцати пикселях
превращалось в крест, а потом в швабру: две палки под углом ничем не отличаются
от любых других двух палок. Круговая шкала со стрелкой узнаётся мгновенно и в
16 пикселей, потому что её силуэт ни на что не похож.

СГЛАЖИВАНИЕ. Каждый пиксель считается по сетке SUPERSAMPLE x SUPERSAMPLE точек
через знаковые расстояния до фигур. Это дороже, чем рисовать и уменьшать, зато
край выходит ровным на всех размерах, включая 16, где любая ошибка видна.

Запуск (Python 3):  python tools/make_icon.py
Результат:          src/WotModCockpit.App/Resources/cockpit.ico
                    docs/cockpit-icon.png (256x256, для документации)
"""
import math
import os
import struct
import sys
import zlib

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICO_PATH = os.path.join(REPO, 'src', 'WotModCockpit.App', 'Resources', 'cockpit.ico')
PNG_PATH = os.path.join(REPO, 'docs', 'cockpit-icon.png')

# Размеры, которые Windows реально просит: 16 и 20 — список и мелкие виды,
# 24/32 — панель задач и Alt+Tab, 40/48/64 — крупные виды проводника,
# 128/256 — плитки и предпросмотр.
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
SUPERSAMPLE = 4

# Палитра взята из темы приложения (Styles/DarkTheme.xaml), чтобы иконка и окно
# смотрелись одним целым.
BACKDROP = (0x22, 0x23, 0x27)          # плитка, чуть светлее Color.Window
EDGE = (0x3A, 0x3A, 0x41)              # Color.Border
PLATE_DARK = (0x44, 0x4A, 0x54)        # втулка стрелки
PLATE = (0x6E, 0x76, 0x83)             # шкала
ACCENT = (0x3A, 0x87, 0xC7)            # Color.AccentHover темы: горячий сектор
NEEDLE = (0xE4, 0xEC, 0xF6)            # стрелка

# Шкала: разрыв внизу. Ось Y экранная (вниз), поэтому -pi/2 это верх, а начало
# дуги в нижней левой части — это 135 градусов.
DIAL_START = math.radians(135.0)
DIAL_SWEEP = math.radians(270.0)
# Стрелка смотрит вверх-вправо, в подсвеченный сектор.
NEEDLE_ANGLE = math.radians(-52.0)


def _clamp01(value):
    if value < 0.0:
        return 0.0
    if value > 1.0:
        return 1.0
    return value


def _rounded_rect(x, y, cx, cy, half_w, half_h, radius):
    u"""Знаковое расстояние до скруглённого прямоугольника (<0 внутри)."""
    dx = abs(x - cx) - (half_w - radius)
    dy = abs(y - cy) - (half_h - radius)
    outside = math.hypot(max(dx, 0.0), max(dy, 0.0))
    inside = min(max(dx, dy), 0.0)
    return outside + inside - radius


def _segment(x, y, ax, ay, bx, by, half_width):
    u"""Знаковое расстояние до отрезка с круглыми концами (капсулы)."""
    vx, vy = bx - ax, by - ay
    wx, wy = x - ax, y - ay
    length2 = vx * vx + vy * vy
    t = 0.0 if length2 <= 0.0 else _clamp01((wx * vx + wy * vy) / length2)
    return math.hypot(wx - vx * t, wy - vy * t) - half_width


def _circle(x, y, cx, cy, radius):
    return math.hypot(x - cx, y - cy) - radius


def _arc(x, y, cx, cy, radius, half_band, start, sweep):
    u"""Расстояние до ДУГИ с круглыми концами.

    start и sweep — радианы; ось X вправо, ось Y ВНИЗ (экранная), поэтому угол
    растёт по часовой стрелке, и «верх» это -pi/2.

    Внутри углового сектора расстояние обычное кольцевое, снаружи — до
    ближайшего конца дуги. Так у дуги получаются аккуратные скруглённые торцы
    без отдельной возни с ними.
    """
    dx = x - cx
    dy = y - cy
    angle = math.atan2(dy, dx)
    delta = (angle - start) % (2.0 * math.pi)
    if delta <= sweep:
        return abs(math.hypot(dx, dy) - radius) - half_band
    first = _circle(x, y, cx + radius * math.cos(start),
                    cy + radius * math.sin(start), half_band)
    last = _circle(x, y, cx + radius * math.cos(start + sweep),
                   cy + radius * math.sin(start + sweep), half_band)
    return min(first, last)


def _over(dst, src, alpha):
    u"""Наложение цвета src поверх dst с прозрачностью alpha (0..1)."""
    return tuple(int(round(dst[i] + (src[i] - dst[i]) * alpha)) for i in range(3))


def _shape_alpha(distance, feather):
    u"""Доля покрытия пикселя фигурой по знаковому расстоянию."""
    return _clamp01(0.5 - distance / feather)


def render(size):
    u"""Картинка size x size как список строк по 4 байта на пиксель (RGBA).

    Координаты нормированы в 0..1, поэтому рисунок одинаков на любом размере, а
    толщины заданы долями стороны — иначе на 16 пикселях трасса исчезла бы, а на
    256 превратилась бы в нитку.
    """
    step = 1.0 / (size * SUPERSAMPLE)
    feather = 1.4 / size                      # ширина сглаживания в тех же долях
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            red = green = blue = 0.0
            alpha_sum = 0.0
            for sy in range(SUPERSAMPLE):
                for sx in range(SUPERSAMPLE):
                    x = (px * SUPERSAMPLE + sx + 0.5) * step
                    y = (py * SUPERSAMPLE + sy + 0.5) * step

                    # Плитка со скруглением. За её пределами пиксель прозрачен.
                    tile = _rounded_rect(x, y, 0.5, 0.5, 0.5, 0.5, 0.22)
                    cover = _shape_alpha(tile, feather)
                    if cover <= 0.0:
                        continue

                    color = BACKDROP
                    # Кайма: узкая полоса у самого края плитки.
                    color = _over(color, EDGE, _shape_alpha(abs(tile + 0.035) - 0.018, feather))

                    # ШКАЛА ПРИБОРА. Разрыв внизу — как у любого указателя,
                    # иначе кольцо читается просто кольцом.
                    dial = _arc(x, y, 0.5, 0.5, 0.285, 0.055,
                                DIAL_START, DIAL_SWEEP)
                    color = _over(color, PLATE, _shape_alpha(dial, feather))
                    # Подсвеченный сектор на высоких значениях: правая четверть
                    # шкалы. Он и даёт иконке цветовое пятно.
                    hot = _arc(x, y, 0.5, 0.5, 0.285, 0.055,
                               DIAL_START + DIAL_SWEEP * 0.72, DIAL_SWEEP * 0.28)
                    color = _over(color, ACCENT, _shape_alpha(hot, feather))

                    # СТРЕЛКА: от центра вверх-вправо, в подсвеченный сектор.
                    tip_x = 0.5 + 0.245 * math.cos(NEEDLE_ANGLE)
                    tip_y = 0.5 + 0.245 * math.sin(NEEDLE_ANGLE)
                    tail_x = 0.5 - 0.070 * math.cos(NEEDLE_ANGLE)
                    tail_y = 0.5 - 0.070 * math.sin(NEEDLE_ANGLE)
                    needle = _segment(x, y, tail_x, tail_y, tip_x, tip_y, 0.030)
                    color = _over(color, NEEDLE, _shape_alpha(needle, feather))

                    # ВТУЛКА в центре: без неё стрелка висит в воздухе.
                    hub = _circle(x, y, 0.5, 0.5, 0.072)
                    color = _over(color, PLATE_DARK, _shape_alpha(hub, feather))
                    hub_lit = _circle(x, y, 0.5, 0.5, 0.036)
                    color = _over(color, NEEDLE, _shape_alpha(hub_lit, feather))

                    red += color[0] * cover
                    green += color[1] * cover
                    blue += color[2] * cover
                    alpha_sum += cover

            samples = float(SUPERSAMPLE * SUPERSAMPLE)
            alpha = alpha_sum / samples
            if alpha <= 0.0:
                row += b'\x00\x00\x00\x00'
                continue
            row += bytes((int(round(red / alpha_sum)),
                          int(round(green / alpha_sum)),
                          int(round(blue / alpha_sum)),
                          int(round(alpha * 255.0))))
        rows.append(bytes(row))
    return rows


def png_bytes(rows, size):
    u"""Готовый PNG (RGBA8) из строк пикселей."""

    def chunk(tag, payload):
        return (struct.pack('>I', len(payload)) + tag + payload
                + struct.pack('>I', zlib.crc32(tag + payload) & 0xFFFFFFFF))

    raw = b''.join(b'\x00' + row for row in rows)     # фильтр 0 на каждой строке
    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', ihdr)
            + chunk(b'IDAT', zlib.compress(raw, 9))
            + chunk(b'IEND', b''))


def ico_bytes(images):
    u"""Контейнер .ico из готовых PNG. images — [(размер, байты PNG)].

    Windows принимает PNG внутри .ico начиная с Vista, поэтому отдельного
    BMP-варианта для мелких размеров не требуется.
    """
    count = len(images)
    header = struct.pack('<HHH', 0, 1, count)
    offset = 6 + 16 * count
    directory = b''
    payload = b''
    for size, blob in images:
        directory += struct.pack('<BBBBHHII',
                                 0 if size >= 256 else size,
                                 0 if size >= 256 else size,
                                 0, 0, 1, 32, len(blob), offset)
        payload += blob
        offset += len(blob)
    return header + directory + payload


def main():
    images = []
    for size in SIZES:
        blob = png_bytes(render(size), size)
        images.append((size, blob))
        print('[icon] %3dx%-3d  %6d байт' % (size, size, len(blob)))
    for path in (ICO_PATH, PNG_PATH):
        folder = os.path.dirname(path)
        if not os.path.isdir(folder):
            os.makedirs(folder)
    with open(ICO_PATH, 'wb') as handle:
        handle.write(ico_bytes(images))
    with open(PNG_PATH, 'wb') as handle:
        handle.write(dict(images)[256])
    print('[icon] %s (%d размеров, %d байт)'
          % (ICO_PATH, len(images), os.path.getsize(ICO_PATH)))
    print('[icon] %s' % (PNG_PATH,))
    return 0


if __name__ == '__main__':
    sys.exit(main())
