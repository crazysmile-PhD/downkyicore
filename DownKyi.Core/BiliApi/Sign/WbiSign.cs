using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DownKyi.Core.BiliApi.Sign;

public static class WbiSign
{
    private static readonly int[] MixinKeyEncodingTable =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
        61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
        36, 20, 34, 44, 52
    };

    /// <summary>
    /// 打乱重排实时口令
    /// </summary>
    /// <param name="origin"></param>
    /// <returns></returns>
    private static string GetMixinKey(string origin)
    {
        var temp = new StringBuilder();
        foreach (var i in MixinKeyEncodingTable)
        {
            temp.Append(origin[i]);
        }

        return temp.ToString()[..32];
    }

    /// <summary>
    /// 将字典参数转为字符串
    /// </summary>
    /// <param name="parameters"></param>
    /// <returns></returns>
    public static string ParametersToQuery(Dictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var keys = parameters.Keys.ToList();
        var queryList = (from item in keys let value = parameters[item] select $"{item}={value}").ToList();

        return string.Join("&", queryList);
    }

    /// <summary>
    /// Wbi签名，返回所有参数字典
    /// </summary>
    /// <param name="parameters"></param>
    /// <param name="imgKey"></param>
    /// <param name="subKey"></param>
    /// <returns></returns>
    public static Dictionary<string, string> EncodeWbi(
        Dictionary<string, object?> parameters,
        string imgKey,
        string subKey,
        long unixTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(imgKey);
        ArgumentException.ThrowIfNullOrEmpty(subKey);
        if (!new WbiKeys(imgKey, subKey).IsValid)
        {
            throw new ArgumentException("WBI keys must each contain 32 ASCII letters or digits.");
        }

        var paraStr = new Dictionary<string, string>();
        foreach (var (key, value) in parameters)
        {
            var val = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (val != null)
            {
                paraStr.Add(key, val);
            }
        }

        var mixinKey = GetMixinKey(imgKey + subKey);
        var currTime = unixTimeSeconds.ToString(CultureInfo.InvariantCulture);
        //添加 wts 字段
        paraStr["wts"] = currTime;
        // 按照 key 重排参数
        paraStr = paraStr.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value);
        //过滤 value 中的 "!'()*" 字符
        paraStr = paraStr.ToDictionary(kvp => kvp.Key, kvp => new string(kvp.Value.Where(chr => !"!'()*".Contains(chr, StringComparison.Ordinal)).ToArray()));
        // 序列化参数
        var query = string.Join(
            "&",
            paraStr.Select(pair => $"{EncodeFormComponent(pair.Key)}={EncodeFormComponent(pair.Value)}"));
        //计算 w_rid
        // Bilibili defines w_rid as MD5 here; it is a protocol field, not a password or local trust hash.
#pragma warning disable CA5351
        var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey));
#pragma warning restore CA5351
        var wbiSign = Convert.ToHexStringLower(hashBytes);
        paraStr["w_rid"] = wbiSign;

        return paraStr;
    }

    private static string EncodeFormComponent(string value)
    {
        return Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
    }
}
