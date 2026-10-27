using System.Runtime.CompilerServices;
using WotModCockpit.App.ViewModels;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Поведенческие тесты <see cref="MainViewModel"/> — того самого конвейера ленты, ради которого
/// кокпит и существует: склейка traceback в фоновом потоке, выгрузка пачкой по такту
/// <c>DispatcherTimer</c>, кольцевой буфер с вытеснением, реакция на усечение файла.
/// <para>
/// Всё крутится на настоящем UI-потоке (<see cref="UiThread"/>), но БЕЗ окна. Игра и диск не
/// участвуют: тейлер подставной, строки скопированы из реального <c>python.log</c>.
/// </para>
/// </summary>
public class MainViewModelTests : IDisposable
{
    private readonly UiThread _ui = new();
    private readonly FakeTailerFactory _tailers = new();
    private readonly FakeSettingsStore _settings = new();
    private readonly FakeProjectStore _projects = new();
    private readonly FakeLocator _locator = new();
    private readonly FakeDeployProbe _deploy = new();

    /// <summary>Запуск build.py. По умолчанию — падает: тест, которому сборка не нужна, её не запустит.</summary>
    private IBuildRunner _buildRunner = new ThrowingBuildRunner();

    private MainViewModel? _vm;

    /// <summary>Гасит модель представления и UI-поток.</summary>
    public void Dispose()
    {
        if (_vm is not null)
        {
            try
            {
                _ui.Invoke(() => _vm!.Dispose());
            }
            catch (Exception)
            {
                // закрытие теста не должно маскировать причину падения самого теста
            }
        }

        _ui.Dispose();
        GC.SuppressFinalize(this);
    }

    // ================================================================= кольцевой буфер

    /// <summary>
    /// РЕГРЕСС «показано 5504 из 1000». Пачка такта (до 4000 строк) больше ёмкости буфера,
    /// поэтому вытеснение обязано идти ПОСЛЕ добавления в видимый список: иначе выравнивание
    /// голов ломается навсегда и видимый список растёт без предела.
    /// </summary>
    [Fact]
    public void Pump_BatchLargerThanCapacity_KeepsVisibleWithinBuffer()
    {
        MainViewModel vm = Start(capacity: 1000);

        FakeTailer tailer = _tailers.Last;
        for (int i = 0; i < 4000; i++)
            tailer.Emit($"2026-08-28 01:02:03.{i % 1000:000}: INFO: [NOTE] строка {i}");

        // Последняя строка закрывается по тишине (IdleFlushMs), поэтому ждём именно её.
        _ui.WaitUntil(
            () => vm.LogEntries.Count > 0 && vm.LogEntries[^1].Message.Contains("строка 3999", StringComparison.Ordinal),
            "вся пачка доехала",
            diagnostics: () => $"visible={vm.VisibleCount} total={vm.TotalCount}");
        _ui.Pump();

        _ui.Invoke(() =>
        {
            Assert.Equal(1000, vm.TotalCount);
            Assert.Equal(1000, vm.VisibleCount);
            Assert.Equal(1000, vm.LogEntries.Count);
            Assert.True(vm.VisibleCount <= vm.TotalCount, "видимых больше, чем в буфере");
            Assert.True(vm.TotalCount <= vm.BufferCapacity, "буфер перерос собственную ёмкость");

            // Вытеснение идёт с ГОЛОВЫ: остаться должен хвост, а не начало.
            Assert.Contains("строка 3999", vm.LogEntries[^1].Message, StringComparison.Ordinal);
            Assert.Contains("строка 3000", vm.LogEntries[0].Message, StringComparison.Ordinal);
        });
    }

    /// <summary>Вытеснение не ломает выравнивание, когда часть записей скрыта фильтром.</summary>
    [Fact]
    public void Pump_WithFilter_KeepsHeadsAligned()
    {
        MainViewModel vm = Start(capacity: 1000);
        _ui.Invoke(() => vm.OnlyErrors = true);

        FakeTailer tailer = _tailers.Last;
        for (int i = 0; i < 2000; i++)
        {
            string level = i % 10 == 0 ? "ERROR" : "INFO";
            tailer.Emit($"2026-08-28 01:02:03.{i % 1000:000}: {level}: [NOTE] строка {i}");
        }

        _ui.WaitUntil(() => vm.TotalCount >= 1000, "буфер наполнился");
        _ui.Pump();

        _ui.Invoke(() =>
        {
            Assert.Equal(1000, vm.TotalCount);
            Assert.True(vm.VisibleCount < vm.TotalCount, "фильтр «только ошибки» ничего не отсёк");
            Assert.Equal(vm.VisibleCount, vm.LogEntries.Count);
            Assert.All(vm.LogEntries, e => Assert.Equal(LogSeverity.Error, e.Severity));

            // Каждая видимая запись обязана оставаться в буфере — это и есть выравнивание голов.
            Assert.Contains("строка 1990", vm.LogEntries[^1].Message, StringComparison.Ordinal);
        });
    }

    // ================================================================= усечение

    /// <summary>
    /// Порядок ленты вокруг усечения: последняя незакрытая запись СТАРОГО содержимого (вместе
    /// с телом traceback) отдаётся ДО разделителя, а всё новое — строго после него.
    /// </summary>
    [Fact]
    public void Truncation_SeparatorSitsExactlyBetweenOldAndNew()
    {
        MainViewModel vm = Start(capacity: 20_000);
        FakeTailer tailer = _tailers.Last;

        for (int i = 1; i <= 3; i++)
            tailer.Emit($"2026-08-28 01:02:0{i}.000: INFO: [NOTE] до усечения {i}");

        // Незакрытая запись с телом traceback — ровно то, что раньше молча пропадало.
        tailer.Emit("2026-08-28 01:02:11.100: ERROR: [EXCEPTION] (scripts/common/items/vehicles.py, 4850)");
        tailer.Emit("Traceback (most recent call last):");
        tailer.Emit("  File \"scripts/common/items/vehicles.py\", line 4845, in getItemByCompactDescr");

        tailer.EmitTruncated();

        for (int i = 1; i <= 3; i++)
            tailer.Emit($"2026-08-28 01:04:0{i}.000: INFO: [NOTE] после усечения {i}");

        _ui.WaitUntil(() => vm.TotalCount >= 8, "все записи доехали",
            diagnostics: () => Dump(vm));
        _ui.Pump();

        List<string> feed = _ui.Invoke(() => vm.LogEntries
            .Select(e => e.IsSystem ? "SYS" : e.Message)
            .ToList());

        int separator = feed.IndexOf("SYS");
        Assert.True(separator >= 0, "разделитель усечения не появился: " + string.Join(" | ", feed));

        // До разделителя — только старое, после — только новое.
        Assert.All(feed.Take(separator), m => Assert.DoesNotContain("после усечения", m, StringComparison.Ordinal));
        Assert.All(feed.Skip(separator + 1), m => Assert.DoesNotContain("до усечения", m, StringComparison.Ordinal));

        // Запись с traceback закрыта ДО разделителя и не потеряла тело.
        LogEntryViewModel withTb = _ui.Invoke(() => vm.LogEntries[separator - 1]);
        Assert.True(withTb.Entry.HasTraceback, "тело traceback выброшено вместе с усечением");
        Assert.Equal(2, withTb.Entry.Continuation.Count);
        Assert.True(withTb.CanExpand);

        Assert.Equal(3, feed.Skip(separator + 1).Count());
    }

    /// <summary>Разделитель усечения виден и при включённом фильтре «только ошибки».</summary>
    [Fact]
    public void Truncation_SeparatorSurvivesFilters()
    {
        MainViewModel vm = Start(capacity: 20_000);
        _ui.Invoke(() =>
        {
            vm.OnlyErrors = true;
            vm.OnlyMine = true;
        });

        FakeTailer tailer = _tailers.Last;
        tailer.Emit("2026-08-28 01:02:01.000: INFO: [NOTE] обычная строка");
        tailer.EmitTruncated();
        tailer.Emit("2026-08-28 01:04:01.000: INFO: [NOTE] ещё обычная строка");

        _ui.WaitUntil(() => vm.TotalCount >= 3, "записи доехали", diagnostics: () => Dump(vm));
        _ui.Pump();

        _ui.Invoke(() =>
        {
            Assert.Equal(1, vm.VisibleCount);
            Assert.True(vm.LogEntries[0].IsSystem);
        });
    }

    // ================================================================= склейка traceback

    /// <summary>
    /// Незакрытая запись дозревает по тишине (IdleFlushMs) и приезжает в ленту ОДНОЙ штукой
    /// вместе со всем телом traceback, а не четырьмя строками.
    /// </summary>
    [Fact]
    public void IdleFlush_GluesTracebackIntoSingleEntry()
    {
        MainViewModel vm = Start(capacity: 20_000);
        FakeTailer tailer = _tailers.Last;

        tailer.Emit("2026-08-28 01:02:11.100: ERROR: [EXCEPTION] (scripts/common/items/vehicles.py, 4850)");
        tailer.Emit("Traceback (most recent call last):");
        tailer.Emit("  File \"scripts/common/items/vehicles.py\", line 4845, in getItemByCompactDescr");
        tailer.Emit("TypeError: unsupported operand type(s) for &: 'NoneType' and 'int'");

        // Ничего больше не приходит: запись обязана закрыться сама, по тишине.
        _ui.WaitUntil(() => vm.TotalCount >= 1, "запись дозрела по тишине",
            timeout: TimeSpan.FromSeconds(5), diagnostics: () => Dump(vm));
        _ui.Pump();

        _ui.Invoke(() =>
        {
            Assert.Equal(1, vm.TotalCount);

            LogEntryViewModel e = vm.LogEntries[0];
            Assert.Equal("ERROR", e.Level);
            Assert.Equal(3, e.Entry.Continuation.Count);
            Assert.True(e.Entry.HasTraceback);
            Assert.True(e.CanExpand);
            Assert.Contains("TypeError", e.ContinuationText, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Строка, которую парсер не разобрал, всё равно попадает в ленту (уровнем UNKNOWN):
    /// в инструменте отладки молча проглоченная строка хуже неразобранной.
    /// </summary>
    [Fact]
    public void UnparsedLine_IsShownRaw()
    {
        MainViewModel vm = Start(capacity: 20_000);
        _tailers.Last.Emit("совершенно не похоже на строку лога");

        _ui.WaitUntil(() => vm.TotalCount >= 1, "сырая строка доехала", diagnostics: () => Dump(vm));

        _ui.Invoke(() =>
        {
            Assert.Equal(LogLevels.Unknown, vm.LogEntries[0].Level);
            Assert.Equal("совершенно не похоже на строку лога", vm.LogEntries[0].Entry.RawLine);
        });
    }

    // ================================================================= фильтры

    /// <summary>
    /// «Только ошибки» = <see cref="LogFilter.IsProblem"/>, одна реализация на Core и UI:
    /// ERROR и записи с traceback проходят, WARNING и INFO — нет.
    /// </summary>
    [Fact]
    public void OnlyErrors_MatchesLogFilterSemantics()
    {
        MainViewModel vm = Start(capacity: 20_000);
        FakeTailer tailer = _tailers.Last;

        tailer.Emit("2026-08-28 01:02:03.345: INFO: [Scaleform] Create GUI Component: 'x.swf'");
        tailer.Emit("2026-08-28 01:02:04.965: WARNING: [gui.shared.gui_items.vehicle_equipment] bad args");
        tailer.Emit("2026-08-28 01:02:11.100: ERROR: [ERROR] (scripts/common/items/vehicles.py, 4851) mismatch");
        tailer.Emit("2026-08-28 01:02:12.000: INFO: [NOTE] упало внутри");
        tailer.Emit("Traceback (most recent call last):");
        tailer.Emit("  File \"scripts/client/gui/mods/mod_hello_tanker.py\", line 67, in init");

        _ui.WaitUntil(() => vm.TotalCount >= 4, "все четыре записи доехали", diagnostics: () => Dump(vm));
        _ui.Pump();

        _ui.Invoke(() =>
        {
            Assert.Equal(4, vm.VisibleCount);

            vm.OnlyErrors = true;

            // ERROR и INFO-с-traceback; WARNING в «ошибки» не входит (LogFilter.IsProblem).
            Assert.Equal(2, vm.VisibleCount);
            Assert.All(vm.LogEntries, e => Assert.True(
                e.Severity == LogSeverity.Error || e.Entry.HasTraceback,
                "в «только ошибки» просочилось: " + e.Message));

            vm.OnlyErrors = false;
            Assert.Equal(4, vm.VisibleCount);
        });
    }

    /// <summary>Поиск по подстроке применяется с задержкой и ищет в том числе по телу traceback.</summary>
    [Fact]
    public void Search_FiltersByTextIncludingTraceback()
    {
        MainViewModel vm = Start(capacity: 20_000);
        FakeTailer tailer = _tailers.Last;

        tailer.Emit("2026-08-28 01:02:03.345: INFO: [Scaleform] Create GUI Component: 'x.swf'");
        tailer.Emit("2026-08-28 01:02:11.100: ERROR: [EXCEPTION] (scripts/common/items/vehicles.py, 4850)");
        tailer.Emit("TypeError: unsupported operand type(s) for &: 'NoneType' and 'int'");

        _ui.WaitUntil(() => vm.TotalCount >= 2, "записи доехали", diagnostics: () => Dump(vm));

        _ui.Invoke(() => vm.SearchText = "typeerror");
        _ui.WaitUntil(() => vm.VisibleCount == 1, "поиск применился", diagnostics: () => Dump(vm));

        _ui.Invoke(() => Assert.Equal("EXCEPTION", vm.LogEntries[0].Category));

        _ui.Invoke(() => vm.SearchText = string.Empty);
        _ui.WaitUntil(() => vm.VisibleCount == 2, "поиск снят", diagnostics: () => Dump(vm));
    }

    // ================================================================= команды ленты

    /// <summary>
    /// «Перечитать лог» очищает ленту перед новым прогревом. Иначе прогрев отдаёт те же
    /// последние килобайты файла, и пользователь получает их вторым экземпляром подряд.
    /// </summary>
    [Fact]
    public void RestartTailing_DoesNotDuplicatePrimedTail()
    {
        _tailers.PrimeLines.AddRange(new[]
        {
            "2026-08-28 01:01:00.000: INFO: [NOTE] прогрев 1",
            "2026-08-28 01:01:01.000: INFO: [NOTE] прогрев 2",
            "2026-08-28 01:01:02.000: INFO: [NOTE] прогрев 3"
        });

        MainViewModel vm = Start(capacity: 20_000);
        _ui.WaitUntil(() => vm.TotalCount >= 3, "прогрев доехал", diagnostics: () => Dump(vm));

        _ui.Invoke(() => vm.RestartTailingCommand.Execute(null));
        _ui.WaitUntil(() => vm.TotalCount >= 3, "прогрев после перезапуска доехал", diagnostics: () => Dump(vm));
        _ui.Pump();

        _ui.Invoke(() =>
        {
            Assert.Equal(3, vm.TotalCount);
            Assert.Equal(3, vm.VisibleCount);
            Assert.Equal(2, _tailers.Created.Count);          // старый тейлер закрыт, создан новый
        });
    }

    /// <summary>«Дочитать весь файл» просит прогрев без потолка, а не настроечные килобайты.</summary>
    [Fact]
    public void ReadWholeLog_PrimesEntireFile()
    {
        MainViewModel vm = Start(capacity: 20_000, primeKilobytes: 64);
        Assert.Equal(64 * 1024, _tailers.Last.LastPrimeBytes);

        _ui.Invoke(() => vm.ReadWholeLogCommand.Execute(null));

        Assert.Equal(int.MaxValue, _tailers.Last.LastPrimeBytes);
        _ui.Invoke(() => Assert.Equal(0, vm.TotalCount));
    }

    /// <summary>«Очистить» чистит и буфер, и видимый список, но не трогает файл.</summary>
    [Fact]
    public void ClearLog_EmptiesBufferAndView()
    {
        MainViewModel vm = Start(capacity: 20_000);
        _tailers.Last.Emit("2026-08-28 01:01:00.000: INFO: [NOTE] что-то");
        _ui.WaitUntil(() => vm.TotalCount >= 1, "запись доехала");

        _ui.Invoke(() => vm.ClearLogCommand.Execute(null));

        _ui.Invoke(() =>
        {
            Assert.Equal(0, vm.TotalCount);
            Assert.Equal(0, vm.VisibleCount);
            Assert.Empty(vm.LogEntries);
        });
    }

    // ================================================================= запись в игру

    /// <summary>
    /// Пока установка игры не опознана, кнопки записи в игру ОБЯЗАНЫ быть недоступны:
    /// каталог назначения неизвестен, а диалог подтверждения — единственная преграда.
    /// </summary>
    [Fact]
    public void WriteActions_AreDisabled_WhenGameIsNotDetected()
    {
        _locator.Fails = true;                         // «не похоже на установку игры»
        MainViewModel vm = Start(capacity: 20_000, projectDir: @"C:\projects\mod");

        _ui.Invoke(() => Assert.Contains("не похоже на установку игры", vm.GameProblem, StringComparison.Ordinal));

        _ui.Invoke(() =>
        {
            Assert.False(vm.RunBuildCommand.CanExecute(BuildAction.BuildAndInstall));
            Assert.False(vm.RunBuildCommand.CanExecute(BuildAction.DevDeploy));
            Assert.False(vm.RunBuildCommand.CanExecute(BuildAction.Uninstall));

            // Не пишущие в игру действия при этом доступны.
            Assert.True(vm.RunBuildCommand.CanExecute(BuildAction.Build));
            Assert.True(vm.RunBuildCommand.CanExecute(BuildAction.List));
        });
    }

    /// <summary>Как только игра опознана, кнопки записи оживают.</summary>
    [Fact]
    public void WriteActions_AreEnabled_WhenGameAndProjectAreKnown()
    {
        MainViewModel vm = Start(capacity: 20_000, projectDir: @"C:\projects\mod");

        _ui.Invoke(() =>
        {
            Assert.True(vm.RunBuildCommand.CanExecute(BuildAction.BuildAndInstall));
            Assert.True(vm.RunBuildCommand.CanExecute(BuildAction.DevDeploy));
        });
    }

    /// <summary>
    /// Чип <c>--clean</c> поверх лежащей в игре россыпи обязан спросить: <c>--clean</c> стирает
    /// dist\dev_manifest.json, без которого «Удалить из игры» больше ничего не найдёт.
    /// </summary>
    [Fact]
    public void CleanBuild_OverDevSpill_AsksBeforeRunning()
    {
        string dist = Path.Combine(Path.GetTempPath(), "wot-cockpit-dist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dist);
        File.WriteAllText(Path.Combine(dist, "dev_manifest.json"), "{\"files\": []}");

        try
        {
            _projects.Project = new ModProject
            {
                ProjectDir = dist,                     // dist лежит прямо в «проекте»
                DistDirRelative = ".",
                ModId = "ru.local.hello_tanker",
                Version = "1.0.0"
            };

            _deploy.State = new DeployState(false, null, true, new[] { @"D:\res_mods\x.py" }, false);

            MainViewModel vm = Start(capacity: 20_000, projectDir: dist);

            var asked = new List<string>();
            _ui.Invoke(() =>
            {
                vm.ConfirmHandler = (caption, _) => { asked.Add(caption); return false; };
                vm.CleanBuild = true;
                Assert.True(vm.DeployInResMods, "проба деплоя не увидела россыпь");
                vm.RunBuildCommand.Execute(BuildAction.Build);
            });

            _ui.WaitUntil(() => asked.Count > 0, "предупреждение про --clean показано",
                diagnostics: () => "диалогов не было");

            Assert.Contains("--clean", asked[0], StringComparison.Ordinal);
            _ui.Invoke(() => Assert.Equal("отменено пользователем", vm.StatusMessage));
        }
        finally
        {
            try { Directory.Delete(dist, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Замечание NO_WOTMOD_JSON обязано сниматься из UI: кнопка «создать wotmod.json»
    /// зовёт <see cref="IProjectStore.Save"/> (единственная запись кокпита в каталог проекта)
    /// и после перечитывания проекта исчезает сама.
    /// </summary>
    [Fact]
    public void CreateWotmodJson_WritesDescriptorAndHidesItself()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wot-cockpit-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            _projects.Project = new ModProject
            {
                ProjectDir = dir,
                ModId = "ru.local.hello_tanker",
                Version = "1.0.0",
                LogTag = "[HelloTanker"
            };

            MainViewModel vm = Start(capacity: 20_000, projectDir: dir);

            _ui.Invoke(() =>
            {
                Assert.True(vm.WotmodJsonMissing, "кнопка «создать wotmod.json» обязана быть видна");
                Assert.True(vm.CreateWotmodJsonCommand.CanExecute(null));

                vm.ConfirmHandler = static (_, _) => true;
                vm.CreateWotmodJsonCommand.Execute(null);

                Assert.True(File.Exists(Path.Combine(dir, "wotmod.json")), "wotmod.json не создан");
                Assert.False(vm.WotmodJsonMissing, "кнопка осталась висеть после создания файла");
                Assert.False(vm.CreateWotmodJsonCommand.CanExecute(null));
            });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Каталог игры из шапки не подставляется в сохраняемую модель проекта: «Создать wotmod.json»
    /// сохраняет этот же объект, и абсолютный путь этой машины уехал бы в git вместе с проектом.
    /// Сборка при этом всё равно знает, куда ставить, — через <see cref="ModProject.EffectiveGameDir"/>.
    /// </summary>
    [Fact]
    public void OpenProject_DoesNotWriteGlobalGameDirIntoTheProject()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wot-cockpit-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            _projects.Project = new ModProject { ProjectDir = dir, ModId = "ru.example.alpha", Version = "1.0.0" };

            MainViewModel vm = Start(capacity: 20_000, projectDir: dir);

            _ui.Invoke(() =>
            {
                Assert.False(string.IsNullOrEmpty(vm.GameDir));
                Assert.Equal(string.Empty, _projects.Project!.GameDir);
                Assert.Equal(vm.GameDir, _projects.Project.EffectiveGameDir);
            });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Каталог игры из шапки главнее всегда: туда смотрят опознание установки и диалог
    /// подтверждения записи, и сборка не имеет права писать в другое место — даже если свой
    /// <c>gameDir</c> проекта существует. <c>gameDir</c> проекта берётся, только когда игра в
    /// шапке не выбрана.
    /// </summary>
    [Fact]
    public void EffectiveGameDir_HeaderAlwaysWins_ProjectGameDirOnlyWithoutHeader()
    {
        string existing = Path.GetTempPath();

        Assert.Equal(@"C:\Games\Tanki", MainViewModel.EffectiveGameDirFor(
            new ModProject { GameDir = @"Z:\no-such-machine\Games\Tanki" }, @"C:\Games\Tanki"));
        Assert.Equal(@"C:\Games\Tanki", MainViewModel.EffectiveGameDirFor(
            new ModProject { GameDir = existing }, @"C:\Games\Tanki"));
        Assert.Equal(@"C:\Games\Tanki", MainViewModel.EffectiveGameDirFor(new ModProject(), @"C:\Games\Tanki"));
        Assert.Equal(existing, MainViewModel.EffectiveGameDirFor(new ModProject { GameDir = existing }, null));
        Assert.Equal(existing, MainViewModel.EffectiveGameDirFor(new ModProject { GameDir = existing }, "  "));
        Assert.Equal(string.Empty, MainViewModel.EffectiveGameDirFor(new ModProject(), null));
    }

    /// <summary>
    /// Шаблон, выбранный в мастере, запоминается в общем объекте настроек и сохраняется; не шаблон
    /// (нет build.py) — не запоминается. Префикс id новых модов берётся из настроек.
    /// </summary>
    [Fact]
    public void RememberTemplateDir_SavesOnlyRealTemplates()
    {
        string template = Path.Combine(Path.GetTempPath(), "wot-cockpit-template-" + Guid.NewGuid().ToString("N"));
        string notTemplate = Path.Combine(Path.GetTempPath(), "wot-cockpit-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(template);
        Directory.CreateDirectory(notTemplate);
        File.WriteAllText(Path.Combine(template, "build.py"), "# stand");

        try
        {
            MainViewModel vm = Start(capacity: 20_000);

            _ui.Invoke(() =>
            {
                Assert.Equal("ru.local", vm.ModIdPrefix);

                vm.RememberTemplateDir(notTemplate);
                Assert.NotEqual(notTemplate, _settings.Saved?.TemplateDir);

                vm.RememberTemplateDir(template);
                Assert.Equal(template, _settings.Saved?.TemplateDir);
            });
        }
        finally
        {
            try { Directory.Delete(template, recursive: true); } catch (IOException) { }
            try { Directory.Delete(notTemplate, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Отказ в диалоге ничего не пишет на диск.</summary>
    [Fact]
    public void CreateWotmodJson_RespectsCancel()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wot-cockpit-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            _projects.Project = new ModProject { ProjectDir = dir, ModId = "ru.local.hello_tanker" };
            MainViewModel vm = Start(capacity: 20_000, projectDir: dir);

            _ui.Invoke(() =>
            {
                vm.ConfirmHandler = static (_, _) => false;
                vm.CreateWotmodJsonCommand.Execute(null);

                Assert.False(File.Exists(Path.Combine(dir, "wotmod.json")));
                Assert.Equal("отменено пользователем", vm.StatusMessage);
            });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ================================================================= настройки

    /// <summary>
    /// В «недавние проекты» не должен попадать путь, по которому проекта нет:
    /// один мусорный lastProjectDir в settings.json иначе переезжает в список навсегда.
    /// </summary>
    [Fact]
    public void SaveSettings_DoesNotRememberProjectsThatFailedToLoad()
    {
        _projects.Fails = true;                         // каталог не похож на проект мода
        MainViewModel vm = Start(capacity: 20_000, projectDir: @"Z:\no\such\project");

        _ui.Invoke(() => Assert.Contains("не похож на проект мода", vm.ProjectProblem, StringComparison.Ordinal));
        _ui.Invoke(() => vm.Dispose());                 // Dispose сохраняет настройки
        _vm = null;

        Assert.DoesNotContain(@"Z:\no\such\project", _settings.Saved?.RecentProjects ?? new List<string>());
    }

    // ================================================================= экран «Моды» после сборки

    /// <summary>
    /// build.py пишет мимо экрана «Моды» — в каталог модов («Удалить из игры») и в dist\
    /// проекта. Показанный экран «Моды» обязан перечитать список после любого запуска: за dist\
    /// наблюдатель каталога модов не следит, и без сигнала от экрана сборки строка так и
    /// показывала бы сборку, которой уже нет.
    /// </summary>
    [Fact]
    public void BuildFinished_ShownModsScreen_Rereads()
    {
        using FakeGameDir game = new();
        string drive = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha");
        string artifact = Path.Combine(drive, "dist", "ru.example.alpha_1.0.0.mtmod");
        Assert.True(File.Exists(artifact), "стенд: у проекта должна быть сборка в dist");

        // Иконку строки читают из этого же архива в фоне — удаление повторяется, пока файл
        // занят, как повторил бы настоящий build.py после чтения.
        _buildRunner = new ScriptedBuildRunner(() => DeleteWithRetry(artifact));
        MainViewModel vm = Start(capacity: 1000, projectDir: drive);

        ModsViewModel mods = _ui.Invoke(() => new ModsViewModel(
            vm,
            new ModLibraryService(FakeGameProbes.Closed, new ModLibraryOptions
            {
                // журнал пользователя тест не трогает ни на запись, ни на чтение
                OperationLogPath = Path.Combine(game.Root, "mods-operations.log"),
                WriteOperationLog = false
            }),
            new FakeLocator { Detected = game.Install },
            new FakeSettingsStore { Settings = new CockpitSettings { LastProjectDir = drive } },
            new NoopEditorLauncher()));

        try
        {
            _ui.Invoke(() => vm.IsModsScreen = true);
            _ui.WaitUntil(
                // IsScanning снимается после подгрузки иконок — архив к этому моменту закрыт.
                () => !mods.IsScanning && mods.Mods.Any(r => r.Title == "Alpha" && r.HasBuiltArtifact),
                "экран «Моды» прочитал каталог и иконки",
                diagnostics: () => string.Join(", ", mods.Mods.Select(r => r.Title)));

            _ui.Invoke(() => vm.RunBuildCommand.Execute(BuildAction.Build));

            _ui.WaitUntil(
                () => mods.Mods.Any(r => r.Title == "Alpha" && !r.HasBuiltArtifact),
                "экран «Моды» перечитал список после сборки",
                diagnostics: () => vm.StatusMessage);
        }
        finally
        {
            _ui.Invoke(mods.Dispose);
        }
    }

    private static void DeleteWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
    }

    // ================================================================= «Собрать и установить» из меню «Мои моды»

    /// <summary>
    /// Главный сценарий. build.py зовут с ДЕЙСТВИЕМ «Собрать» (без <c>--install</c>) и именно
    /// для проекта строки; в игру архив ставит служба библиотеки после диалога с полными путями,
    /// прежняя версия выключается. Открытый на экране сборки проект при этом не меняется.
    /// </summary>
    [Fact]
    public void BuildAndInstall_BuildsWithoutInstallFlag_ThenInstallsThroughLibrary()
    {
        using FakeGameDir game = new();
        string drive = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha");
        string oldInstalled = game.CopyIntoMods(
            Path.Combine(drive, "dist", "ru.example.alpha_1.0.0.mtmod"), "ru.example.alpha_1.0.0.mtmod");
        string fresh = Path.Combine(drive, "dist", "ru.example.alpha_1.1.0.mtmod");
        string target = Path.Combine(game.ModsDir, "ru.example.alpha_1.1.0.mtmod");

        var runner = new RecordingBuildRunner(() =>
        {
            WriteDriveArchive(fresh, "1.1.0");
            return new BuildResult(true, 0, fresh, TimeSpan.FromMilliseconds(1));
        });
        (MainViewModel vm, ModsViewModel mods, List<(string Caption, string Body)> asked) =
            StartWithMods(game, drive, "1.1.0", runner, FakeGameProbes.Closed, answer: true);

        try
        {
            SelectInstalledRow(mods, "Alpha");
            Assert.True(_ui.Invoke(() => mods.BuildAndInstallCommand.CanExecute(null)), "пункт меню погашен у своего мода");

            _ui.Invoke(() => mods.BuildAndInstallCommand.Execute(null));
            _ui.WaitUntil(
                () => !mods.BuildAndInstallCommand.IsRunning && File.Exists(target),
                "новая сборка установлена",
                diagnostics: () => mods.StatusText);

            Assert.Equal(new[] { (BuildAction.Build, drive) }, _ui.Invoke(() => runner.Calls.ToList()));
            Assert.False(File.Exists(oldInstalled), "прежняя версия осталась включённой");
            Assert.True(File.Exists(oldInstalled + ".off"), "прежняя версия не выключена, а пропала");

            (string caption, string body) = Assert.Single(asked);
            Assert.Equal("Установить мод в игру", caption);
            Assert.Contains(fresh, body, StringComparison.Ordinal);
            Assert.Contains(target, body, StringComparison.Ordinal);
            Assert.Contains("1.1.0", body, StringComparison.Ordinal);

            List<string> journal = _ui.Invoke(() => mods.Journal.ToList());
            Assert.Contains(journal, line => line.Contains("BUILD", StringComparison.Ordinal)
                                             && line.Contains(fresh, StringComparison.Ordinal));
            Assert.Equal(string.Empty, _ui.Invoke(() => vm.ProjectDir));
            Assert.False(_ui.Invoke(() => vm.IsBusy));
        }
        finally
        {
            _ui.Invoke(mods.Dispose);
        }
    }

    /// <summary>
    /// Игра запущена: пункт погашен, а вызванная в обход меню команда не собирает и не пишет
    /// ничего — незачем собирать то, что всё равно не поставить.
    /// </summary>
    [Fact]
    public void BuildAndInstall_GameRunning_BuildsNothing()
    {
        using FakeGameDir game = new();
        string drive = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha");
        game.CopyIntoMods(Path.Combine(drive, "dist", "ru.example.alpha_1.0.0.mtmod"), "ru.example.alpha_1.0.0.mtmod");
        string[] before = game.Snapshot();

        var runner = new RecordingBuildRunner(() => throw new InvalidOperationException("сборка при запущенной игре"));
        (_, ModsViewModel mods, List<(string Caption, string Body)> asked) =
            StartWithMods(game, drive, "1.1.0", runner, FakeGameProbes.Running, answer: true);

        try
        {
            SelectInstalledRow(mods, "Alpha");
            Assert.False(_ui.Invoke(() => mods.BuildAndInstallCommand.CanExecute(null)), "пункт горит при запущенной игре");

            _ui.Invoke(() => mods.BuildAndInstallCommand.Execute(null));
            _ui.WaitUntil(() => !mods.BuildAndInstallCommand.IsRunning, "команда отработала");

            Assert.Empty(_ui.Invoke(() => runner.Calls.ToList()));
            Assert.Empty(asked);
            Assert.Equal(before, game.Snapshot());
        }
        finally
        {
            _ui.Invoke(mods.Dispose);
        }
    }

    /// <summary>Сборка упала: в игру не записано ничего, причина — в диалоге и журнале.</summary>
    [Fact]
    public void BuildAndInstall_BuildFails_InstallsNothing()
    {
        using FakeGameDir game = new();
        string drive = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha");
        game.CopyIntoMods(Path.Combine(drive, "dist", "ru.example.alpha_1.0.0.mtmod"), "ru.example.alpha_1.0.0.mtmod");
        string[] before = game.Snapshot();

        var runner = new RecordingBuildRunner(
            () => new BuildResult(false, 1, string.Empty, TimeSpan.FromMilliseconds(1)) { FailureReason = "синтаксис py2" });
        (_, ModsViewModel mods, List<(string Caption, string Body)> asked) =
            StartWithMods(game, drive, "1.1.0", runner, FakeGameProbes.Closed, answer: true);

        try
        {
            SelectInstalledRow(mods, "Alpha");
            _ui.Invoke(() => mods.BuildAndInstallCommand.Execute(null));
            _ui.WaitUntil(() => !mods.BuildAndInstallCommand.IsRunning, "команда отработала");

            Assert.Single(_ui.Invoke(() => runner.Calls.ToList()));
            (string caption, string body) = Assert.Single(asked);
            Assert.Equal("Сборка не удалась", caption);
            Assert.Contains("синтаксис py2", body, StringComparison.Ordinal);
            Assert.Contains(_ui.Invoke(() => mods.Journal.ToList()),
                            line => line.Contains("BUILD", StringComparison.Ordinal) && line.Contains("FAIL", StringComparison.Ordinal));
            Assert.Equal(before, game.Snapshot());
        }
        finally
        {
            _ui.Invoke(mods.Dispose);
        }
    }

    /// <summary>Сборка прошла, но в диалоге установки ответили «нет»: каталог игры не тронут.</summary>
    [Fact]
    public void BuildAndInstall_InstallDeclined_GameUntouched()
    {
        using FakeGameDir game = new();
        string drive = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha");
        game.CopyIntoMods(Path.Combine(drive, "dist", "ru.example.alpha_1.0.0.mtmod"), "ru.example.alpha_1.0.0.mtmod");
        string[] before = game.Snapshot();
        string fresh = Path.Combine(drive, "dist", "ru.example.alpha_1.1.0.mtmod");

        var runner = new RecordingBuildRunner(() =>
        {
            WriteDriveArchive(fresh, "1.1.0");
            return new BuildResult(true, 0, fresh, TimeSpan.FromMilliseconds(1));
        });
        (_, ModsViewModel mods, List<(string Caption, string Body)> asked) =
            StartWithMods(game, drive, "1.1.0", runner, FakeGameProbes.Closed, answer: false);

        try
        {
            SelectInstalledRow(mods, "Alpha");
            _ui.Invoke(() => mods.BuildAndInstallCommand.Execute(null));
            _ui.WaitUntil(() => !mods.BuildAndInstallCommand.IsRunning, "команда отработала");

            Assert.True(File.Exists(fresh), "сборка не дошла до dist");
            Assert.Equal("Установить мод в игру", Assert.Single(asked).Caption);
            Assert.Equal(before, game.Snapshot());
        }
        finally
        {
            _ui.Invoke(mods.Dispose);
        }
    }

    /// <summary>
    /// build.py один на оба экрана. Пока экран сборки занят, пункт гаснет (и об этом сообщается
    /// меню), а вызванная в обход команда второй build.py не запускает.
    /// </summary>
    [Fact]
    public void BuildAndInstall_WhileBuildScreenBusy_DoesNotStartSecondBuild()
    {
        using FakeGameDir game = new();
        string drive = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha");
        game.CopyIntoMods(Path.Combine(drive, "dist", "ru.example.alpha_1.0.0.mtmod"), "ru.example.alpha_1.0.0.mtmod");
        string[] before = game.Snapshot();

        var runner = new RecordingBuildRunner(() => throw new InvalidOperationException("вторая сборка"));
        (MainViewModel vm, ModsViewModel mods, List<(string Caption, string Body)> asked) =
            StartWithMods(game, drive, "1.1.0", runner, FakeGameProbes.Closed, answer: true);

        try
        {
            SelectInstalledRow(mods, "Alpha");
            int raised = 0;
            _ui.Invoke(() => mods.BuildAndInstallCommand.CanExecuteChanged += (_, _) => raised++);

            _ui.Invoke(() => vm.IsBusy = true);
            Assert.True(raised > 0, "меню не узнало, что экран сборки занят");
            Assert.False(_ui.Invoke(() => mods.BuildAndInstallCommand.CanExecute(null)));

            _ui.Invoke(() => mods.BuildAndInstallCommand.Execute(null));
            _ui.WaitUntil(() => !mods.BuildAndInstallCommand.IsRunning, "команда отработала");

            Assert.Empty(_ui.Invoke(() => runner.Calls.ToList()));
            Assert.Empty(asked);
            Assert.Contains("занят", _ui.Invoke(() => mods.StatusText), StringComparison.Ordinal);
            Assert.Equal(before, game.Snapshot());

            _ui.Invoke(() => vm.IsBusy = false);
            Assert.True(_ui.Invoke(() => mods.BuildAndInstallCommand.CanExecute(null)));
        }
        finally
        {
            _ui.Invoke(mods.Dispose);
        }
    }

    /// <summary>Чужой мод: проекта нет — собирать нечего, пункт погашен.</summary>
    [Fact]
    public void BuildAndInstall_ForeignMod_IsDisabled()
    {
        using FakeGameDir game = new();
        string drive = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha");
        game.AddMod("com.other.stuff_1.0.0.mtmod", FakeGameDir.Meta("com.other.stuff", "1.0.0", "Чужой мод", "чужое"));

        var runner = new RecordingBuildRunner(() => throw new InvalidOperationException("сборка чужого мода"));
        (_, ModsViewModel mods, _) = StartWithMods(game, drive, "1.1.0", runner, FakeGameProbes.Closed, answer: true);

        try
        {
            SelectInstalledRow(mods, "Чужой мод");
            Assert.False(_ui.Invoke(() => mods.BuildAndInstallCommand.CanExecute(null)));
        }
        finally
        {
            _ui.Invoke(mods.Dispose);
        }
    }

    private (MainViewModel Main, ModsViewModel Mods, List<(string Caption, string Body)> Asked) StartWithMods(
        FakeGameDir game,
        string projectDir,
        string projectVersion,
        RecordingBuildRunner runner,
        IGameRunningProbe probe,
        bool answer)
    {
        // Проект, который прочитает «Собрать и установить»; открытым на экране сборки он не станет.
        _projects.Project = new ModProject
        {
            ProjectDir = projectDir,
            ModId = "ru.example.alpha",
            Version = projectVersion,
            DisplayName = "Alpha"
        };
        _buildRunner = runner;
        MainViewModel vm = Start(capacity: 1000);

        var asked = new List<(string Caption, string Body)>();
        ModsViewModel mods = _ui.Invoke(() => new ModsViewModel(
            vm,
            new ModLibraryService(probe, new ModLibraryOptions
            {
                // журнал пользователя тест не трогает ни на запись, ни на чтение
                OperationLogPath = Path.Combine(game.Root, "mods-operations.log"),
                WriteOperationLog = false
            }),
            new FakeLocator { Detected = game.Install },
            new FakeSettingsStore { Settings = new CockpitSettings { LastProjectDir = projectDir } },
            new NoopEditorLauncher())
        {
            ConfirmHandler = (caption, body) =>
            {
                asked.Add((caption, body));
                return answer;
            }
        });

        _ui.Invoke(() => vm.IsModsScreen = true);
        return (vm, mods, asked);
    }

    private void SelectInstalledRow(ModsViewModel mods, string title)
    {
        _ui.WaitUntil(
            () => !mods.IsScanning && mods.Mods.Any(r => r.Title == title && r.IsInstalled),
            "экран «Моды» прочитал каталог",
            diagnostics: () => string.Join(", ", mods.Mods.Select(r => r.Title)));
        _ui.Invoke(() => mods.Selected = mods.Mods.First(r => r.Title == title && r.IsInstalled));
    }

    private static void WriteDriveArchive(string path, string version) =>
        FakeGameDir.WriteArchive(
            path,
            FakeGameDir.Meta("ru.example.alpha", version, "Alpha", "описание Alpha"),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_alpha.pyc", "fresh bytecode " + version)],
            compress: false);

    // ================================================================= подмостки

    private MainViewModel Start(int capacity, int primeKilobytes = 64, string projectDir = "")
    {
        string gameDir = Path.Combine(Path.GetTempPath(), "wot-cockpit-game-" + Guid.NewGuid().ToString("N"));

        _settings.Settings = new CockpitSettings
        {
            GameDir = gameDir,
            LastProjectDir = projectDir,
            LogBufferCapacity = capacity,
            LogPrimeTailKilobytes = primeKilobytes
        };

        _locator.Detected ??= new GameInstall(
            gameDir,
            "1.44.0.0",
            Path.Combine(gameDir, "mods", "1.44.0.0"),
            Path.Combine(gameDir, "res_mods", "1.44.0.0"),
            Path.Combine(gameDir, "python.log"));

        _projects.Project ??= projectDir.Length == 0
            ? null
            : new ModProject { ProjectDir = projectDir, ModId = "ru.local.hello_tanker", Version = "1.0.0" };

        MainViewModel vm = _ui.Invoke(() =>
        {
            var model = new MainViewModel(
                _settings,
                _locator,
                _projects,
                _buildRunner,
                new PythonLogParser(),
                _tailers,
                new WotSrcTracebackMapper(),
                _deploy,
                new NoopEditorLauncher())
            {
                ConfirmHandler = static (_, _) => false
            };

            model.Initialize();
            return model;
        });

        _vm = vm;
        return vm;
    }

    private static string Dump(MainViewModel vm) =>
        $"total={vm.TotalCount} visible={vm.VisibleCount} лента=[" +
        string.Join(" | ", vm.LogEntries.Take(12).Select(e => e.IsSystem ? "SYS" : e.Message)) + "]";

    // ---------------------------------------------------------------- подставные сервисы

    private sealed class FakeTailerFactory : ILogTailerFactory
    {
        public List<string> PrimeLines { get; } = new();

        public List<FakeTailer> Created { get; } = new();

        public FakeTailer Last => Created[^1];

        public ILogTailer Create(string filePath)
        {
            var tailer = new FakeTailer(filePath, PrimeLines);
            Created.Add(tailer);
            return tailer;
        }
    }

    /// <summary>
    /// Тейлер без файла: строки в него закладывает тест, события поднимаются из потока теста —
    /// то есть НЕ из UI-потока, ровно как у настоящего <see cref="PollingLogTailer"/>.
    /// </summary>
    private sealed class FakeTailer : ILogTailer
    {
        private readonly IReadOnlyList<string> _prime;

        public FakeTailer(string path, IReadOnlyList<string> prime)
        {
            FilePath = path;
            _prime = prime;
        }

        public string FilePath { get; }

        public bool IsRunning { get; private set; }

        public int LastPrimeBytes { get; private set; } = -1;

        public event EventHandler<string>? LineRead;

        public event EventHandler? FileTruncated;

        public event EventHandler<Exception>? Error;

        public void Start(int primeTailBytes = 64 * 1024)
        {
            LastPrimeBytes = primeTailBytes;
            IsRunning = true;

            foreach (string line in _prime)
                LineRead?.Invoke(this, line);
        }

        public void Stop() => IsRunning = false;

        public void Dispose() => IsRunning = false;

        public void Emit(string line) => LineRead?.Invoke(this, line);

        public void EmitTruncated() => FileTruncated?.Invoke(this, EventArgs.Empty);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EmitError(Exception ex) => Error?.Invoke(this, ex);
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public CockpitSettings Settings { get; set; } = new();

        public CockpitSettings? Saved { get; private set; }

        public string SettingsPath => @"<в тесте на диск ничего не пишется>";

        public CockpitSettings Load() => Settings;

        public void Save(CockpitSettings settings) => Saved = settings;
    }

    private sealed class FakeLocator : IGameInstallLocator
    {
        /// <summary>true = «каталог не похож на установку игры» (нет разбираемого paths.xml).</summary>
        public bool Fails { get; set; }

        public GameInstall? Detected { get; set; }

        public GameInstall? Detect(string rootPath) => Fails ? null : Detected;
    }

    private sealed class FakeProjectStore : IProjectStore
    {
        /// <summary>true = «каталог не похож на проект мода».</summary>
        public bool Fails { get; set; }

        public ModProject? Project { get; set; }

        public ModProject? Load(string dir) => Fails ? null : Project;

        public ModProject? Sniff(string dir) => Fails ? null : Project;

        public int SaveCalls { get; private set; }

        /// <summary>Пишет файл так же, как настоящий store: тесту важен факт появления файла.</summary>
        /// <param name="p">Проект.</param>
        public void Save(ModProject p)
        {
            SaveCalls++;
            File.WriteAllText(Path.Combine(p.ProjectDir, "wotmod.json"), "{ \"modId\": \"" + p.ModId + "\" }");
        }

        public IReadOnlyList<ProjectValidation> Validate(ModProject p) => Array.Empty<ProjectValidation>();

        public void SyncMetaXml(ModProject p)
        {
        }
    }

    private sealed class FakeDeployProbe : IDeployProbe
    {
        public DeployState State { get; set; } = DeployState.Absent;

        public DeployState Probe(GameInstall g, ModProject p) => State;
    }

    private sealed class NoopEditorLauncher : IEditorLauncher
    {
        public bool IsAvailable => false;

        public event EventHandler<string>? LaunchFailed;

        public void OpenAt(string filePath, int line) => LaunchFailed?.Invoke(this, "тест");

        public void OpenFolder(string folderPath) => LaunchFailed?.Invoke(this, "тест");
    }

    /// <summary>
    /// «build.py», который сразу успешно завершается, сделав с диском то, что задал тест, —
    /// например, удалив файл, как «Удалить из игры».
    /// </summary>
    private sealed class ScriptedBuildRunner(Action effect) : IBuildRunner
    {
        public IBuildRun Start(BuildAction action, ModProject project, BuildRunOptions? options = null)
            => throw new InvalidOperationException("экран сборки зовёт RunAsync");

        public Task<BuildResult> RunAsync(
            BuildAction action,
            ModProject project,
            BuildRunOptions? options,
            IProgress<BuildOutputLine>? progress,
            CancellationToken ct = default)
        {
            effect();
            return Task.FromResult(new BuildResult(true, 0, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }

    /// <summary>
    /// «build.py», который помнит, с каким действием и для какого проекта его звали, и
    /// отвечает тем, что задал тест (ответ может и сам что-то записать в dist\).
    /// </summary>
    private sealed class RecordingBuildRunner(Func<BuildResult> answer) : IBuildRunner
    {
        public List<(BuildAction Action, string ProjectDir)> Calls { get; } = new();

        public IBuildRun Start(BuildAction action, ModProject project, BuildRunOptions? options = null)
            => throw new InvalidOperationException("экран сборки зовёт RunAsync");

        public Task<BuildResult> RunAsync(
            BuildAction action,
            ModProject project,
            BuildRunOptions? options,
            IProgress<BuildOutputLine>? progress,
            CancellationToken ct = default)
        {
            Calls.Add((action, project.ProjectDir));
            return Task.FromResult(answer());
        }
    }

    /// <summary>Сборку тесты не запускают: если она всё же стартует, тест обязан упасть.</summary>
    private sealed class ThrowingBuildRunner : IBuildRunner
    {
        public IBuildRun Start(BuildAction action, ModProject project, BuildRunOptions? options = null)
            => throw new InvalidOperationException("тест не должен запускать build.py");

        public Task<BuildResult> RunAsync(
            BuildAction action,
            ModProject project,
            BuildRunOptions? options,
            IProgress<BuildOutputLine>? progress,
            CancellationToken ct = default)
            => throw new InvalidOperationException("тест не должен запускать build.py");
    }
}
