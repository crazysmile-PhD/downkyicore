namespace DownKyi.Application.Settings;

public enum SettingsCommitFailure
{
    None,
    PersistenceDisabled,
    WriteFailed
}

public sealed record SettingsCommitResult(
    ApplicationSettings Settings,
    SettingsCommitFailure Failure)
{
    public bool Succeeded => Failure == SettingsCommitFailure.None;
}
