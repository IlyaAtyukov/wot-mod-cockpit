using WotModCockpit.App.ApiBrowser;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Запись доменного каталога (событие, сервис, горячая точка) в списке.
/// <para>
/// Каталог отличается от индекса символов тем, что отвечает не на вопрос «где объявлено X»,
/// а на вопрос «что мне вообще перехватить». Поэтому здесь, помимо места объявления, есть три
/// вещи, которых у обычного символа нет: путь доступа из мода, предупреждение о поведении
/// (исключение из обработчика улетает в клиент) и пометка «этого в вашем клиенте больше нет».
/// </para>
/// </summary>
public sealed class CatalogItemViewModel
{
    /// <summary>
    /// Шесть событий, которые загрузчик мода отдаёт САМ, без подписки. Их приходится
    /// показывать первыми: подсказка над списком про них уже написана, а в общем порядке
    /// (по числу подписок) они лежат на позициях 37, 49, 74, 91, 92 и 94 из 1030,
    /// то есть не видны вообще.
    /// </summary>
    public static readonly string[] LoaderCallbacks =
    [
        "onAccountShowGUI",
        "onAccountBecomePlayer",
        "onAccountBecomeNonPlayer",
        "onAvatarBecomePlayer",
        "onConnected",
        "onDisconnected"
    ];

    /// <summary>Создаёт модель представления поверх записи каталога.</summary>
    /// <param name="entry">Запись из <see cref="IApiBrowserBackend.GetCatalogAsync"/>.</param>
    public CatalogItemViewModel(ApiCatalogEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));

        IsLoaderCallback = entry.Kind == ApiCatalogKind.Events
                           && Array.Exists(LoaderCallbacks, c => string.Equals(c, entry.Name, StringComparison.Ordinal));

        Title = entry.Signature.Length > 0 ? entry.Name + entry.Signature : entry.Name;

        Subtitle = entry.AccessPath.Length > 0
            ? entry.AccessPath
            : entry.Owner.Length > 0
                ? entry.Owner
                : entry.RelativePath;

        // Предлагаемый сниппет выбирается автоматически: событие боевого владельца требует
        // двухступенчатой подписки (init -> onAvatarBecomePlayer -> attach), потому что вне боя
        // ни arena, ни sessionProvider.shared.<слот> не существуют.
        SnippetKind = entry.Kind switch
        {
            ApiCatalogKind.Events => entry.BattleOnly
                ? ApiSnippetKind.LateEventSubscription
                : ApiSnippetKind.EventSubscription,
            ApiCatalogKind.Services => ApiSnippetKind.ServiceAccess,
            _ => ApiSnippetKind.MonkeyPatch
        };

        SnippetTitle = SnippetKind switch
        {
            ApiSnippetKind.EventSubscription => "сниппет: подписка",
            ApiSnippetKind.LateEventSubscription => "сниппет: подписка в бою",
            ApiSnippetKind.ServiceAccess => "сниппет: получить сервис",
            _ => "сниппет: монкипатч"
        };

        Warning = BuildWarning(entry, forRow: false);
        RowWarning = BuildWarning(entry, forRow: true);

        ToolTipText = string.Join(
            Environment.NewLine,
            new[]
            {
                Title,
                entry.Owner.Length == 0 ? null : "владелец: " + entry.Owner,
                entry.AccessPath.Length == 0 ? null : "доступ: " + entry.AccessPath,
                entry.VirtualPath.Length == 0 ? null : $"{entry.VirtualPath}:{entry.Line}",
                IsLoaderCallback ? "это одно из 6 событий, которые загрузчик мода отдаёт сам" : null,
                entry.Detail.Length == 0 ? null : entry.Detail,
                Warning.Length == 0 ? null : Warning
            }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>Исходная запись каталога.</summary>
    public ApiCatalogEntry Entry { get; }

    /// <summary>Имя с сигнатурой — первая строка элемента.</summary>
    public string Title { get; }

    /// <summary>Путь доступа либо владелец — вторая строка элемента.</summary>
    public string Subtitle { get; }

    /// <summary>Короткая пометка справа.</summary>
    public string Badge => Entry.Badge;

    /// <summary>Запись не подтверждена индексом текущего клиента.</summary>
    public bool IsStale => Entry.IsStale;

    /// <summary>Абсолютный путь на диске.</summary>
    public string FullPath => Entry.FullPath;

    /// <summary>Виртуальный путь.</summary>
    public string VirtualPath => Entry.VirtualPath;

    /// <summary>Строка объявления.</summary>
    public int Line => Entry.Line;

    /// <summary>Есть куда перейти в исходнике.</summary>
    public bool CanNavigate => Entry.FullPath.Length > 0 && Entry.Line > 0;

    /// <summary>Какой сниппет предлагать для этой записи.</summary>
    public ApiSnippetKind SnippetKind { get; }

    /// <summary>Подпись кнопки сниппета.</summary>
    public string SnippetTitle { get; }

    /// <summary>Предупреждение, которое надо видеть до того, как код скопирован (карточка выбранной записи).</summary>
    public string Warning { get; }

    /// <summary>
    /// Предупреждение для СТРОКИ СПИСКА — только то, что отличает эту запись от соседних.
    /// <para>
    /// Про «Event, а не SafeEvent» здесь нет намеренно: замерено, что это верно у 1023 записей
    /// из 1030 (99.3 %), то есть на строке этот текст не несёт ни бита информации, но занимает
    /// половину её высоты — в окно помещалось 11 записей из 1030. Это свойство КОРПУСА,
    /// поэтому оно сказано один раз в подсказке над списком, в карточке выбранной записи
    /// и в шапке сниппета.
    /// </para>
    /// </summary>
    public string RowWarning { get; }

    /// <summary>Предупреждение есть.</summary>
    public bool HasWarning => Warning.Length > 0;

    /// <summary>Есть что показать прямо в строке списка.</summary>
    public bool HasRowWarning => RowWarning.Length > 0;

    /// <summary>Это одно из шести событий, которые загрузчик мода отдаёт сам.</summary>
    public bool IsLoaderCallback { get; }

    /// <summary>Подсказка на весь элемент.</summary>
    public string ToolTipText { get; }

    /// <summary>Подходит ли запись под фильтр списка каталога.</summary>
    /// <param name="needle">Подстрока. Пустая — подходит всё.</param>
    /// <returns>true, если показывать.</returns>
    public bool Matches(string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return true;

        const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

        return MatchesName(needle)
               || Entry.Owner.Contains(needle, Cmp)
               || Entry.AccessPath.Contains(needle, Cmp)
               || Entry.RelativePath.Contains(needle, Cmp)
               || Entry.Badge.Contains(needle, Cmp);
    }

    /// <summary>
    /// Совпадение именно по ИМЕНИ записи. Отдельно от <see cref="Matches"/>, потому что
    /// совпадение по имени и совпадение по пути доступа — ответы разного качества, и
    /// показывать их вперемешку нельзя: на запросе «battle» 28 записей из первых 30
    /// совпадали по строке «# BATTLE_CTRL_ID…», то есть сверху лежало то, из чего про
    /// вход в бой не следует ничего.
    /// </summary>
    /// <param name="needle">Подстрока.</param>
    /// <returns>true, если совпало имя.</returns>
    public bool MatchesName(string? needle)
        => !string.IsNullOrWhiteSpace(needle)
           && Entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string BuildWarning(ApiCatalogEntry entry, bool forRow)
    {
        var parts = new List<string>(3);

        if (entry.IsStale)
        {
            parts.Add(entry.Kind == ApiCatalogKind.Hotspots
                ? "в вашем клиенте такого члена нет — патч мёртв"
                : "индексом текущего клиента не подтверждено");
        }

        if (entry.ThrowsToClient && !forRow)
        {
            // common/Event.py:33-36 — Event.__call__ делает LOG_CURRENT_EXCEPTION(); raise.
            // У SafeEvent raise нет. Разница видна только в исходнике, и мододел о ней не знает.
            // В строку списка это НЕ идёт: верно для 99.3 % записей, см. RowWarning.
            parts.Add("это Event, а не SafeEvent: исключение из обработчика улетит в клиент — заворачивайте тело в try/except");
        }

        if (entry.BattleOnly)
        {
            // repositories.py:345-356 — _EmptyRepository.getController возвращает None.
            // 160 записей из 1030 — вот это действительно про КОНКРЕТНУЮ запись, поэтому в строке остаётся.
            parts.Add(forRow
                ? "только в бою"
                : "владелец существует только в бою: вне боя sessionProvider.shared.<слот> равен None");
        }

        return string.Join(Environment.NewLine, parts);
    }
}
