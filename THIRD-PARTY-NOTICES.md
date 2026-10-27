# Сторонние компоненты

Код кокпита распространяется по MIT (`LICENSE`), а код, который кокпит отдаёт в чужие
моды, — дополнительно по 0BSD (`LICENSE-0BSD`, перечень —
`src/WotModCockpit.Core/Recipes/Recipes/LICENSE.md`). Ниже — всё стороннее, на что он
опирается, и на каких условиях.

## Библиотеки, которые входят в сборку кокпита

Эти пакеты NuGet подтягиваются при сборке, и их DLL ложатся рядом с
`WotModCockpit.App.exe`. Все четыре — под MIT; текст лицензии приведён в конце раздела,
он один и тот же, различаются только строки копирайта.

| Пакет | Версия | Лицензия | Копирайт | Проект |
|---|---|---|---|---|
| AvalonEdit | 6.3.1.120 | MIT | Copyright (c) 2000-2025 AlphaSierraPapa for the SharpDevelop Team | https://github.com/icsharpcode/AvalonEdit |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |

Собственные уведомления о сторонних компонентах этих библиотек (`ThirdPartyNotices.txt`,
`THIRD-PARTY-NOTICES.TXT`) лежат внутри их пакетов NuGet.

Заглушка установщика `src/WotModCockpit.Setup` (из неё получается каждый `Setup.exe`)
сторонних библиотек не содержит: только сборки .NET Framework, которые уже есть в Windows.

### Текст лицензии MIT

```
MIT License

Copyright (c) <год> <правообладатель — см. таблицу выше>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Только для тестов (в сборку кокпита не входят)

| Пакет | Версия | Лицензия |
|---|---|---|
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.0.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.13.0 | MIT |
| Xunit.SkippableFact | 1.5.23 | MS-PL |

Плюс их транзитивные зависимости. Всё это нужно только проекту
`tests/WotModCockpit.Tests` и вместе с кокпитом не распространяется.

## Внешние инструменты (не входят в поставку)

Кокпит ничего из этого не содержит и не распространяет. Каждый инструмент пользователь
ставит сам, а кокпит запускает его отдельным процессом и читает вывод. Поэтому лицензии
этих инструментов на лицензию кокпита не влияют.

| Инструмент | Лицензия | Зачем кокпиту |
|---|---|---|
| Python 3 | PSF-2.0 | сборка мода (`build.py` шаблона), запуск uncompyle6 |
| uncompyle6 | GPL-3.0 | декомпиляция `scripts.pkg` клиента в исходники для браузера API и перехода на новую версию клиента |
| xdis | GPL-2.0 | зависимость uncompyle6 |
| spark-parser | MIT | зависимость uncompyle6 |
| click | BSD-3-Clause | зависимость uncompyle6 |
| six | MIT | зависимость xdis |
| Node.js | MIT | запуск pyright |
| pyright | MIT | необязательный линтер кода мода |

**Не встраивать.** uncompyle6 и xdis — под GPL. Если положить их (например, готовое
окружение Python с ними) в один архив или установщик с кокпитом, такая поставка станет
производным GPL-комплектом с обязательством раскрывать исходники. Ставятся они только
пользователем, отдельно.

## Игра и чужие моды

Кокпит работает с файлами игры «Мир танков» (Lesta Games), её скриптами и чужими модами,
которые установлены у пользователя, но ничего из этого не содержит и не распространяет.
Лицензии кокпита на них не распространяются; права на них принадлежат их правообладателям.
