using WotModCockpit.Core.Models;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Часть главного экрана, относящаяся к этапу 3 (Problems, рецепты, мастер нового проекта).
/// <para>
/// Вынесена отдельным файлом намеренно. Этап 3 подключается целиком: если реализаций Core
/// нет, <see cref="Stage3"/> остаётся <c>null</c>, и главный экран ведёт себя ровно так же,
/// как до этапа 3. В теле <c>MainViewModel.cs</c> от этого остался ровно один вызов —
/// <see cref="Stage3PreflightGateAsync"/> перед запуском build.py.
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Оболочка этапа 3 либо <c>null</c>, если она не зарегистрирована (так модель собирают
    /// тесты этапа 1).
    /// </summary>
    public Stage3ViewModel? Stage3 { get; private set; }

    /// <summary>Этап 3 подключён: в разметке от этого зависят вкладка рецептов и панель Problems.</summary>
    public bool HasStage3 => Stage3 is not null;

    /// <summary>
    /// Текущий загруженный проект либо <c>null</c>. Нужен панели Problems и каталогу рецептов:
    /// им нужен не путь, а разобранный паспорт мода (тег лога, имя пакета, точка входа).
    /// </summary>
    public ModProject? CurrentProject => _project;

    /// <summary>
    /// Подключает оболочку этапа 3. Зовётся из её же конструктора: контейнер создаёт сначала
    /// эту модель, потом оболочку, и обратной ссылки в конструкторе быть не может.
    /// </summary>
    /// <param name="stage3">Оболочка.</param>
    public void AttachStage3(Stage3ViewModel stage3)
    {
        Stage3 = stage3 ?? throw new ArgumentNullException(nameof(stage3));
        OnPropertyChanged(nameof(Stage3));
        OnPropertyChanged(nameof(HasStage3));
    }

    /// <summary>
    /// Открыть проект по пути программно — тем же путём, каким это делает кнопка
    /// «выбрать папку проекта…». Нужен мастеру нового проекта: созданный мод логично
    /// сразу открыть.
    /// </summary>
    /// <param name="dir">Каталог проекта.</param>
    public void OpenProject(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        SetProjectDir(dir, save: true);
    }

    /// <summary>
    /// Проверка перед сборкой. Возвращает <c>true</c>, если сборку можно продолжать.
    /// <para>
    /// Ошибка preflight сборку НЕ блокирует — она только требует подтверждения. Причина
    /// прагматичная: заведомо кривой мод иногда собирают специально, чтобы посмотреть,
    /// как именно он падает в игре, и запрет на это превратил бы полезную проверку в помеху.
    /// </para>
    /// </summary>
    /// <param name="project">Проект, который собираются собирать.</param>
    /// <returns><c>true</c> — продолжать.</returns>
    private async Task<bool> Stage3PreflightGateAsync(ModProject project)
    {
        Stage3ViewModel? stage3 = Stage3;
        if (stage3 is null)
        {
            return true;
        }

        try
        {
            BusyText = "preflight перед сборкой…";
            IsBusy = true;

            return await stage3.ConfirmBuildAsync(project, ConfirmHandler).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Сломавшаяся проверка не имеет права мешать сборке: build.py и без неё
            // прогонит check_py2_syntax.py.
            StatusMessage = "preflight: " + Short(ex);
            return true;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }
}
