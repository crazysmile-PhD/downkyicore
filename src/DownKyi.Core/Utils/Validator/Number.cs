using System.Globalization;
using System.Text.RegularExpressions;

namespace DownKyi.Core.Utils.Validator;

public static class Number
{
    /// <summary>
    /// 字符串转数字（长整型）
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public static long GetInt(string value)
    {
        return Regex.IsMatch(value, @"^\d+$")
            ? long.Parse(value, CultureInfo.InvariantCulture)
            : -1;
    }
}
