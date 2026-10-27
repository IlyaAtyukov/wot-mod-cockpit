using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WotModCockpit.App.Collections;

/// <summary>
/// <see cref="ObservableCollection{T}"/> с пакетными операциями.
/// <para>
/// Зачем: python.log приходит пачками по сотням строк. Добавление по одному элементу
/// с отдельным уведомлением на каждый — это сотни проходов CollectionView и layout
/// в секунду. WPF при этом НЕ умеет диапазонные уведомления
/// (<see cref="NotifyCollectionChangedAction.Add"/> с несколькими элементами роняет
/// <c>CollectionView</c> с <see cref="NotSupportedException"/>), поэтому большие пачки
/// отдаются одним <see cref="NotifyCollectionChangedAction.Reset"/>, а маленькие —
/// поштучно, чтобы не сбрасывать выделение и позицию прокрутки на каждый чих.
/// </para>
/// </summary>
/// <typeparam name="T">Тип элемента.</typeparam>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Порог, начиная с которого пачка отдаётся одним Reset вместо поштучных уведомлений.
    /// Подобран так, чтобы обычный поток лога (десятки строк за такт) шёл поштучно,
    /// а «прогрев» хвостом на 64 КБ и смена фильтра — одним Reset.
    /// </summary>
    public const int ResetThreshold = 64;

    private int _suppress;

    /// <summary>Добавляет элементы в конец. Пустая последовательность — не операция.</summary>
    /// <param name="items">Элементы.</param>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        IList<T> list = items as IList<T> ?? items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        if (list.Count <= ResetThreshold)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Add(list[i]);
            }

            return;
        }

        _suppress++;
        try
        {
            for (int i = 0; i < list.Count; i++)
            {
                Items.Add(list[i]);
            }
        }
        finally
        {
            _suppress--;
        }

        RaiseReset();
    }

    /// <summary>Полностью заменяет содержимое одним уведомлением. Так применяется фильтр лога.</summary>
    /// <param name="items">Новое содержимое.</param>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _suppress++;
        try
        {
            Items.Clear();
            foreach (T item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppress--;
        }

        RaiseReset();
    }

    /// <summary>
    /// Удаляет <paramref name="count"/> элементов с начала — вытеснение старых записей
    /// кольцевым буфером лога. Значения &lt;= 0 игнорируются, превышение размера обрезается.
    /// </summary>
    /// <param name="count">Сколько удалить.</param>
    public void RemoveFirst(int count)
    {
        if (count <= 0 || Count == 0)
        {
            return;
        }

        count = Math.Min(count, Count);

        if (count <= ResetThreshold)
        {
            for (int i = 0; i < count; i++)
            {
                RemoveAt(0);
            }

            return;
        }

        _suppress++;
        try
        {
            for (int i = 0; i < count; i++)
            {
                Items.RemoveAt(0);
            }
        }
        finally
        {
            _suppress--;
        }

        RaiseReset();
    }

    /// <inheritdoc />
    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppress == 0)
        {
            base.OnCollectionChanged(e);
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppress == 0)
        {
            base.OnPropertyChanged(e);
        }
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
