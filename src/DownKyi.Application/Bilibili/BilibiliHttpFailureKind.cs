namespace DownKyi.Application.Bilibili;

public enum BilibiliHttpFailureKind
{
    Authentication,
    RateLimited,
    HttpStatus,
    EmptyResponse,
    Transport,
    Timeout
}
