using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.Stage4;
using WotModCockpit.App.ViewModels;
using WotModCockpit.App.Views;
using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// ЭТАП 4, регрессии, пойманные адверсариальной проверкой. Каждый тест здесь закрывает
/// дефект, который был в сборке и правился по месту, — и падает, если дефект вернётся.
/// </summary>
[Collection("WpfBindingTrace")]
public sealed class Stage4RegressionTests : IDisposable
{
    private readonly string _temp;

    /// <summary>Заводит временное дерево под пробы.</summary>
    public Stage4RegressionTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "cockpit-stage4reg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
    }

    /// <summary>Убирает временное дерево.</summary>
    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ============================================================ повреждённый modsettings.dat

    /// <summary>
    /// В <c>modsettings.dat</c> пишут тринадцать чужих модов и сам API, и на месте строки
    /// <c>modDisplayName</c> в испорченном состоянии может оказаться что угодно.
    /// <c>Read</c> обязан ДОКЛАДЫВАТЬ о беде, а не бросать: раньше
    /// <c>GetValue&lt;string&gt;()</c> ронял чтение ВСЕГО живого состояния из-за одного
    /// испорченного мода.
    /// </summary>
    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{\"ru\": \"имя\"}")]
    [InlineData("[1, 2]")]
    [InlineData("\"\"")]
    public void LiveReader_DoesNotThrow_WhenDisplayNameIsNotString(string json)
    {
        string path = Path.Combine(_temp, "modsettings.dat");
        File.WriteAllText(
            path,
            "{\"templates\": {\"ru.local.broken\": {\"modDisplayName\": " + json
            + ", \"column1\": [], \"column2\": []}}, \"settings\": {}, \"storage\": {}}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var reader = new LiveModSettingsReader();

        bool ok = reader.Read(path);

        Assert.True(ok, reader.Error);
        Assert.Equal(string.Empty, reader.Error);

        LiveModTemplate template = Assert.Single(reader.Templates);

        // Не строка — берём ключ мода: он есть всегда, и список остаётся читаемым.
        Assert.Equal("ru.local.broken", template.Linkage);
        Assert.Equal("ru.local.broken", template.DisplayName);
        Assert.Contains("ru.local.broken", template.Display, StringComparison.Ordinal);
    }

    /// <summary>Нормальную строку заголовка починка не испортила.</summary>
    [Fact]
    public void LiveReader_KeepsDisplayName_WhenItIsAString()
    {
        string path = Path.Combine(_temp, "modsettings.dat");
        File.WriteAllText(
            path,
            "{\"templates\": {\"ru.local.ok\": {\"modDisplayName\": \"Хороший мод\", \"column1\": [], \"column2\": []}},"
            + " \"settings\": {}, \"storage\": {}}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var reader = new LiveModSettingsReader();

        Assert.True(reader.Read(path), reader.Error);
        Assert.Equal("Хороший мод", Assert.Single(reader.Templates).DisplayName);
    }

    // ============================================================ причина отказа импорта

    /// <summary>
    /// Битый <c>settings.design.json</c> рядом с отсутствующей схемой раньше давал заведомо
    /// ложное «в проекте нет settings.design.json»: <c>SettingsImportResult.Failed</c> заметки
    /// не несёт, и настоящая причина терялась. Человек шёл искать не ту беду.
    /// </summary>
    [Fact]
    public void Import_NamesTheRealReason_WhenDesignFileIsBroken()
    {
        File.WriteAllText(
            Path.Combine(_temp, SettingsDesignerService.DesignFileName),
            "{{{ это не json",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var backend = new CoreSettingsDesignerBackend(new SettingsDesignerService());

        SettingsImportResult result = backend.ImportFromProject(_temp);

        Assert.Null(result.Form);
        Assert.NotNull(result.Error);
        Assert.Contains("не разбирается как JSON", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("в проекте нет", result.Error, StringComparison.Ordinal);
    }

    /// <summary>А когда документа и правда нет — так и сказано.</summary>
    [Fact]
    public void Import_SaysFileIsMissing_WhenNothingToRead()
    {
        var backend = new CoreSettingsDesignerBackend(new SettingsDesignerService());

        SettingsImportResult result = backend.ImportFromProject(_temp);

        Assert.Null(result.Form);
        Assert.NotNull(result.Error);
        Assert.Contains("в проекте нет", result.Error, StringComparison.Ordinal);
        Assert.Contains("settings_schema.py", result.Error, StringComparison.Ordinal);
    }

    // ============================================================ панель свойств без выбора

    /// <summary>
    /// Пока контрол не выбран, <c>Selected.HasError</c> / <c>Selected.HasWarning</c> /
    /// <c>Selected.IsUnproven</c> не разрешаются, и WPF берёт умолчание свойства
    /// <see cref="Visibility"/> — то есть <c>Visible</c>. На ПЕРВОМ же экране это давало две
    /// пустые цветные рамки и строку «в живых шаблонах такого контрола нет» про контрол,
    /// которого нет вовсе. Лечится <c>FallbackValue</c>/<c>TargetNullValue</c> у трёх привязок.
    /// </summary>
    [Fact]
    public void PropertiesPane_ShowsNoIssueBoxes_WhenNothingSelected()
    {
        (int boxes, bool unproven) = WpfHost.Invoke(() =>
        {
            using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

            var vm = provider.GetRequiredService<SettingsDesignerViewModel>();
            vm.ConfirmHandler = static (_, _) => true;
            vm.Selected = null;

            var view = new SettingsDesignerView { DataContext = vm };
            using var host = OffscreenHost.Show(view, 1440, 820);
            host.Pump();

            return (EmptyIssueBoxes(view), UnprovenLineVisible(view));
        });

        Assert.Equal(0, boxes);
        Assert.False(unproven, "строка про отсутствие живых образцов видна, хотя контрол не выбран");
    }

    /// <summary>
    /// Обратная сторона той же починки: у ВЫБРАННОГО контрола рамка ошибки и строка
    /// «нет живых образцов» обязаны показываться по-прежнему. Иначе лечение хуже болезни.
    /// </summary>
    [Fact]
    public void PropertiesPane_StillShowsIssues_WhenControlSelected()
    {
        (bool errorShown, bool unprovenShown) = WpfHost.Invoke(() =>
        {
            using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

            var vm = provider.GetRequiredService<SettingsDesignerViewModel>();
            vm.ConfirmHandler = static (_, _) => true;
            vm.Linkage = "ru.local.test";
            vm.ModDisplayName = "Тест";

            var view = new SettingsDesignerView { DataContext = vm };
            using var host = OffscreenHost.Show(view, 1440, 820);

            // Ползунок с минимумом больше максимума: гарантированная ошибка контрола.
            SettingControlViewModel slider = vm.InsertNew(SettingsControlType.Slider, 1, -1);
            slider.Text = "Плохой";
            slider.VarName = "bad";
            slider.MinimumText = "10";
            slider.MaximumText = "1";
            vm.Revalidate();
            vm.Selected = slider;
            host.Pump();

            bool error = slider.HasError && TextVisible(view, slider.ErrorText);

            // ColorChoice — тип без живых образцов: строка предупреждения обязана появиться.
            SettingControlViewModel color = vm.InsertNew(SettingsControlType.ColorChoice, 1, -1);
            color.VarName = "tint";
            vm.Revalidate();
            vm.Selected = color;
            host.Pump();

            return (error, UnprovenLineVisible(view));
        });

        Assert.True(errorShown, "ошибка выбранного контрола перестала показываться в панели свойств");
        Assert.True(unprovenShown, "предупреждение о непроверенном типе перестало показываться");
    }

    // ============================================================ служебное

    /// <summary>Рамки замечаний, нарисованные ПУСТЫМИ: ровно то, что чинилось.</summary>
    private static int EmptyIssueBoxes(DependencyObject root)
        => Descendants<Border>(root).Count(b =>
            b.IsVisible
            && b.CornerRadius.TopLeft == 3
            && b.Child is TextBlock text
            && text.FontSize == 10
            && text.Text.Length == 0);

    private static bool UnprovenLineVisible(DependencyObject root)
        => TextVisible(root, "в живых шаблонах такого контрола нет — тип восстановлен по коду API");

    private static bool TextVisible(DependencyObject root, string text)
        => text.Length > 0
           && Descendants<TextBlock>(root).Any(t =>
               t.IsVisible && string.Equals(t.Text, text, StringComparison.Ordinal));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Окно за краем экрана: разметка обязана быть НАСТОЯЩЕ отрисована.</summary>
    private sealed class OffscreenHost : IDisposable
    {
        private readonly Window _window;

        private OffscreenHost(Window window) => _window = window;

        public static OffscreenHost Show(FrameworkElement content, double width, double height)
        {
            var window = new Window
            {
                Content = content,
                Width = width,
                Height = height,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false
            };

            window.Show();
            window.UpdateLayout();
            return new OffscreenHost(window);
        }

        public void Pump()
        {
            for (int i = 0; i < 8; i++)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
            }

            _window.UpdateLayout();
        }

        public void Dispose() => _window.Close();
    }
}
