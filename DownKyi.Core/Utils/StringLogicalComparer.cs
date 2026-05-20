namespace DownKyi.Core.Utils;

public class StringLogicalComparer<T> : IComparer<T>
{
    /// <summary>
    /// 比较两个字符串，如果含用数字，则数字按数字的大小来比较。
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    public int Compare(T? x, T? y)
    {
        if (x == null || y == null)
        {
            throw new ArgumentException("Parameters can't be null");
        }

        // StringLogicalComparer<T> is intended for string-compatible values.
        // Keep existing behavior for non-string T inputs.
        string? fileA = x as string;
        string? fileB = y as string;
        char[]? arr1 = fileA?.ToCharArray();
        char[]? arr2 = fileB?.ToCharArray();

        if (arr1 == null || arr2 == null)
        {
            int? arr1Length = arr1?.Length;
            int? arr2Length = arr2?.Length;
            if (arr1Length == arr2Length)
            {
                return 0;
            }

            return arr1Length > arr2Length ? 1 : -1;
        }

        int i = 0;
        int j = 0;
        while (i < arr1.Length && j < arr2.Length)
        {
            if (char.IsDigit(arr1[i]) && char.IsDigit(arr2[j]))
            {
                string s1 = "";
                string s2 = "";
                while (i < arr1.Length && char.IsDigit(arr1[i]))
                {
                    s1 += arr1[i];
                    i++;
                }

                while (j < arr2.Length && char.IsDigit(arr2[j]))
                {
                    s2 += arr2[j];
                    j++;
                }

                if (int.Parse(s1) > int.Parse(s2))
                {
                    return 1;
                }

                if (int.Parse(s1) < int.Parse(s2))
                {
                    return -1;
                }
            }
            else
            {
                if (arr1[i] > arr2[j])
                {
                    return 1;
                }

                if (arr1[i] < arr2[j])
                {
                    return -1;
                }

                i++;
                j++;
            }
        }

        if (arr1.Length == arr2.Length)
        {
            return 0;
        }

        return arr1.Length > arr2.Length ? 1 : -1;
    }
}
