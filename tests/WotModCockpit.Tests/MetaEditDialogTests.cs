using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WotModCockpit.App.ViewModels;
using WotModCockpit.App.Views;

namespace WotModCockpit.Tests;

/// <summary>
/// Диалог «Имя и описание мода»: модель (<see cref="MetaEditViewModel"/>) и само окно
/// (<see cref="MetaEditDialog"/>).
///
/// <para><b>Почему окно проверяется живым.</b> В нём три места, которые ломаются молча и
/// тестами модели не ловятся:</para>
/// <list type="bullet">
///   <item>«Отмена» с привязанной командой — кнопка с <c>IsCancel</c> закрывает окно САМА только
///   без команды; забудь связку с событием модели — и команда выполнится, а окно останется;</item>
///   <item>фокус — WPF его никуда не ставит, и окно, открытое пунктом «Изменить описание…»,
///   открылось бы без курсора;</item>
///   <item>многострочное поле — шаблон TextBox темы жёстко центрировал текст по вертикали, и
///   короткое описание висело посреди высокого поля.</item>
/// </list>
/// </summary>
[Collection("WpfBindingTrace")]
public sealed class MetaEditDialogTests
{
    // ================================================================= модель

    /// <summary>Без изменений сохранять нечего: кнопка погашена.</summary>
    [Fact]
    public void Model_WithoutChanges_CannotSave()
    {
        var vm = Model();

        Assert.False(vm.HasChanges);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    /// <summary>
    /// Пробелы по краям — не изменение: build.py их всё равно срезает, и «Сохранить» только
    /// тронул бы файл впустую.
    /// </summary>
    [Fact]
    public void Model_WhitespaceAroundTheSameText_IsNotAChange()
    {
        var vm = Model();

        vm.Name = "  Demo Mod  ";

        Assert.False(vm.HasChanges);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    /// <summary>Настоящее изменение разрешает сохранение, и сохранение закрывает окно с «да».</summary>
    [Fact]
    public void Model_RealChange_CanSave_AndSaveClosesWithYes()
    {
        var vm = Model();
        bool? closed = null;
        vm.CloseRequested += (_, save) => closed = save;

        vm.Name = "Демо-мод";

        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);
        Assert.True(closed);
        Assert.Equal("Демо-мод", vm.ResultName);
        Assert.True(vm.NameChanged);
        Assert.False(vm.DescriptionChanged);
    }

    /// <summary>
    /// Какое поле изменено, модель знает по каждому отдельно: в файл уйдёт только оно, и
    /// нетронутое поле не перепишется снимком, взятым при открытии окна.
    /// </summary>
    [Fact]
    public void Model_OnlyTheDescriptionChanged_IsReportedSo()
    {
        var vm = Model();

        vm.Description = "Новое описание";

        Assert.False(vm.NameChanged);
        Assert.True(vm.DescriptionChanged);
    }

    /// <summary>Пустое имя: ошибка под полем и погашенная кнопка.</summary>
    [Fact]
    public void Model_EmptyName_ShowsWhy_AndCannotSave()
    {
        var vm = Model();

        vm.Name = "   ";

        Assert.NotEqual(string.Empty, vm.NameError);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    /// <summary>Отмена закрывает окно с «нет».</summary>
    [Fact]
    public void Model_Cancel_ClosesWithNo()
    {
        var vm = Model();
        bool? closed = null;
        vm.CloseRequested += (_, save) => closed = save;

        vm.CancelCommand.Execute(null);

        Assert.False(closed);
    }

    // ================================================================= окно

    /// <summary>Поля привязаны к модели: в них то, что лежит в проекте.</summary>
    [Fact]
    public void Dialog_FieldsShowTheProjectValues()
    {
        (string name, string description) = WpfHost.Invoke(() =>
        {
            MetaEditDialog dialog = Open(Model());
            try
            {
                return (Box(dialog, "NameBox").Text, Box(dialog, "DescriptionBox").Text);
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.Equal("Demo Mod", name);
        Assert.Equal("Описание демо.", description);
    }

    /// <summary>Курсор стоит в том поле, ради которого окно открыли.</summary>
    [Theory]
    [InlineData(MetaEditFocus.Name, "NameBox")]
    [InlineData(MetaEditFocus.Description, "DescriptionBox")]
    public void Dialog_FocusesTheRequestedField(MetaEditFocus focus, string expected)
    {
        string? focused = WpfHost.Invoke(() =>
        {
            MetaEditDialog dialog = Open(Model(focus));
            try
            {
                return (FocusManager.GetFocusedElement(dialog) as FrameworkElement)?.Name;
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.Equal(expected, focused);
    }

    /// <summary>
    /// «Отмена» ЗАКРЫВАЕТ окно. Кнопка с командой сама этого не делает — это работа связки
    /// «событие модели → окно», и без неё окно осталось бы висеть.
    /// </summary>
    [Fact]
    public void Dialog_Cancel_ClosesTheWindow()
    {
        bool stillOpen = WpfHost.Invoke(() =>
        {
            var vm = Model();
            MetaEditDialog dialog = Open(vm);
            vm.CancelCommand.Execute(null);
            Pump();
            bool open = dialog.IsLoaded && dialog.IsVisible;
            if (open)
            {
                dialog.Close();
            }

            return open;
        });

        Assert.False(stillOpen, "после «Отмены» окно осталось открытым");
    }

    /// <summary>
    /// По-настоящему модальное окно, как его открывает экран «Моды»: «Сохранить» — ShowDialog
    /// возвращает «да», «Отмена» — «нет». Только так проверяется ветка
    /// <see cref="Window.DialogResult"/>: у окна, открытого через Show, она не работает, и
    /// тесты выше идут по запасной ветке с Close.
    /// <para>
    /// Нажатие — через кнопку (автоматизация UI), а не через команду модели: так проверяется
    /// и то, что кнопка привязана к нужной команде.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("SaveButton", true)]
    [InlineData("CancelButton", false)]
    public void Dialog_ShownModally_ButtonGivesTheResult(string button, bool expected)
    {
        (bool? result, bool hung, bool nameChanged) = WpfHost.Invoke(() =>
        {
            var vm = Model();
            MetaEditDialog dialog = Offscreen(new MetaEditDialog { DataContext = vm });
            bool timedOut = false;

            // Страховка: если окно не закроется само, тест не должен висеть вечно.
            var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            guard.Tick += (_, _) =>
            {
                guard.Stop();
                timedOut = true;
                dialog.Close();
            };
            guard.Start();

            // Встаёт в очередь ДО ShowDialog и выполняется уже внутри его цикла сообщений, когда
            // окно загружено.
            _ = dialog.Dispatcher.InvokeAsync(
                () =>
                {
                    Box(dialog, "NameBox").Text = "Демо-мод";
                    var peer = new ButtonAutomationPeer((Button)dialog.FindName(button)!);
                    ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
                },
                DispatcherPriority.ApplicationIdle);

            bool? shown = dialog.ShowDialog();
            guard.Stop();
            return (shown, timedOut, vm.NameChanged);
        });

        Assert.False(hung, $"после нажатия {button} модальное окно осталось открытым");
        Assert.Equal(expected, result);
        Assert.True(nameChanged, "текст из поля не дошёл до модели");
    }

    /// <summary>
    /// Текст многострочного поля — у ВЕРХНЕГО края. Раньше шаблон TextBox темы жёстко ставил
    /// содержимое по центру, и <c>VerticalContentAlignment="Top"</c> молча игнорировался.
    /// </summary>
    [Fact]
    public void Dialog_DescriptionText_SitsAtTheTop()
    {
        VerticalAlignment alignment = WpfHost.Invoke(() =>
        {
            MetaEditDialog dialog = Open(Model());
            try
            {
                TextBox box = Box(dialog, "DescriptionBox");
                var host = (FrameworkElement)box.Template.FindName("PART_ContentHost", box);
                return host.VerticalAlignment;
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.Equal(VerticalAlignment.Top, alignment);
    }

    /// <summary>
    /// А однострочные поля остались по центру — починка шаблона не должна была сдвинуть
    /// ни одно из прежних полей приложения.
    /// </summary>
    [Fact]
    public void Dialog_SingleLineField_StaysCentred()
    {
        VerticalAlignment alignment = WpfHost.Invoke(() =>
        {
            MetaEditDialog dialog = Open(Model());
            try
            {
                TextBox box = Box(dialog, "NameBox");
                var host = (FrameworkElement)box.Template.FindName("PART_ContentHost", box);
                return host.VerticalAlignment;
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.Equal(VerticalAlignment.Center, alignment);
    }

    private static MetaEditViewModel Model(MetaEditFocus focus = MetaEditFocus.Name) =>
        new("ru.example.meta_demo", @"C:\repos\wot-mod-meta_demo\src\meta.xml",
            "Demo Mod", "Описание демо.", focus);

    /// <summary>Окно за пределами экрана, как в остальных тестах разметки.</summary>
    private static MetaEditDialog Open(MetaEditViewModel vm)
    {
        MetaEditDialog dialog = Offscreen(new MetaEditDialog { DataContext = vm });
        dialog.Show();
        Pump();
        return dialog;
    }

    private static MetaEditDialog Offscreen(MetaEditDialog dialog)
    {
        dialog.Left = -32000;
        dialog.Top = -32000;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.ShowActivated = false;
        return dialog;
    }

    private static TextBox Box(Window dialog, string name) =>
        (TextBox)dialog.FindName(name)!;

    private static void Pump()
    {
        for (int i = 0; i < 8; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        }
    }
}
