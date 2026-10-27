using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WotModCockpit.Core.Project;

namespace WotModCockpit.App.ViewModels;

/// <summary>С какого поля начинать правку: пункт меню «Переименовать…» или «Изменить описание…».</summary>
public enum MetaEditFocus
{
    /// <summary>Курсор в поле имени.</summary>
    Name,

    /// <summary>Курсор в поле описания.</summary>
    Description,
}

/// <summary>
/// Диалог «Имя и описание мода»: правка <c>&lt;name&gt;</c> и <c>&lt;description&gt;</c> в
/// <c>src\meta.xml</c> проекта.
/// <para>
/// Модель ничего не пишет на диск сама — она только собирает и проверяет ввод. Записью
/// занимается <see cref="ModsViewModel"/> через <see cref="MetaXmlEditor"/>, после того как
/// диалог закрыт кнопкой «Сохранить». Так модель проверяется тестами без окна и без файлов.
/// </para>
/// <para>
/// Правила ввода — те же, что у <see cref="MetaXmlEditor"/>: одно место, где они описаны, и
/// диалог не может пропустить то, что редактор потом отобьёт.
/// </para>
/// </summary>
public sealed partial class MetaEditViewModel : ObservableObject
{
    private readonly string _originalName;
    private readonly string _originalDescription;

    /// <summary>Создаёт модель диалога.</summary>
    /// <param name="modId"><c>&lt;id&gt;</c> мода — показывается, но не правится.</param>
    /// <param name="metaPath">Путь к <c>src\meta.xml</c>, который будет изменён.</param>
    /// <param name="name">Текущее имя из проекта.</param>
    /// <param name="description">Текущее описание из проекта.</param>
    /// <param name="focus">С какого поля начать.</param>
    public MetaEditViewModel(string modId, string metaPath, string name, string description, MetaEditFocus focus)
    {
        ModId = modId ?? string.Empty;
        MetaPath = metaPath ?? string.Empty;
        _originalName = (name ?? string.Empty).Trim();
        _originalDescription = (description ?? string.Empty).Trim();
        _name = _originalName;
        _description = _originalDescription;
        Focus = focus;
    }

    /// <summary>Закрыть окно: <c>true</c> — сохранить, <c>false</c> — отмена.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary><c>&lt;id&gt;</c> мода.</summary>
    public string ModId { get; }

    /// <summary>Файл, который будет изменён.</summary>
    public string MetaPath { get; }

    /// <summary>С какого поля начать.</summary>
    public MetaEditFocus Focus { get; }

    /// <summary>Курсор сначала в поле описания.</summary>
    public bool FocusDescription => Focus == MetaEditFocus.Description;

    /// <summary>Потолок длины имени — для <c>MaxLength</c> поля.</summary>
    public int MaxNameLength => MetaXmlEditor.MaxNameLength;

    /// <summary>Потолок длины описания — для <c>MaxLength</c> поля.</summary>
    public int MaxDescriptionLength => MetaXmlEditor.MaxDescriptionLength;

    /// <summary>Имя в поле ввода.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameError))]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name;

    /// <summary>Описание в поле ввода.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DescriptionError))]
    [NotifyPropertyChangedFor(nameof(DescriptionCounter))]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _description;

    /// <summary>Что не так с именем; пустая строка — всё в порядке.</summary>
    public string NameError => MetaXmlEditor.ValidateName(ResultName) ?? string.Empty;

    /// <summary>Что не так с описанием; пустая строка — всё в порядке.</summary>
    public string DescriptionError => MetaXmlEditor.ValidateDescription(ResultDescription) ?? string.Empty;

    /// <summary>Сколько символов описания занято из доступных.</summary>
    public string DescriptionCounter => $"{ResultDescription.Length} / {MetaXmlEditor.MaxDescriptionLength}";

    /// <summary>
    /// Что-то действительно поменялось. Сравнение — по обрезанным значениям: пробел в конце
    /// поля ничего не меняет в файле (build.py их всё равно срезает), и «Сохранить» без
    /// изменений только тронул бы файл впустую.
    /// </summary>
    public bool HasChanges => ResultName != _originalName || ResultDescription != _originalDescription;

    /// <summary>Сохранять есть что и есть чем.</summary>
    public bool CanSave => HasChanges && NameError.Length == 0 && DescriptionError.Length == 0;

    /// <summary>Имя, которое уйдёт в файл: без пробелов по краям.</summary>
    public string ResultName => (Name ?? string.Empty).Trim();

    /// <summary>Описание, которое уйдёт в файл: без пробелов по краям.</summary>
    public string ResultDescription => (Description ?? string.Empty).Trim();

    /// <summary>
    /// Имя изменилось. В файл уходит только изменённое поле: нетронутое переписывать снимком,
    /// взятым при открытии окна, нельзя — это откатило бы правку, сделанную за это время в
    /// редакторе.
    /// </summary>
    public bool NameChanged => ResultName != _originalName;

    /// <summary>Описание изменилось. См. <see cref="NameChanged"/>.</summary>
    public bool DescriptionChanged => ResultDescription != _originalDescription;

    /// <summary>Прежнее имя — для строки состояния после записи.</summary>
    public string OriginalName => _originalName;

    /// <summary>Закрыть с сохранением.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => CloseRequested?.Invoke(this, true);

    /// <summary>Закрыть без сохранения.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
