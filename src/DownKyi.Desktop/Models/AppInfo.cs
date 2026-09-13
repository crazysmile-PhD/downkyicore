using System;
using System.Globalization;
using System.Reflection;
using DownKyi.Core.Versioning;

namespace DownKyi.Models;

internal class AppInfo
{
    public string Name { get; } = "哔哩下载姬";
    public int VersionCode { get; }
    public string VersionName { get; }

    public AppInfo()
    {
        var versionName = ResolveVersionName();
        VersionCode = VersionNameToCode(versionName);

#if DEBUG
        VersionName = $"{versionName}-debug";
#else
        VersionName = versionName;
#endif
    }

    public static string NormalizeVersionName(string? versionName)
    {
        return SemanticVersionPolicy.NormalizeForDisplay(versionName);
    }

    public static int VersionNameToCode(string versionName)
    {
        var code = 0;
        var normalizedVersion = NormalizeVersionName(versionName);

        var coreVersion = normalizedVersion.Split('-', 2)[0];
        var parts = coreVersion.Split('.');
        if (parts.Length != 3)
        {
            return 0;
        }

        var i = 2;
        foreach (var item in parts)
        {
            code += int.Parse(item, CultureInfo.InvariantCulture) * (int)Math.Pow(100, i);
            i--;
        }

        return code;
    }

    private static string ResolveVersionName()
    {
        var informationalVersion = typeof(AppInfo)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var normalizedVersion = NormalizeVersionName(informationalVersion);
        if (!string.IsNullOrEmpty(normalizedVersion))
        {
            return normalizedVersion;
        }

        var assemblyVersion = typeof(AppInfo).Assembly.GetName().Version;
        return assemblyVersion == null
            ? "0.0.0"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}";
    }
}
