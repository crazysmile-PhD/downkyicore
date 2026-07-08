using System.Collections.Generic;
using System.Text.Json.Serialization;
using DownKyi.Core.BiliApi.BiliUtils;

namespace DownKyi.Services.Download;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Quality))]
internal sealed partial class DownloadStorageJsonContext : JsonSerializerContext;
