using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.ViewModels;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;
using WotModCockpit.Core.Project;

namespace WotModCockpit.Tests;

/// <summary>
/// Безопасность экрана «Моды» на уровне ИНТЕРФЕЙСА, а не службы.
/// <para>
/// Тесты службы (<see cref="ModsToggleTests"/>, <see cref="ModsLibraryServiceTests"/>) доказывают,
/// что Core отказывает в опасной операции. Здесь доказывается другое и не менее важное: что
/// экран до этого отказа вообще не доходит — что случайный клик не удаляет файл, что при
/// запущенной игре кнопки мертвы, и что при СТАРТЕ приложения в каталоге игры не меняется ни байта.
/// </para>
/// <para>
/// Всё происходит в ФЕЙКОВОМ каталоге игры во временной папке. Настоящая установка игры
/// здесь не участвует даже на чтение: каталог модов подставляется через фальшивый
/// <see cref="IGameInstallLocator"/>, список проектов — через фальшивый <see cref="ISettingsStore"/>.
/// </para>
/// </summary>
public sealed class ModsViewModelTests : IDisposable
{
    private readonly UiThread _ui = new();
    private Harness? _harness;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_harness is not null)
        {
            try
            {
                _ui.Invoke(() => _harness!.Dispose());
            }
            catch (Exception)
            {
                // закрытие стенда не должно маскировать причину падения самого теста
            }
        }

        _ui.Dispose();
    }

    // ================================================================= старт приложения

    /// <summary>
    /// ГЛАВНОЕ ПРАВИЛО: при старте приложения экран не делает ничего. Ни записи, ни чтения —
    /// каталог читается только тогда, когда на экран переключились.
    /// <para>
    /// Снимок каталога сверяется побайтово (путь + размер + sha256 + время записи) до создания
    /// модели, после создания и после первого чтения.
    /// </para>
    /// </summary>
    [Fact]
    public void Startup_ScansNothingAndChangesNothing()
    {
        string[] before;
        Harness h;

        using (FakeGameDir game = NewGame())
        {
            before = Snapshot(game.ModsDir);

            // Ровно то, что делает AppServices при старте: модель создаётся, экран не показан.
            h = Build(game, FakeGameProbes.Closed);
            _ui.Pump(2, 30);

            Assert.Equal(before, Snapshot(game.ModsDir));
            Assert.Equal(0, _ui.Invoke(() => h.Vm.Mods.Count));
            Assert.Equal("каталог модов ещё не читали", _ui.Invoke(() => h.Vm.Summary));

            // А теперь переключение на экран — это ЧТЕНИЕ и только чтение.
            Load(h);

            Assert.True(_ui.Invoke(() => h.Vm.Mods.Count) >= 3, "список не заполнился");
            Assert.Equal(before, Snapshot(game.ModsDir));
        }
    }

    /// <summary>
    /// Свои моды опознаны и помечены как свои, чужие — как чужие, выключенный виден выключенным.
    /// Это то, ради чего экран существует, и заодно страховка: отказ операции по чужому моду
    /// обязан опираться на верную пометку.
    /// </summary>
    [Fact]
    public void Scan_MarksMineForeignAndDisabled()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.Invoke(() =>
        {
            InstalledModViewModel mine = Row(h, "ru.example.alpha_1.0.0.mtmod");
            InstalledModViewModel foreign = Row(h, "tv.example.battleatlas_9.9.mtmod");
            InstalledModViewModel off = Row(h, "xvm_1.0.mtmod.off");

            Assert.True(mine.IsMine);
            Assert.Equal("МОЙ ПРОЕКТ", mine.OriginBadge);
            Assert.False(foreign.IsMine);
            Assert.Equal("ЧУЖОЙ", foreign.OriginBadge);
            Assert.True(off.IsInstalled);
            Assert.False(off.IsEnabled);
        });
    }

    /// <summary>Иконка своего мода доезжает из архива в строку списка — это видимый результат работы.</summary>
    [Fact]
    public void Scan_ShowsIconOfMyMod()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.WaitUntil(() => Row(h, "ru.example.alpha_1.0.0.mtmod").HasIcon, "иконка своего мода");
        _ui.Invoke(() => Assert.False(Row(h, "tv.example.battleatlas_9.9.mtmod").HasIcon));
    }

    /// <summary>
    /// У своего мода иконка берётся из СБОРКИ, когда в игре её ещё нет: нарисованная иконка
    /// лежит в <c>dist\</c> с момента сборки, и до установки мод не должен выглядеть безымянной
    /// плиткой с буквой. У чужого мода запасного пути нет — только то, что стоит в игре.
    /// </summary>
    [Fact]
    public void NotInstalledProject_ShowsIconFromItsBuild()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.WaitUntil(() => RowByTitle(h, "Delta Path").HasIcon, "иконка несобранного в игру мода");

        _ui.Invoke(() =>
        {
            InstalledModViewModel drive = RowByTitle(h, "Delta Path");
            Assert.False(drive.IsInstalled, "Delta Path не должен считаться установленным");
            Assert.True(drive.IsMine);
            Assert.EndsWith("ru.example.delta_1.0.0.mtmod", drive.IconSourcePath, StringComparison.OrdinalIgnoreCase);

            // Чужой мод без иконки внутри так и остаётся с плиткой: подставлять ему нечего.
            InstalledModViewModel foreign = Row(h, "tv.example.battleatlas_9.9.mtmod");
            Assert.Equal(string.Empty, foreign.IconSourcePath);
            Assert.False(foreign.HasIcon);
        });
    }

    // ================================================================= запущенная игра

    /// <summary>
    /// Игра запущена — все команды записи мертвы. Проверяется и <c>CanExecute</c> (кнопки погашены),
    /// и сама попытка выполнить команду в обход кнопки: каталог обязан остаться нетронутым.
    /// </summary>
    [Fact]
    public void GameRunning_DisablesEveryWriteCommand()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Running);
        Load(h);

        string[] before = Snapshot(game.ModsDir);

        _ui.Invoke(() =>
        {
            h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod");
            Assert.False(h.Vm.CanWrite, "запись разрешена при запущенной игре");
            Assert.False(h.Vm.DisableCommand.CanExecute(null), "кнопка «выключить» жива");
            Assert.False(h.Vm.DeleteCommand.CanExecute(null), "кнопка «удалить» жива");
            Assert.Contains("ИГРА ЗАПУЩЕНА", h.Vm.WriteLockReason, StringComparison.Ordinal);

            h.Vm.Selected = Row(h, "xvm_1.0.mtmod.off");
            Assert.False(h.Vm.EnableCommand.CanExecute(null), "кнопка «включить» жива");
        });

        // В обход кнопки: команда всё равно обязана остановиться на проверке.
        h.Confirm = (_, _) => true;
        _ui.Invoke(() => h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod"));
        Run(() => h.Vm.DisableCommand.ExecuteAsync(null));

        Assert.Equal(before, Snapshot(game.ModsDir));
        Assert.Contains("ИГРА ЗАПУЩЕНА", _ui.Invoke(() => h.Vm.StatusText), StringComparison.Ordinal);
    }

    /// <summary>
    /// «Выяснить не удалось» — тот же запрет. Неизвестность трактуется в пользу отказа:
    /// ошибиться тут можно ровно один раз.
    /// </summary>
    [Fact]
    public void GameStateUnknown_DisablesWrites()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Unknown);
        Load(h);

        _ui.Invoke(() =>
        {
            h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod");
            Assert.False(h.Vm.CanWrite);
            Assert.False(h.Vm.DisableCommand.CanExecute(null));
        });
    }

    // ================================================================= подтверждения

    /// <summary>Отказ в диалоге выключения — файл не тронут, ни одна операция не выполнена.</summary>
    [Fact]
    public void Disable_DeclinedInDialog_ChangesNothing()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string[] before = Snapshot(game.ModsDir);
        int asked = 0;
        h.Confirm = (_, _) =>
        {
            asked++;
            return false;
        };

        _ui.Invoke(() => h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod"));
        Run(() => h.Vm.DisableCommand.ExecuteAsync(null));

        Assert.Equal(1, asked);
        Assert.Equal(before, Snapshot(game.ModsDir));
    }

    /// <summary>
    /// Диалог выключения ЧУЖОГО мода обязан сказать, что мод чужой: пользователь ставил эти моды
    /// сам, и кокпит не знает, что без них сломается.
    /// </summary>
    [Fact]
    public void Disable_ForeignMod_WarnsThatItIsForeign()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string caption = string.Empty;
        string body = string.Empty;
        h.Confirm = (c, b) =>
        {
            caption = c;
            body = b;
            return false;
        };

        _ui.Invoke(() => h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod"));
        Run(() => h.Vm.DisableCommand.ExecuteAsync(null));

        Assert.Contains("ЧУЖОЙ", caption, StringComparison.Ordinal);
        Assert.Contains("ЭТО ЧУЖОЙ МОД", body, StringComparison.Ordinal);

        // В теле диалога обязан быть полный путь — и исходный, и итоговый.
        Assert.Contains(Path.Combine(game.ModsDir, "tv.example.battleatlas_9.9.mtmod"), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tv.example.battleatlas_9.9.mtmod.off", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>СЛУЧАЙНЫЙ КЛИК НЕ УДАЛЯЕТ: отказ в первом диалоге — второго даже не спрашивают.</summary>
    [Fact]
    public void Delete_DeclinedInFirstDialog_NeverAsksToTypeAndKeepsFile()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string[] before = Snapshot(game.ModsDir);
        int typedAsked = 0;
        h.Confirm = (_, _) => false;
        h.TypedConfirm = (_, _, expected) =>
        {
            typedAsked++;
            return expected;
        };

        _ui.Invoke(() => h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod"));
        Run(() => h.Vm.DeleteCommand.ExecuteAsync(null));

        Assert.Equal(0, typedAsked);
        Assert.Equal(before, Snapshot(game.ModsDir));
    }

    /// <summary>Второй шаг отменён (диалог ввода закрыт) — файл на месте.</summary>
    [Fact]
    public void Delete_TypedConfirmationCancelled_KeepsFile()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string[] before = Snapshot(game.ModsDir);
        h.Confirm = (_, _) => true;
        h.TypedConfirm = (_, _, _) => null;

        _ui.Invoke(() => h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod"));
        Run(() => h.Vm.DeleteCommand.ExecuteAsync(null));

        Assert.Equal(before, Snapshot(game.ModsDir));
        Assert.Equal("удаление отменено", _ui.Invoke(() => h.Vm.StatusText));
    }

    /// <summary>Напечатано не то имя — служба отказывает, файл на месте.</summary>
    [Fact]
    public void Delete_TypedWrongName_IsRefusedAndKeepsFile()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string[] before = Snapshot(game.ModsDir);
        string shown = string.Empty;
        h.Confirm = (_, body) =>
        {
            shown = body;
            return true;
        };

        h.TypedConfirm = (_, _, _) => "xvm_1.0.mtmod";

        _ui.Invoke(() => h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod"));
        Run(() => h.Vm.DeleteCommand.ExecuteAsync(null));

        Assert.Equal(before, Snapshot(game.ModsDir));

        // Отказ обязан быть виден в трёх местах: диалог, строка состояния, журнал.
        Assert.Contains("не совпало", shown, StringComparison.Ordinal);
        Assert.Contains("не совпало", _ui.Invoke(() => h.Vm.StatusText), StringComparison.Ordinal);
        Assert.Contains(
            _ui.Invoke(() => h.Vm.Journal.ToArray()),
            line => line.Contains("REFUSED", StringComparison.Ordinal)
                    && line.Contains("tv.example.battleatlas_9.9.mtmod", StringComparison.Ordinal));
    }

    /// <summary>
    /// Оба подтверждения пройдены — файл удалён, и ТОЛЬКО он. Тест нужен, чтобы предыдущие три
    /// не оказались пустыми: они доказывают запрет лишь тогда, когда разрешение работает.
    /// </summary>
    [Fact]
    public void Delete_BothConfirmationsPassed_RemovesOnlyThatFile()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string victim = Path.Combine(game.ModsDir, "tv.example.battleatlas_9.9.mtmod");
        string[] others = Snapshot(game.ModsDir).Where(s => !s.StartsWith("tv.example.battleatlas_9.9.mtmod|", StringComparison.OrdinalIgnoreCase)).ToArray();

        h.Confirm = (_, _) => true;
        h.TypedConfirm = (_, _, expected) => expected;

        _ui.Invoke(() => h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod"));
        Run(() => h.Vm.DeleteCommand.ExecuteAsync(null));

        Assert.False(File.Exists(victim));
        Assert.Equal(others, Snapshot(game.ModsDir));
    }

    // ================================================================= обратимость

    /// <summary>
    /// ВЫКЛЮЧЕНИЕ ОБРАТИМО. Полный круг через команды экрана: выключить → включить → выключить →
    /// включить. Файл обязан вернуться на то же место и совпасть побайтово, а каталог целиком —
    /// со снимком до первого клика.
    /// </summary>
    [Fact]
    public void DisableEnable_RoundTripThroughUi_RestoresByteIdenticalFile()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string enabled = Path.Combine(game.ModsDir, "ru.example.alpha_1.0.0.mtmod");
        string disabled = enabled + ModFileNames.DisabledSuffix;
        string[] before = Snapshot(game.ModsDir);
        string hashBefore = Sha256(enabled);

        h.Confirm = (_, _) => true;
        _ui.Invoke(() => h.Vm.Selected = Row(h, "ru.example.alpha_1.0.0.mtmod"));

        for (int round = 1; round <= 2; round++)
        {
            Run(() => h.Vm.DisableCommand.ExecuteAsync(null));

            Assert.False(File.Exists(enabled));
            Assert.True(File.Exists(disabled), $"круг {round}: выключенного файла нет");
            Assert.Equal(hashBefore, Sha256(disabled));

            // Именно так его видит движок: маской *.mtmod, рекурсивно.
            Assert.DoesNotContain(
                Directory.GetFiles(game.ModsDir, "*.mtmod", SearchOption.AllDirectories),
                p => string.Equals(p, enabled, StringComparison.OrdinalIgnoreCase));

            _ui.Invoke(() => h.Vm.Selected = Row(h, "ru.example.alpha_1.0.0.mtmod.off"));
            Run(() => h.Vm.EnableCommand.ExecuteAsync(null));

            Assert.True(File.Exists(enabled), $"круг {round}: файл не вернулся");
            Assert.False(File.Exists(disabled));
            Assert.Equal(hashBefore, Sha256(enabled));
            Assert.Equal(before, Snapshot(game.ModsDir));

            _ui.Invoke(() => h.Vm.Selected = Row(h, "ru.example.alpha_1.0.0.mtmod"));
        }
    }

    /// <summary>
    /// Путь-ловушка: строка списка подменена на файл ВНЕ каталога модов. Отказ обязан прийти
    /// от службы, а файл — остаться нетронутым. Это последняя линия: выше неё стоит ещё и то,
    /// что таких строк в списке не бывает.
    /// </summary>
    [Fact]
    public void Disable_PathOutsideModsDir_IsRefusedByService()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        // Файл рядом с каталогом игры — ровно то, что кокпит не имеет права трогать.
        string outside = Path.Combine(game.Root, "res", "packages", "gui.mtmod");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "чужое добро");
        byte[] bytes = File.ReadAllBytes(outside);

        ModOperationResult result = h.Library.Disable(game.Install, outside);

        Assert.False(result.Success);
        Assert.Equal(ModOperationStatus.RefusedOutsideModsDir, result.Status);
        Assert.Contains(game.ModsDir, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(outside));
        Assert.Equal(bytes, File.ReadAllBytes(outside));
    }

    // ================================================================= открыть проект

    /// <summary>
    /// «Открыть в VS Code» отдаёт редактору КАТАЛОГ ПРОЕКТА выбранного мода.
    /// <para>
    /// Команда старше меню правой кнопки — раньше её дёргала только кнопка справа. Но с
    /// появлением меню она стала жестом, которым пользуются не глядя, и путь, который уходит
    /// в редактор, обязан быть именно тем, что показан в карточке.
    /// </para>
    /// </summary>
    [Fact]
    public void OpenProject_HandsTheProjectDirToTheEditor()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.Invoke(() =>
        {
            InstalledModViewModel mine = Row(h, "ru.example.alpha_1.0.0.mtmod");
            h.Vm.Selected = mine;

            Assert.True(h.Vm.OpenProjectCommand.CanExecute(null));
            h.Vm.OpenProjectCommand.Execute(null);

            Assert.Equal(mine.ProjectDir, h.Editor.LastFolder);
            Assert.NotEqual(string.Empty, mine.ProjectDir);
        });
    }

    /// <summary>
    /// У ЧУЖОГО мода каталога проекта нет, и команда обязана быть недоступна: в меню правой
    /// кнопки это и есть погасший пункт. Проверка держит договор, на который опирается разметка.
    /// </summary>
    [Fact]
    public void OpenProject_IsDisabledForForeignMod()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.Invoke(() =>
        {
            InstalledModViewModel foreign = Row(h, "tv.example.battleatlas_9.9.mtmod");
            h.Vm.Selected = foreign;

            Assert.False(foreign.IsMine);
            Assert.Equal(string.Empty, foreign.ProjectDir);
            Assert.False(h.Vm.OpenProjectCommand.CanExecute(null));

            // И даже если команду дёрнуть мимо интерфейса — редактор не зовётся.
            h.Vm.OpenProjectCommand.Execute(null);
            Assert.Null(h.Editor.LastFolder);
        });
    }

    // ================================================================= имя и описание

    /// <summary>
    /// ГЛАВНАЯ ПРОВЕРКА ПЕРЕИМЕНОВАНИЯ: меняется ровно <c>&lt;name&gt;</c> в <c>src\meta.xml</c>
    /// проекта, а каталог игры не тронут ни одним байтом.
    /// <para>
    /// В диалог приходят значения из ФАЙЛА ПРОЕКТА, а не из строки списка: строка показывает
    /// архив в игре, а правится проект. В стенде они с самого начала различаются описанием —
    /// тем лучше: видно, откуда взято.
    /// </para>
    /// </summary>
    [Fact]
    public void Rename_ChangesOnlyTheProjectName_TheGameStaysUntouched()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string metaPath = string.Empty;
        string original = string.Empty;
        string[] before = [];
        MetaEditViewModel? shown = null;

        _ui.Invoke(() =>
        {
            InstalledModViewModel mine = Row(h, "ru.example.alpha_1.0.0.mtmod");
            h.Vm.Selected = mine;
            metaPath = Path.Combine(mine.ProjectDir, "src", "meta.xml");
            original = File.ReadAllText(metaPath);
            before = Snapshot(game.ModsDir);

            Assert.True(h.Vm.RenameCommand.CanExecute(null));
        });

        h.EditMeta = edit =>
        {
            shown = edit;
            edit.Name = "Alpha Path 2";
            return true;
        };
        Run(() => h.Vm.RenameCommand.ExecuteAsync(null));
        _ui.Pump(2, 30);

        Assert.NotNull(shown);
        Assert.Equal("Alpha Path", shown!.OriginalName);
        Assert.Equal("описание Alpha Path", shown.Description);        // из проекта, не «описание Alpha» из архива
        Assert.Equal(MetaEditFocus.Name, shown.Focus);

        Assert.Equal(original.Replace("<name>Alpha Path</name>", "<name>Alpha Path 2</name>"), File.ReadAllText(metaPath));
        Assert.Equal(before, Snapshot(game.ModsDir));

        _ui.Invoke(() =>
        {
            // Строка честно показывает то, что стоит в игре, а карточка — что в проекте уже новое.
            InstalledModViewModel row = Row(h, "ru.example.alpha_1.0.0.mtmod");
            Assert.Same(row, h.Vm.Selected);
            Assert.Equal("Alpha Path", row.Title);
            Assert.True(row.ProjectMetaDiffers);
            Assert.Contains("«Alpha Path 2»", row.ProjectMetaNotice);
            Assert.Contains("«Alpha Path» → «Alpha Path 2»", h.Vm.StatusText);
            Assert.Contains("после пересборки", h.Vm.StatusText);
        });
    }

    /// <summary>«Изменить описание…» открывает тот же диалог с курсором в описании и меняет только его.</summary>
    [Fact]
    public void EditDescription_StartsInTheDescription_AndChangesOnlyIt()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string metaPath = string.Empty;
        _ui.Invoke(() =>
        {
            InstalledModViewModel mine = Row(h, "ru.example.alpha_1.0.0.mtmod");
            h.Vm.Selected = mine;
            metaPath = Path.Combine(mine.ProjectDir, "src", "meta.xml");
        });

        MetaEditFocus? focus = null;
        h.EditMeta = edit =>
        {
            focus = edit.Focus;
            edit.Description = "Новое описание мода.";
            return true;
        };
        Run(() => h.Vm.EditDescriptionCommand.ExecuteAsync(null));

        Assert.Equal(MetaEditFocus.Description, focus);
        MetaXmlFields saved = MetaXmlEditor.Read(metaPath);
        Assert.Equal("Alpha Path", saved.Name);
        Assert.Equal("Новое описание мода.", saved.Description);
        _ui.Invoke(() => Assert.Contains("описание сохранено", h.Vm.StatusText));
    }

    /// <summary>Отмена в диалоге — файл проекта не тронут ни одним байтом.</summary>
    [Fact]
    public void Rename_Cancelled_WritesNothing()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string[] before = [];
        _ui.Invoke(() =>
        {
            h.Vm.Selected = Row(h, "ru.example.alpha_1.0.0.mtmod");
            before = Snapshot(game.Root);
        });

        h.EditMeta = edit =>
        {
            edit.Name = "Не сохранится";
            return false;
        };
        Run(() => h.Vm.RenameCommand.ExecuteAsync(null));

        Assert.Equal(before, Snapshot(game.Root));
        _ui.Invoke(() => Assert.Contains("не изменены", h.Vm.StatusText));
    }

    /// <summary>
    /// У ЧУЖОГО мода проекта нет: оба пункта погашены, а вызов в обход интерфейса не открывает
    /// диалог и ничего не пишет.
    /// </summary>
    [Fact]
    public void Rename_IsDisabledForForeignMod_AndWritesNothingEvenWhenCalledDirectly()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string[] before = [];
        _ui.Invoke(() =>
        {
            h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod");
            Assert.False(h.Vm.RenameCommand.CanExecute(null));
            Assert.False(h.Vm.EditDescriptionCommand.CanExecute(null));
            before = Snapshot(game.Root);
        });

        bool asked = false;
        h.EditMeta = _ =>
        {
            asked = true;
            return true;
        };
        Run(() => h.Vm.RenameCommand.ExecuteAsync(null));

        Assert.False(asked, "для чужого мода открылся диалог правки");
        Assert.Equal(before, Snapshot(game.Root));
    }

    /// <summary>
    /// Запущенная игра правке не мешает: меняется каталог ПРОЕКТА, а его игра не читает.
    /// Запрет записи на этом экране — про каталог модов игры.
    /// </summary>
    [Fact]
    public void Rename_IsAvailableWhileTheGameIsRunning()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Running);
        Load(h);

        _ui.Invoke(() =>
        {
            h.Vm.Selected = Row(h, "ru.example.alpha_1.0.0.mtmod");
            Assert.False(h.Vm.CanWrite, "стенд: игра должна быть запущена");
            Assert.True(h.Vm.RenameCommand.CanExecute(null));
        });
    }

    /// <summary>
    /// Проект, который в игру ещё не ставили: имя в списке берётся из проекта, поэтому меняется
    /// сразу. И выделение остаётся на строке — раньше строка «не установлен» теряла его при
    /// каждом перечитывании, потому что у неё нет пути к файлу.
    /// </summary>
    [Fact]
    public void Rename_NotInstalledProject_TitleFollowsAtOnce_AndTheRowStaysSelected()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.Invoke(() => h.Vm.Selected = RowByTitle(h, "Delta Path"));

        h.EditMeta = edit =>
        {
            edit.Name = "Delta Path Pro";
            return true;
        };
        Run(() => h.Vm.RenameCommand.ExecuteAsync(null));
        _ui.Pump(2, 30);

        _ui.Invoke(() =>
        {
            Assert.NotNull(h.Vm.Selected);
            Assert.False(h.Vm.Selected!.IsInstalled);
            Assert.Equal("Delta Path Pro", h.Vm.Selected.Title);
            Assert.DoesNotContain("пересборки", h.Vm.StatusText);

            // Но собранный архив в dist\ — ещё со старым именем, и поставить его сейчас значило
            // бы поставить старое.
            Assert.Contains("Сборка в dist\\ ещё со старыми", h.Vm.StatusText);
        });
    }

    /// <summary>
    /// Предупреждение «в проекте уже другое» появляется ИМЕННО от правки: до неё проект и
    /// архив совпадают, и карточка молчит. В общем стенде они различаются с самого начала, и
    /// проверка там проходила бы и без правки.
    /// </summary>
    [Fact]
    public void ProjectMetaNotice_IsSilentWhenTheyMatch_AndAppearsAfterRename()
    {
        using FakeGameDir game = new();
        _ = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha Path");
        game.AddMod("ru.example.alpha_1.0.0.mtmod", FakeGameDir.Meta("ru.example.alpha", "1.0.0", "Alpha Path", "описание Alpha Path"));
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.Invoke(() =>
        {
            InstalledModViewModel row = Row(h, "ru.example.alpha_1.0.0.mtmod");
            Assert.True(row.IsMine, "стенд: строка должна быть связана с проектом");
            Assert.False(row.ProjectMetaDiffers);
            Assert.Equal(string.Empty, row.ProjectMetaNotice);
            h.Vm.Selected = row;
        });

        h.EditMeta = edit =>
        {
            edit.Name = "Alpha Path 2";
            return true;
        };
        Run(() => h.Vm.RenameCommand.ExecuteAsync(null));
        _ui.Pump(2, 30);

        _ui.Invoke(() =>
        {
            InstalledModViewModel row = Row(h, "ru.example.alpha_1.0.0.mtmod");
            Assert.True(row.ProjectMetaDiffers);
            Assert.Contains("«Alpha Path 2»", row.ProjectMetaNotice);
        });
    }

    /// <summary>
    /// Предупреждение — только у файла, который игра действительно грузит. У выключенной и у
    /// лишней старой версии имя всегда «старое», и совет пересобрать их ничего бы не изменил.
    /// </summary>
    [Fact]
    public void ProjectMetaNotice_OnlyForTheFileTheGameLoads()
    {
        using FakeGameDir game = new();
        _ = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.1.0", "Alpha Path");
        game.AddMod("ru.example.alpha_1.1.0.mtmod", FakeGameDir.Meta("ru.example.alpha", "1.1.0", "Alpha Path", "описание Alpha Path"));
        string old = game.AddMod("ru.example.alpha_1.0.0.mtmod", FakeGameDir.Meta("ru.example.alpha", "1.0.0", "Old Path", "старое"));
        File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        game.AddMod("ru.example.alpha_0.9.0.mtmod.off", FakeGameDir.Meta("ru.example.alpha", "0.9.0", "Older Path", "ещё старее"));
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.Invoke(() =>
        {
            foreach (string file in new[] { "ru.example.alpha_1.0.0.mtmod", "ru.example.alpha_0.9.0.mtmod.off" })
            {
                InstalledModViewModel row = Row(h, file);
                Assert.True(row.IsMine, $"стенд: {file} должен быть связан с проектом");
                Assert.False(row.ProjectMetaDiffers, $"{file}: предупреждение у файла, который игра не грузит");
                Assert.Equal(string.Empty, row.ProjectMetaNotice);
            }

            Assert.False(Row(h, "ru.example.alpha_1.1.0.mtmod").ProjectMetaDiffers);
        });
    }

    /// <summary>
    /// В игре архив вовсе без meta.xml: сравнивать нечего, но сказать, что имя из проекта
    /// появится только после пересборки, надо. И описание проекта не выдаётся за описание из
    /// игры — в архиве его нет.
    /// </summary>
    [Fact]
    public void ArchiveWithoutMeta_SaysTheNameComesAfterRebuild_AndShowsNoProjectDescription()
    {
        using FakeGameDir game = new();
        _ = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha Path");
        game.AddMod("ru.example.alpha_1.0.0.mtmod", metaXml: null);
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.Invoke(() =>
        {
            InstalledModViewModel row = Row(h, "ru.example.alpha_1.0.0.mtmod");
            Assert.True(row.IsMine, "стенд: строка должна быть связана с проектом по имени файла");
            Assert.Contains("без meta.xml", row.ProjectMetaNotice);
            Assert.Equal(string.Empty, row.Description);
        });
    }

    /// <summary>У строки «не установлен» описание — из проекта: другого у неё нет.</summary>
    [Fact]
    public void NotInstalledRow_DescriptionComesFromTheProject()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.Invoke(() => Assert.Equal("описание Delta Path", RowByTitle(h, "Delta Path").Description));
    }

    /// <summary>
    /// Пункты гаснут, когда src\meta.xml проекта нет или он битый: окно открылось бы только
    /// затем, чтобы сразу отказать.
    /// </summary>
    [Fact]
    public void Rename_IsDisabled_WhenTheProjectMetaIsMissingOrBroken()
    {
        using FakeGameDir game = new();
        string broken = game.AddProject("wot-mod-broken", "ru.local.broken", "1.0.0", "Broken", buildArtifact: false);
        string metaPath = Path.Combine(broken, "src", "meta.xml");
        File.WriteAllText(metaPath, File.ReadAllText(metaPath).Replace("</name>", "</nme>"));
        string missing = game.AddProject("wot-mod-nometa", modId: null, buildArtifact: false);
        game.AddMod("tv.example.battleatlas_9.9.mtmod", FakeGameDir.Meta("tv.example.battleatlas", "9.9", "Battle Atlas", "чужой"));
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.Invoke(() =>
        {
            foreach (string dir in new[] { broken, missing })
            {
                InstalledModViewModel row = h.Vm.Mods.First(r => string.Equals(
                    Path.TrimEndingDirectorySeparator(r.ProjectDir), Path.TrimEndingDirectorySeparator(dir), StringComparison.OrdinalIgnoreCase));
                h.Vm.Selected = row;
                Assert.False(h.Vm.RenameCommand.CanExecute(null), $"{Path.GetFileName(dir)}: пункт доступен");
                Assert.False(h.Vm.EditDescriptionCommand.CanExecute(null), $"{Path.GetFileName(dir)}: пункт доступен");
            }
        });
    }

    /// <summary>
    /// Пока окно было открыто, описание поправили в редакторе. Окно меняло только имя — и
    /// описание из редактора остаётся: снимок, взятый при открытии окна, в файл не уходит.
    /// </summary>
    [Fact]
    public void Rename_DoesNotRollBackTheDescriptionEditedMeanwhile()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string metaPath = string.Empty;
        _ui.Invoke(() =>
        {
            InstalledModViewModel mine = Row(h, "ru.example.alpha_1.0.0.mtmod");
            h.Vm.Selected = mine;
            metaPath = Path.Combine(mine.ProjectDir, "src", "meta.xml");
        });

        h.EditMeta = edit =>
        {
            // «В VS Code» — пока окно открыто.
            File.WriteAllText(metaPath, File.ReadAllText(metaPath).Replace("описание Alpha Path", "поправлено в редакторе"));
            edit.Name = "Alpha Path 2";
            return true;
        };
        Run(() => h.Vm.RenameCommand.ExecuteAsync(null));

        MetaXmlFields saved = MetaXmlEditor.Read(metaPath);
        Assert.Equal("Alpha Path 2", saved.Name);
        Assert.Equal("поправлено в редакторе", saved.Description);
    }

    /// <summary>
    /// Пока окно было открыто, файл уже поправили ровно так же: записывать нечего, и об этом
    /// сказано, а не «сохранено».
    /// </summary>
    [Fact]
    public void Rename_ToWhatIsAlreadyInTheFile_SaysThereWasNothingToWrite()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string metaPath = string.Empty;
        _ui.Invoke(() =>
        {
            InstalledModViewModel mine = Row(h, "ru.example.alpha_1.0.0.mtmod");
            h.Vm.Selected = mine;
            metaPath = Path.Combine(mine.ProjectDir, "src", "meta.xml");
        });

        byte[] meanwhile = [];
        h.EditMeta = edit =>
        {
            File.WriteAllText(metaPath, File.ReadAllText(metaPath).Replace("<name>Alpha Path</name>", "<name>Alpha Path 2</name>"));
            meanwhile = File.ReadAllBytes(metaPath);
            edit.Name = "Alpha Path 2";
            return true;
        };
        Run(() => h.Vm.RenameCommand.ExecuteAsync(null));

        Assert.Equal(meanwhile, File.ReadAllBytes(metaPath));
        _ui.Invoke(() => Assert.Contains("записывать нечего", h.Vm.StatusText));
    }

    /// <summary>
    /// Новое имя больше не подходит под строку поиска — строка пропадает из списка, и итог
    /// правки говорит почему. Иначе со стороны это выглядело бы как пропавший мод.
    /// </summary>
    [Fact]
    public void Rename_RowHiddenBySearch_IsExplained()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        _ui.Invoke(() =>
        {
            h.Vm.SearchText = "Delta P";
            h.Vm.Selected = RowByTitle(h, "Delta Path");
        });

        h.EditMeta = edit =>
        {
            edit.Name = "Новое имя";
            return true;
        };
        Run(() => h.Vm.RenameCommand.ExecuteAsync(null));
        _ui.Pump(2, 30);

        _ui.Invoke(() =>
        {
            Assert.DoesNotContain(h.Vm.Mods, r => r.Title == "Новое имя");
            Assert.Contains("скрыта поиском «Delta P»", h.Vm.StatusText);
        });
    }

    /// <summary>
    /// Битый meta.xml проекта не правится: диалог не открывается, файл остаётся прежним, а
    /// причина показана. Готовый писатель кокпита заменил бы такой файл пустым корнем.
    /// </summary>
    [Fact]
    public void Rename_BrokenProjectMeta_IsRefused_AndTheFileStays()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        string metaPath = string.Empty;
        byte[] before = [];
        _ui.Invoke(() =>
        {
            InstalledModViewModel mine = Row(h, "ru.example.alpha_1.0.0.mtmod");
            h.Vm.Selected = mine;
            metaPath = Path.Combine(mine.ProjectDir, "src", "meta.xml");
            File.WriteAllText(metaPath, File.ReadAllText(metaPath).Replace("</name>", "</nme>"));
            before = File.ReadAllBytes(metaPath);
        });

        bool asked = false;
        string? complaint = null;
        h.EditMeta = _ =>
        {
            asked = true;
            return true;
        };
        h.Confirm = (_, body) =>
        {
            complaint = body;
            return false;
        };
        Run(() => h.Vm.RenameCommand.ExecuteAsync(null));

        Assert.False(asked, "для битого meta.xml открылся диалог правки");
        Assert.NotNull(complaint);
        Assert.Contains("битый", complaint);
        Assert.Equal(before, File.ReadAllBytes(metaPath));
    }

    // ================================================================= список не устаревает

    /// <summary>
    /// ИМЕННО ТА ОШИБКА, КОТОРУЮ НАШЁЛ ПОЛЬЗОВАТЕЛЬ. Экран «Моды» уже открывали; мод удалили
    /// мимо него — кнопкой «Удалить из игры» на экране «Сборка и лог» (build.py --uninstall
    /// удаляет файл сам) или проводником; на экран вернулись. Раньше каталог читался один раз
    /// за запуск, и удалённый мод висел в списке «ВКЛЮЧЁН». Теперь каталог перечитывается при
    /// каждом показе, и строка становится «не установлен» — она же и выделена.
    /// </summary>
    [Fact]
    public void FileRemovedBehindTheScreen_ReturningToTheScreen_ShowsItAsNotInstalled()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);
        WaitQuiet(h);

        string file = Path.Combine(game.ModsDir, "ru.example.alpha_1.0.0.mtmod");
        _ui.Invoke(() =>
        {
            h.Vm.Selected = Row(h, "ru.example.alpha_1.0.0.mtmod");
            h.Vm.Suspend();   // ушли на «Сборку и лог»
        });

        File.Delete(file);   // build.py --uninstall: os.remove, мимо службы библиотеки

        // Скрытый экран за каталогом не следит и ничего не читает — он перечитает всё при показе.
        _ui.Pump(20, 60);
        _ui.Invoke(() => Assert.Contains(h.Vm.Mods, r => r.FileName == "ru.example.alpha_1.0.0.mtmod"));

        Run(() => h.Vm.EnsureLoadedAsync());   // вернулись на «Моды»

        _ui.Invoke(() =>
        {
            Assert.DoesNotContain(h.Vm.Mods, r => r.FileName == "ru.example.alpha_1.0.0.mtmod");
            InstalledModViewModel row = RowByTitle(h, "Alpha Path");
            Assert.False(row.IsInstalled);
            Assert.Same(row, h.Vm.Selected);
        });
    }

    /// <summary>
    /// Экран показан, а файл удалили рядом — терминалом, проводником, build.py. Список
    /// следует за каталогом сам, без кнопки «обновить»: за каталогом следит наблюдатель.
    /// </summary>
    [Fact]
    public void FileRemovedWhileTheScreenIsShown_ListFollowsByItself()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);
        WaitQuiet(h);

        File.Delete(Path.Combine(game.ModsDir, "ru.example.alpha_1.0.0.mtmod"));

        _ui.WaitUntil(
            () => h.Vm.Mods.All(r => r.FileName != "ru.example.alpha_1.0.0.mtmod"),
            "удалённый файл пропал из списка",
            TimeSpan.FromSeconds(10),
            () => string.Join(", ", h.Vm.Mods.Select(r => r.FileName + (r.IsInstalled ? "" : "(нет)"))));
        _ui.Invoke(() => Assert.False(RowByTitle(h, "Alpha Path").IsInstalled));
    }

    /// <summary>
    /// Сигнал «каталог мог измениться» (его подаёт экран «Сборка и лог» после build.py)
    /// перечитывает показанный экран — даже когда поменялось то, за чем наблюдатель не следит:
    /// сборка в dist\ проекта.
    /// </summary>
    [Fact]
    public void NotifyExternalChange_WhileShown_RereadsEvenOutsideTheModsDir()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);
        WaitQuiet(h);

        string dist = string.Empty;
        _ui.Invoke(() =>
        {
            InstalledModViewModel drive = RowByTitle(h, "Delta Path");
            Assert.True(drive.HasBuiltArtifact, "стенд: у Delta Path должна быть сборка в dist");
            dist = drive.BuiltArtifactPath;
        });

        File.Delete(dist);

        // Без сигнала список НЕ перечитывается: dist\ наблюдатель не видит. Без этой проверки
        // тест проходил бы и с выключенным сигналом — от случайного перечитывания.
        _ui.Pump(15, 60);
        _ui.Invoke(() => Assert.True(
            RowByTitle(h, "Delta Path").HasBuiltArtifact,
            "список перечитался без сигнала — проверка сигнала была бы пустой"));

        _ui.Invoke(() => h.Vm.NotifyExternalChange());

        _ui.WaitUntil(
            () => !RowByTitle(h, "Delta Path").HasBuiltArtifact,
            "строка Delta Path узнала, что сборки в dist больше нет");
    }

    /// <summary>
    /// Ни разу не открытый экран на сигнал не читает ничего: при старте приложения каталог
    /// игры не трогается даже на чтение.
    /// </summary>
    [Fact]
    public void NotifyExternalChange_BeforeTheScreenWasEverShown_ReadsNothing()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);

        _ui.Invoke(() => h.Vm.NotifyExternalChange());
        _ui.Pump(12, 60);

        _ui.Invoke(() =>
        {
            Assert.Empty(h.Vm.Mods);
            Assert.False(h.Vm.IsScanning);
        });
    }

    /// <summary>
    /// Удалили свой мод на этом экране: выделение переходит на строку его проекта — карточка
    /// сразу говорит «в игре не установлен», а не пустеет. Итог удаления остаётся в строке
    /// состояния: раньше его тут же стирало «прочитано N файлов».
    /// </summary>
    [Fact]
    public void Delete_OwnMod_SelectsItsProjectRow_AndKeepsTheResultInTheStatus()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        h.Confirm = (_, _) => true;
        h.TypedConfirm = (_, _, expected) => expected;

        _ui.Invoke(() => h.Vm.Selected = Row(h, "ru.example.alpha_1.0.0.mtmod"));
        Run(() => h.Vm.DeleteCommand.ExecuteAsync(null));

        // Удаление даёт событие наблюдателю, и следом идёт его тихое перечитывание: итог и
        // выделение обязаны пережить и его.
        WaitQuiet(h);

        _ui.Invoke(() =>
        {
            Assert.NotNull(h.Vm.Selected);
            Assert.False(h.Vm.Selected!.IsInstalled);
            Assert.Equal("Alpha Path", h.Vm.Selected.Title);
            Assert.DoesNotContain("прочитано", h.Vm.StatusText);
            Assert.Contains("удал", h.Vm.StatusText);
        });
    }

    /// <summary>
    /// Кнопку нажали на строке, чей файл уже исчез: объяснение «файл исчез с диска» остаётся на
    /// виду. Раньше его в тот же миг стирало «чтение каталога модов…» перечитывания.
    /// </summary>
    [Fact]
    public void ActionOnVanishedFile_TheExplanationStaysVisible()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);
        WaitQuiet(h);

        _ui.Invoke(() =>
        {
            h.Vm.Selected = Row(h, "ru.example.alpha_1.0.0.mtmod");
            h.Vm.Suspend();   // наблюдатель выключен: строка остаётся устаревшей до нажатия
        });
        File.Delete(Path.Combine(game.ModsDir, "ru.example.alpha_1.0.0.mtmod"));

        Run(() => h.Vm.DisableCommand.ExecuteAsync(null));
        _ui.WaitUntil(() => !h.Vm.IsScanning, "перечитывание закончилось");

        _ui.Invoke(() =>
        {
            Assert.Contains("файл исчез с диска", h.Vm.StatusText);
            Assert.DoesNotContain(h.Vm.Mods, r => r.FileName == "ru.example.alpha_1.0.0.mtmod");
        });
    }

    /// <summary>
    /// Пока открыты диалоги удаления, список обновляться не должен мешать удалению. Две вещи:
    /// <list type="bullet">
    ///   <item>наблюдатель под диалогом ждёт конца команды — сигнал «каталог изменился» под
    ///   первым диалогом не перечитывает список;</item>
    ///   <item>а перечитывание, которое всё-таки закончилось под диалогом (начато раньше),
    ///   заменяет строку ТЕМ ЖЕ файлом — и это не повод отказывать «выбор изменился». Раньше
    ///   сверялся объект строки, и удаление ложно отменялось.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Delete_ListRereadUnderTheDialogs_IsNotRefused_AndTheWatcherWaits()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);
        WaitQuiet(h);

        string victim = Path.Combine(game.ModsDir, "tv.example.battleatlas_9.9.mtmod");
        int scans = 0;
        _ui.Invoke(() =>
        {
            h.Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModsViewModel.IsScanning) && h.Vm.IsScanning)
                {
                    scans++;
                }
            };
            h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod");
        });

        int scansUnderFirstDialog = -1;
        h.Confirm = (_, _) =>
        {
            // Модальное окно крутит свой цикл сообщений — таймер наблюдателя в нём тикает.
            int before = scans;
            h.Vm.NotifyExternalChange();
            PumpNested(TimeSpan.FromMilliseconds(1200));
            scansUnderFirstDialog = scans - before;
            return true;
        };
        h.TypedConfirm = (_, _, expected) =>
        {
            // Перечитывание, которое закончилось, пока пользователь печатал имя файла.
            PumpUntil(h.Vm.RefreshCommand.ExecuteAsync(null));
            return expected;
        };

        Run(() => h.Vm.DeleteCommand.ExecuteAsync(null));

        Assert.Equal(0, scansUnderFirstDialog);
        Assert.False(File.Exists(victim), "удаление ложно отменено");
        _ui.Invoke(() => Assert.DoesNotContain("выбор изменился", h.Vm.StatusText));
    }

    /// <summary>
    /// После операции выделяется строка её итога — но только если она видна. Под фильтром
    /// «Выключенные» включённый мод из списка уходит, и карточка не должна показывать то, чего
    /// в списке нет.
    /// </summary>
    [Fact]
    public void Enable_UnderTheDisabledFilter_DoesNotSelectAHiddenRow()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);
        WaitQuiet(h);

        h.Confirm = (_, _) => true;
        _ui.Invoke(() =>
        {
            h.Vm.Filter = ModsFilter.Disabled;
            h.Vm.Selected = Row(h, "xvm_1.0.mtmod.off");
        });

        Run(() => h.Vm.EnableCommand.ExecuteAsync(null));

        // Сразу после операции — и после тихого перечитывания, которое идёт за ней от
        // наблюдателя (без первой проверки оно маскировало бы ошибку, сняв выделение само).
        _ui.Invoke(() => Assert.True(
            h.Vm.Selected is null || h.Vm.Mods.Contains(h.Vm.Selected),
            "сразу после операции выделена строка, которой нет в списке: " + h.Vm.Selected?.FileName));
        WaitQuiet(h);

        Assert.True(File.Exists(Path.Combine(game.ModsDir, "xvm_1.0.mtmod")), "стенд: мод должен включиться");
        _ui.Invoke(() => Assert.True(
            h.Vm.Selected is null || h.Vm.Mods.Contains(h.Vm.Selected),
            "выделена строка, которой нет в списке: " + h.Vm.Selected?.FileName));
    }

    /// <summary>
    /// Служба бросила исключение вместо итога — запись о сбое называет НАСТОЯЩУЮ операцию.
    /// Раньше любой такой сбой уходил в журнал как ENABLE.
    /// </summary>
    [Fact]
    public void OperationThrows_TheJournalNamesTheRealOperation()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed, inner => new ThrowingDeleteLibrary(inner));
        Load(h);

        h.Confirm = (_, _) => true;
        h.TypedConfirm = (_, _, expected) => expected;
        _ui.Invoke(() => h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod"));
        Run(() => h.Vm.DeleteCommand.ExecuteAsync(null));

        string line = _ui.Invoke(() => h.Vm.Journal.First(l => l.Contains("отказ диска в тесте", StringComparison.Ordinal)));
        Assert.Contains("DELETE", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENABLE", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Перечитывание не превращает иконки в плитки с буквой: новая строка получает иконку
    /// прежней сразу, ещё до того, как иконки прочитаются заново. Перечитываний теперь много
    /// (показ экрана, наблюдатель), и без этого список мигал бы при каждом.
    /// </summary>
    [Fact]
    public async Task Reread_KeepsIconsWhileTheyAreReadAgain()
    {
        using FakeGameDir game = NewGame();
        using var gate = new ManualResetEventSlim(true);
        Harness h = Build(game, FakeGameProbes.Closed, inner => new GatedIconLibrary(inner, gate));
        Load(h);
        _ui.WaitUntil(() => Row(h, "ru.example.alpha_1.0.0.mtmod").HasIcon, "иконка своего мода");
        WaitQuiet(h);

        InstalledModViewModel before = _ui.Invoke(() => Row(h, "ru.example.alpha_1.0.0.mtmod"));
        gate.Reset();   // иконки больше не читаются, пока тест не отпустит
        Task refresh = _ui.Invoke(() => h.Vm.RefreshCommand.ExecuteAsync(null));
        try
        {
            _ui.WaitUntil(
                () => !ReferenceEquals(Row(h, "ru.example.alpha_1.0.0.mtmod"), before),
                "список перестроен новыми строками");
            _ui.Invoke(() => Assert.True(
                Row(h, "ru.example.alpha_1.0.0.mtmod").HasIcon,
                "новая строка без иконки, пока иконки читаются заново"));
        }
        finally
        {
            gate.Set();

            // Дождаться перечитывания, но не бросать по таймауту: исключение из finally спрятало
            // бы настоящую причину падения из блока выше.
            await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(30)));
        }
    }

    /// <summary>
    /// Возврат на экран перечитывает каталог тихо: в строке состояния остаётся итог последнего
    /// действия пользователя, а не «прочитано N файлов».
    /// </summary>
    [Fact]
    public void ReturningToTheScreen_KeepsTheLastResultInTheStatus()
    {
        using FakeGameDir game = NewGame();
        Harness h = Build(game, FakeGameProbes.Closed);
        Load(h);

        h.Confirm = (_, _) => true;
        h.TypedConfirm = (_, _, expected) => expected;
        _ui.Invoke(() => h.Vm.Selected = Row(h, "tv.example.battleatlas_9.9.mtmod"));
        Run(() => h.Vm.DeleteCommand.ExecuteAsync(null));
        string result = _ui.Invoke(() => h.Vm.StatusText);

        _ui.Invoke(() => h.Vm.Suspend());
        Run(() => h.Vm.EnsureLoadedAsync());

        _ui.Invoke(() => Assert.Equal(result, h.Vm.StatusText));
    }

    /// <summary>Вложенный цикл сообщений на время — так его крутит модальное окно.</summary>
    private static void PumpNested(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>Вложенный цикл сообщений, пока задача не закончится.</summary>
    private static void PumpUntil(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    // ================================================================= стенд

    private static FakeGameDir NewGame()
    {
        FakeGameDir game = new();

        _ = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha Path");

        // Собранный, но не поставленный в игру проект — с иконкой внутри сборки.
        string drive = game.AddProject("wot-mod-delta", "ru.example.delta", "1.0.0", "Delta Path");
        FakeGameDir.WriteArchive(
            Path.Combine(drive, "dist", "ru.example.delta_1.0.0.mtmod"),
            FakeGameDir.Meta("ru.example.delta", "1.0.0", "Delta Path", "описание Delta"),
            [
                FakeGameDir.Entry("res/scripts/client/gui/mods/mod_delta.pyc", "bytecode"),
                FakeGameDir.IconEntry("res/gui/maps/icons/mods/ru.example.delta/modsListApi.png")
            ],
            compress: false);

        // Свой мод — с иконкой внутри, как её кладёт кузница иконок.
        game.AddMod(
            "ru.example.alpha_1.0.0.mtmod",
            FakeGameDir.Meta("ru.example.alpha", "1.0.0", "Alpha Path", "описание Alpha"),
            [
                FakeGameDir.Entry("res/scripts/client/gui/mods/mod_alpha.pyc", "bytecode"),
                FakeGameDir.IconEntry("res/gui/maps/icons/mods/ru.example.alpha/modsListApi.png")
            ]);

        // Чужие моды: один в корне, один в подпапке, один уже выключенный.
        game.AddMod(
            "tv.example.battleatlas_9.9.mtmod",
            FakeGameDir.Meta("tv.example.battleatlas", "9.9", "Battle Atlas", "чужой мод сборки"));
        game.AddMod("audioww/tv.example.sounds_1.2.mtmod", metaXml: null);
        game.AddMod("xvm_1.0.mtmod.off", FakeGameDir.Meta("com.modxvm.xfw", "1.0", "XVM", "выключен"));

        return game;
    }

    private Harness Build(
        FakeGameDir game,
        IGameRunningProbe probe,
        Func<IModLibraryService, IModLibraryService>? wrapLibrary = null) =>
        _harness = _ui.Invoke(() => new Harness(game, probe, wrapLibrary));

    private void Load(Harness h)
    {
        Run(() => h.Vm.EnsureLoadedAsync());
        _ui.Pump(2, 30);
    }

    /// <summary>
    /// Ждёт, пока экран затихнет: две секунды подряд ни одного нового чтения каталога — с
    /// запасом больше паузы наблюдателя (0,4 с), чтобы взведённое, но ещё не начатое
    /// перечитывание успело себя показать.
    /// <para>
    /// Сразу после того, как стенд записал файлы, Windows ещё досылает наблюдателю за каталогом
    /// запоздалые события о них, и экран сам тихо перечитывает список. Тесту, который проверяет,
    /// КТО вызвал перечитывание, это перечитывание подмешало бы ложный повод.
    /// </para>
    /// </summary>
    private void WaitQuiet(Harness h)
    {
        int scans = 0;
        void Count(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModsViewModel.IsScanning) && h.Vm.IsScanning)
            {
                scans++;
            }
        }

        _ui.Invoke(() => h.Vm.PropertyChanged += Count);
        try
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            var quiet = System.Diagnostics.Stopwatch.StartNew();
            int seen = _ui.Invoke(() => scans);
            while (total.Elapsed < TimeSpan.FromSeconds(15))
            {
                Thread.Sleep(100);
                (int now, bool scanning) = _ui.Invoke(() => (scans, h.Vm.IsScanning));
                if (now != seen || scanning)
                {
                    seen = now;
                    quiet.Restart();
                }
                else if (quiet.Elapsed > TimeSpan.FromSeconds(2))
                {
                    return;
                }
            }

            Assert.Fail("экран «Моды» не затих за 15 секунд: каталог перечитывается без конца");
        }
        finally
        {
            _ui.Invoke(() => h.Vm.PropertyChanged -= Count);
        }
    }

    private void Run(Func<Task> action)
    {
        Task task = _ui.Invoke(action);
        if (!task.Wait(TimeSpan.FromSeconds(30)))
        {
            Assert.Fail("операция экрана не завершилась за 30 секунд");
        }
    }

    private static InstalledModViewModel RowByTitle(Harness h, string title) =>
        h.Vm.Mods.FirstOrDefault(r => string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"в списке нет строки «{title}»; есть: {string.Join(", ", h.Vm.Mods.Select(r => r.Title))}");

    private static InstalledModViewModel Row(Harness h, string fileName) =>
        h.Vm.Mods.FirstOrDefault(r => string.Equals(r.FileName, fileName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"в списке нет строки «{fileName}»; есть: {string.Join(", ", h.Vm.Mods.Select(r => r.FileName))}");

    /// <summary>
    /// Снимок каталога: относительный путь, размер, sha256 и время записи каждого файла.
    /// Сравнение снимков — это и есть проверка «ничего не тронуто».
    /// </summary>
    private static string[] Snapshot(string dir)
    {
        string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        return files.Select(f =>
        {
            FileInfo info = new(f);
            return $"{Path.GetRelativePath(dir, f)}|{info.Length}|{Sha256(f)}|{info.LastWriteTimeUtc.Ticks}";
        }).ToArray();
    }

    private static string Sha256(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>
    /// Экран «Моды» поверх фейкового каталога: настоящая служба Core, настоящая модель экрана,
    /// подставные проба процессов, локатор игры и хранилище настроек.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Harness(FakeGameDir game, IGameRunningProbe probe, Func<IModLibraryService, IModLibraryService>? wrapLibrary = null)
        {
            Library = new ModLibraryService(probe, new ModLibraryOptions
            {
                // Журнал операций пользователя тесты не трогают ни на запись, ни на чтение.
                OperationLogPath = Path.Combine(game.Root, "mods-operations.log"),
                WriteOperationLog = false
            });

            _provider = App.AppServices.BuildServiceProvider();
            Main = _provider.GetRequiredService<MainViewModel>();
            Main.GameDir = game.Root;

            Editor = new SilentEditor();
            Vm = new ModsViewModel(
                Main,
                wrapLibrary is null ? Library : wrapLibrary(Library),
                new FixedLocator(game.Install),
                new FixedSettings(Path.Combine(game.Root, "projects", "wot-mod-alpha")),
                Editor)
            {
                ConfirmHandler = (c, b) => Confirm(c, b),
                TypedConfirmHandler = (c, b, e) => TypedConfirm(c, b, e),
                EditMetaHandler = edit => EditMeta(edit)
            };
        }

        public ModLibraryService Library { get; }

        public MainViewModel Main { get; }

        public ModsViewModel Vm { get; }

        /// <summary>Заглушка редактора: помнит, какой каталог просили открыть.</summary>
        public SilentEditor Editor { get; }

        /// <summary>Ответ на диалог подтверждения. По умолчанию — отказ: тест обязан разрешать явно.</summary>
        public Func<string, string, bool> Confirm { get; set; } = (_, _) => false;

        /// <summary>Ответ на ввод имени файла. По умолчанию — отмена.</summary>
        public Func<string, string, string, string?> TypedConfirm { get; set; } = (_, _, _) => null;

        /// <summary>
        /// Ответ на диалог «Имя и описание мода». По умолчанию — отмена: неподменённый диалог
        /// открыл бы модальное окно и повесил тест.
        /// </summary>
        public Func<MetaEditViewModel, bool> EditMeta { get; set; } = _ => false;

        public void Dispose()
        {
            Vm.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class FixedLocator(GameInstall install) : IGameInstallLocator
    {
        public GameInstall? Detect(string rootPath) => install;
    }

    private sealed class FixedSettings(string projectDir) : ISettingsStore
    {
        public string SettingsPath => Path.Combine(projectDir, "settings.json");

        public CockpitSettings Load() => new() { LastProjectDir = projectDir };

        public void Save(CockpitSettings settings)
        {
            // тесты настройки не пишут
        }
    }

    /// <summary>
    /// Настоящая служба библиотеки, у которой удаление бросает исключение вместо итога — как
    /// бросила бы при сбое, который служба не предусмотрела.
    /// </summary>
    private sealed class ThrowingDeleteLibrary(IModLibraryService inner) : IModLibraryService
    {
        public string OperationLogPath => inner.OperationLogPath;

        public ModLibraryScan Scan(GameInstall game, IReadOnlyList<string>? projectDirs = null) => inner.Scan(game, projectDirs);

        public InstalledMod? Refresh(GameInstall game, string fullPath, IReadOnlyList<string>? projectDirs = null) =>
            inner.Refresh(game, fullPath, projectDirs);

        public ResModsSummary ScanResMods(GameInstall game) => inner.ScanResMods(game);

        public GameRunningState CheckGame() => inner.CheckGame();

        public ModOperationResult Enable(GameInstall game, string fullPath, ModWriteExpectation? expect = null) =>
            inner.Enable(game, fullPath, expect);

        public ModOperationResult Disable(GameInstall game, string fullPath, ModWriteExpectation? expect = null) =>
            inner.Disable(game, fullPath, expect);

        public ModOperationResult Install(GameInstall game, string artifactPath, ModInstallOptions? options = null) =>
            inner.Install(game, artifactPath, options);

        public ModOperationResult Delete(GameInstall game, string fullPath, string confirmFileName, ModWriteExpectation? expect = null) =>
            throw new IOException("отказ диска в тесте");

        public ModIcon ReadIcon(string mtmodPath, string? modId = null) => inner.ReadIcon(mtmodPath, modId);

        public IReadOnlyList<string> ReadOperationLog(int maxLines = 200) => inner.ReadOperationLog(maxLines);
    }

    /// <summary>Настоящая служба, у которой чтение иконки ждёт, пока тест откроет ворота.</summary>
    private sealed class GatedIconLibrary(IModLibraryService inner, ManualResetEventSlim gate) : IModLibraryService
    {
        public string OperationLogPath => inner.OperationLogPath;

        public ModLibraryScan Scan(GameInstall game, IReadOnlyList<string>? projectDirs = null) => inner.Scan(game, projectDirs);

        public InstalledMod? Refresh(GameInstall game, string fullPath, IReadOnlyList<string>? projectDirs = null) =>
            inner.Refresh(game, fullPath, projectDirs);

        public ResModsSummary ScanResMods(GameInstall game) => inner.ScanResMods(game);

        public GameRunningState CheckGame() => inner.CheckGame();

        public ModOperationResult Enable(GameInstall game, string fullPath, ModWriteExpectation? expect = null) =>
            inner.Enable(game, fullPath, expect);

        public ModOperationResult Disable(GameInstall game, string fullPath, ModWriteExpectation? expect = null) =>
            inner.Disable(game, fullPath, expect);

        public ModOperationResult Install(GameInstall game, string artifactPath, ModInstallOptions? options = null) =>
            inner.Install(game, artifactPath, options);

        public ModOperationResult Delete(GameInstall game, string fullPath, string confirmFileName, ModWriteExpectation? expect = null) =>
            inner.Delete(game, fullPath, confirmFileName, expect);

        public ModIcon ReadIcon(string mtmodPath, string? modId = null)
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return inner.ReadIcon(mtmodPath, modId);
        }

        public IReadOnlyList<string> ReadOperationLog(int maxLines = 200) => inner.ReadOperationLog(maxLines);
    }

    private sealed class SilentEditor : IEditorLauncher
    {
        /// <summary>Не поднимается никогда: редактор в тестах не запускается.</summary>
        public event EventHandler<string>? LaunchFailed;

        public bool IsAvailable => false;

        /// <summary>Последний каталог, который просили открыть, либо null. Для проверки жеста.</summary>
        public string? LastFolder { get; private set; }

        public void OpenAt(string filePath, int line) => LaunchFailed?.Invoke(this, "редактор в тестах не запускается");

        public void OpenFolder(string folderPath)
        {
            LastFolder = folderPath;
            LaunchFailed?.Invoke(this, "редактор в тестах не запускается");
        }
    }
}
