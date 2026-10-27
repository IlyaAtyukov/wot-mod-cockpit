namespace WotModCockpit.Core.ApiCatalog;

/// <summary>
/// Сервис клиента — интерфейс из <c>client/skeletons/**</c>, который мод получает через
/// <c>helpers.dependency</c>. В корпусе 215 таких классов в 47 модулях, из них 206 названы <c>I*</c>;
/// 137 типов реально передаются в <c>dependency.instance/descriptor</c> (3881 обращение).
/// <para>
/// Ранг полезности — это <see cref="UsageCount"/>, и сортировать список надо ИМЕННО по нему:
/// 102 интерфейса из 215 лежат в одном <c>skeletons.gui.game_control</c> и иначе похоронят
/// под собой нужные шесть.
/// </para>
/// </summary>
public sealed record GameService
{
    /// <summary>Имя интерфейса, например <c>IBattleSessionProvider</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Импортируемый модуль, например <c>skeletons.gui.battle_session</c>.</summary>
    public required string ModuleName { get; init; }

    /// <summary>Путь файла относительно корня дерева.</summary>
    public required string RelPath { get; init; }

    /// <summary>Виртуальный путь из шапки uncompyle6 — мост к traceback этапа 1.</summary>
    public string? VirtualPath { get; init; }

    /// <summary>Номер строки объявления класса.</summary>
    public int Line { get; init; }

    /// <summary>Сырой текст списка баз.</summary>
    public string Bases { get; init; } = string.Empty;

    /// <summary>Публичные методы интерфейса (отступ 4, имя без ведущего подчёркивания).</summary>
    public IReadOnlyList<string> Methods { get; init; } = Array.Empty<string>();

    /// <summary>Сколько раз тип передан в <c>dependency.instance</c> или <c>dependency.descriptor</c>.</summary>
    public int UsageCount { get; init; }

    /// <summary>Сколько из них — <c>dependency.instance</c> (немедленное получение).</summary>
    public int InstanceCount { get; init; }

    /// <summary>Сколько из них — <c>dependency.descriptor</c> (ленивое объявление на уровне класса).</summary>
    public int DescriptorCount { get; init; }

    /// <summary>Конкретные реализации: <c>class X(..., IFoo, ...)</c> вне <c>skeletons/</c>. Не более 5 штук.</summary>
    public IReadOnlyList<ServiceImplementation> Implementations { get; init; } = Array.Empty<ServiceImplementation>();

    /// <summary>Названо по соглашению об интерфейсах.</summary>
    public bool IsInterface => Name.Length > 1 && Name[0] == 'I' && char.IsUpper(Name[1]);
}

/// <summary>Конкретная реализация интерфейса сервиса.</summary>
/// <param name="ClassName">Имя класса-реализации.</param>
/// <param name="RelPath">Файл относительно корня дерева.</param>
/// <param name="Line">Строка объявления.</param>
public sealed record ServiceImplementation(string ClassName, string RelPath, int Line);

/// <summary>
/// Слот боевой сессии: <c>sessionProvider.shared.&lt;имя&gt;</c> или <c>sessionProvider.dynamic.&lt;имя&gt;</c>.
/// В <c>skeletons/gui/battle_session.py</c> объявлено 27 shared-слотов и 28 dynamic-слотов.
/// <para>
/// ВАЖНО: вне боя ЛЮБОЙ слот равен <c>None</c> — <c>repositories.py:345-356</c>,
/// <c>_EmptyRepository.getController</c> возвращает <c>None</c>, при том что сам
/// <c>IBattleSessionProvider</c> резолвится успешно. Проверять надо именно слот.
/// </para>
/// </summary>
public sealed record BattleControllerSlot
{
    /// <summary>Имя слота, например <c>feedback</c>.</summary>
    public required string SlotName { get; init; }

    /// <summary><c>shared</c> либо <c>dynamic</c>.</summary>
    public required string LocatorKind { get; init; }

    /// <summary>Значение <c>BATTLE_CTRL_ID</c>, например <c>FEEDBACK</c>. Пусто, если связка не найдена.</summary>
    public string ControllerId { get; init; } = string.Empty;

    /// <summary>Класс контроллера, объявляющий <c>getControllerID</c> с этим идентификатором.</summary>
    public string? ControllerClass { get; init; }

    /// <summary>Файл класса контроллера относительно корня дерева.</summary>
    public string? ControllerRelPath { get; init; }

    /// <summary>Сколько событий объявляет этот контроллер.</summary>
    public int EventCount { get; init; }

    /// <summary>Выражение доступа для сниппета.</summary>
    public string AccessExpression => $"sessionProvider.{LocatorKind}.{SlotName}";
}
