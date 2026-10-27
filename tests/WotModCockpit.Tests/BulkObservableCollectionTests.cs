using System.Collections.Specialized;
using WotModCockpit.App.Collections;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="BulkObservableCollection{T}"/> — коллекции, на которой держится лента лога.
/// Проверяется поведение на границе <see cref="BulkObservableCollection{T}.ResetThreshold"/>:
/// маленькая пачка идёт поштучными уведомлениями (не сбрасывая выделение и прокрутку),
/// большая — одним Reset (иначе WPF роняет CollectionView на диапазонном Add).
/// </summary>
public class BulkObservableCollectionTests
{
    private const int Threshold = BulkObservableCollection<int>.ResetThreshold;   // 64

    /// <summary>Пачка ровно по порогу отдаётся поштучно и в правильном порядке.</summary>
    [Fact]
    public void AddRange_AtThreshold_NotifiesPerItem()
    {
        var list = new BulkObservableCollection<int>();
        var actions = Watch(list);

        list.AddRange(Enumerable.Range(1, Threshold));

        Assert.Equal(Threshold, list.Count);
        Assert.Equal(Threshold, actions.Count);
        Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Add, a));
        Assert.Equal(1, list[0]);
        Assert.Equal(Threshold, list[^1]);
    }

    /// <summary>Пачка на единицу больше порога отдаётся ОДНИМ Reset.</summary>
    [Fact]
    public void AddRange_AboveThreshold_NotifiesOnceWithReset()
    {
        var list = new BulkObservableCollection<int>();
        var actions = Watch(list);

        list.AddRange(Enumerable.Range(1, Threshold + 1));

        Assert.Equal(Threshold + 1, list.Count);
        Assert.Single(actions);
        Assert.Equal(NotifyCollectionChangedAction.Reset, actions[0]);
        Assert.Equal(Threshold + 1, list[^1]);
    }

    /// <summary>Пустая пачка — не операция: ни одного уведомления.</summary>
    [Fact]
    public void AddRange_Empty_IsNoOp()
    {
        var list = new BulkObservableCollection<int>();
        var actions = Watch(list);

        list.AddRange(Array.Empty<int>());

        Assert.Empty(list);
        Assert.Empty(actions);
    }

    /// <summary>Вытеснение с головы: снимается ровно столько, сколько просили, и именно с начала.</summary>
    [Fact]
    public void RemoveFirst_AtThreshold_RemovesFromHeadPerItem()
    {
        var list = new BulkObservableCollection<int>();
        list.AddRange(Enumerable.Range(1, 200));

        var actions = Watch(list);
        list.RemoveFirst(Threshold);

        Assert.Equal(200 - Threshold, list.Count);
        Assert.Equal(Threshold + 1, list[0]);
        Assert.Equal(Threshold, actions.Count);
        Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Remove, a));
    }

    /// <summary>Вытеснение больше порога — один Reset.</summary>
    [Fact]
    public void RemoveFirst_AboveThreshold_NotifiesOnceWithReset()
    {
        var list = new BulkObservableCollection<int>();
        list.AddRange(Enumerable.Range(1, 200));

        var actions = Watch(list);
        list.RemoveFirst(Threshold + 1);

        Assert.Equal(200 - Threshold - 1, list.Count);
        Assert.Equal(Threshold + 2, list[0]);
        Assert.Single(actions);
        Assert.Equal(NotifyCollectionChangedAction.Reset, actions[0]);
    }

    /// <summary>Запрос больше размера коллекции обрезается, отрицательный игнорируется.</summary>
    [Fact]
    public void RemoveFirst_ClampsCount()
    {
        var list = new BulkObservableCollection<int>();
        list.AddRange(Enumerable.Range(1, 10));

        list.RemoveFirst(0);
        list.RemoveFirst(-5);
        Assert.Equal(10, list.Count);

        list.RemoveFirst(1000);
        Assert.Empty(list);

        list.RemoveFirst(1);           // на пустой коллекции — не операция и не исключение
        Assert.Empty(list);
    }

    /// <summary>Замена содержимого — всегда один Reset, даже на пустом входе.</summary>
    [Fact]
    public void ReplaceAll_AlwaysOneReset()
    {
        var list = new BulkObservableCollection<int>();
        list.AddRange(Enumerable.Range(1, 10));

        var actions = Watch(list);

        list.ReplaceAll(new[] { 42, 43 });
        Assert.Equal(new[] { 42, 43 }, list);

        list.ReplaceAll(Array.Empty<int>());
        Assert.Empty(list);

        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Reset, a));
    }

    /// <summary>Count меняется вместе с уведомлением — иначе счётчик «показано N» врал бы.</summary>
    [Fact]
    public void CountPropertyChanged_IsRaisedForBulkOperations()
    {
        var list = new BulkObservableCollection<int>();
        int countNotifications = 0;
        ((System.ComponentModel.INotifyPropertyChanged)list).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(list.Count))
                countNotifications++;
        };

        list.AddRange(Enumerable.Range(1, Threshold + 1));
        list.RemoveFirst(Threshold + 1);
        list.ReplaceAll(new[] { 1 });

        Assert.Equal(3, countNotifications);
    }

    private static List<NotifyCollectionChangedAction> Watch<T>(BulkObservableCollection<T> list)
    {
        var actions = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => actions.Add(e.Action);
        return actions;
    }
}
