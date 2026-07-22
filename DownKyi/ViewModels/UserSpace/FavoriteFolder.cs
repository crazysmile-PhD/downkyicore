using DownKyi.Images;

namespace DownKyi.ViewModels.UserSpace;

internal sealed record FavoriteFolder(
    long Id,
    string Cover,
    VectorImage TypeImage,
    string Title,
    int Count,
    string UpdatedAt);
