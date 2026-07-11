using System.Collections.ObjectModel;

namespace DownKyi.Core.Utils;

public static class ListHelper
{
    /// <summary>
    /// 判断ObservableCollection中是否存在，不存在则添加
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="list"></param>
    /// <param name="item"></param>
    public static void AddUnique<T>(ObservableCollection<T> list, T item)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (!list.Contains(item))
        {
            list.Add(item);
        }
    }

    /// <summary>
    /// 判断List中是否存在，不存在则添加
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="list"></param>
    /// <param name="item"></param>
    public static void AddUnique<T>(List<T> list, T item)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (!list.Exists(t => EqualityComparer<T>.Default.Equals(t, item)))
        {
            list.Add(item);
        }
    }

    /// <summary>
    /// 判断List中是否存在，不存在则添加
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="list"></param>
    /// <param name="item"></param>
    /// <param name="index"></param>
    /// <param name="currentSelection"></param>
    public static void InsertUnique<T>(Collection<T> list, T item, int index, ref T currentSelection)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (!list.Contains(item))
        {
            list.Insert(index, item);
        }
        else
        {
            var previousSelection = currentSelection;
            list.Remove(item);
            list.Insert(index, item);
            if (previousSelection != null && EqualityComparer<T>.Default.Equals(previousSelection, item))
            {
                currentSelection = previousSelection;
            }
        }
    }

    public static void InsertUnique<T>(Collection<T> list, T item, int index)
    {
        T defaultSelection = default!;
        InsertUnique(list, item, index, ref defaultSelection);
    }
}
