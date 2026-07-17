using DownKyi.Core.Settings.Models;

namespace DownKyi.Core.Settings;

internal sealed record SettingsMigrationResult(bool Migrated, bool IsFutureSchema);

internal static class SettingsSchemaMigrator
{
    public static SettingsMigrationResult Migrate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion > ApplicationSettingsValidator.CurrentSchemaVersion)
        {
            return new SettingsMigrationResult(Migrated: false, IsFutureSchema: true);
        }

        var migrated = false;
        if (settings.SchemaVersion < 0)
        {
            settings.SchemaVersion = 0;
            migrated = true;
        }

        while (settings.SchemaVersion < ApplicationSettingsValidator.CurrentSchemaVersion)
        {
            switch (settings.SchemaVersion)
            {
                case 0:
                    MigrateVersionZeroToOne(settings);
                    migrated = true;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"No settings migration exists for schema {settings.SchemaVersion}.");
            }
        }

        return new SettingsMigrationResult(migrated, IsFutureSchema: false);
    }

    private static void MigrateVersionZeroToOne(AppSettings settings)
    {
        // Version 1 formalizes the existing DTO shape without renaming persisted fields.
        settings.SchemaVersion = 1;
    }
}
