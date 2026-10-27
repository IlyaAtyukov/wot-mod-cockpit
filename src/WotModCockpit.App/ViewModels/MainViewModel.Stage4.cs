using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Часть главного экрана, относящаяся к этапу 4 — дизайнеру настроек ModsSettingsAPI.
/// <para>
/// Отдельным файлом по той же причине, что и <c>MainViewModel.Stage3.cs</c>: этап
/// подключается целиком. Если модель дизайнера не зарегистрирована,
/// <see cref="SettingsDesigner"/> остаётся <c>null</c>, чип «Настройки мода» гаснет,
/// и главный экран ведёт себя ровно так же, как до этапа 4.
/// </para>
/// <para>
/// Здесь же живёт ЕДИНСТВЕННЫЙ переключатель экранов, который знает про все четыре:
/// <see cref="IsBuildScreen"/>. Экран сборки — это «не браузер API, не рецепты и не
/// дизайнер», и выразить это тремя независимыми галочками нельзя: снятая галочка
/// оставила бы окно пустым, а зажжённая одновременно с чужим экраном — соврала бы.
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    private SettingsDesignerViewModel? _settingsDesigner;

    /// <summary>Модель дизайнера настроек либо <c>null</c>, если он не зарегистрирован.</summary>
    public SettingsDesignerViewModel? SettingsDesigner => _settingsDesigner;

    /// <summary>Дизайнер настроек подключён: от этого зависит чип «Настройки мода».</summary>
    public bool HasSettingsDesigner => _settingsDesigner is not null;

    /// <summary>Показан дизайнер настроек.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuildScreen))]
    private bool _isSettingsScreen;

    /// <summary>
    /// Показан экран сборки и лога — то есть не браузер API, не рецепты и не дизайнер.
    /// Установка в <c>true</c> гасит остальные экраны; установка в <c>false</c> игнорируется:
    /// снятая галочка не должна оставлять окно без единого видимого экрана.
    /// </summary>
    public bool IsBuildScreen
    {
        get => !IsApiScreen && !IsSettingsScreen && (Stage3?.IsBuildScreen ?? true);
        set
        {
            if (!value)
            {
                // Уведомление обязательно: без него привязка чипа осталась бы «снятой»,
                // хотя экран сборки по-прежнему на месте.
                OnPropertyChanged(nameof(IsBuildScreen));
                return;
            }

            IsSettingsScreen = false;
            IsApiScreen = false;

            if (Stage3 is not null)
            {
                Stage3.IsBuildScreen = true;
            }
        }
    }

    /// <summary>
    /// Подключает дизайнер. Зовётся из его же конструктора: он ссылается на эту модель,
    /// и обратной ссылки в конструкторе быть не может (так же устроен этап 3).
    /// </summary>
    /// <param name="designer">Модель дизайнера.</param>
    public void AttachSettingsDesigner(SettingsDesignerViewModel designer)
    {
        _settingsDesigner = designer ?? throw new ArgumentNullException(nameof(designer));

        OnPropertyChanged(nameof(SettingsDesigner));
        OnPropertyChanged(nameof(HasSettingsDesigner));

        // Этап 3 мог быть подключён раньше нас, а мог и позже: порядок задаётся тем,
        // в каком порядке контейнер резолвит аргументы окна. Ловим оба случая.
        HookStage3();
        PropertyChanged += OnOwnPropertyChanged;
    }

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Stage3) || e.PropertyName == nameof(HasStage3))
        {
            HookStage3();
        }
    }

    private bool _stage3Hooked;

    private void HookStage3()
    {
        if (_stage3Hooked || Stage3 is null)
        {
            return;
        }

        _stage3Hooked = true;
        Stage3.PropertyChanged += OnStage3ScreenChanged;
    }

    private void OnStage3ScreenChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Stage3ViewModel.IsRecipesScreen))
        {
            return;
        }

        if (Stage3?.IsRecipesScreen == true)
        {
            IsSettingsScreen = false;
        }

        OnPropertyChanged(nameof(IsBuildScreen));
    }

    partial void OnIsApiScreenChanged(bool value)
    {
        if (value)
        {
            IsSettingsScreen = false;
        }

        OnPropertyChanged(nameof(IsBuildScreen));
    }

    partial void OnIsSettingsScreenChanged(bool value)
    {
        if (!value)
        {
            return;
        }

        // Экраны взаимоисключающие: все четыре живут в одной ячейке разметки.
        IsApiScreen = false;

        if (Stage3 is not null)
        {
            Stage3.IsRecipesScreen = false;
        }
    }

    /// <summary>
    /// Отдаёт текущий проект дизайнеру. Зовётся из главного окна после подъёма состояния
    /// и из этапа 3, когда проект сменился.
    /// </summary>
    public void PushProjectToDesigner() => _settingsDesigner?.SetProject(CurrentProject);
}
