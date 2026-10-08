using System.Diagnostics;
using System.Globalization;
using System.Text;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Logging;

public static class SafeExceptionDiagnosticFormatter
{
    private const int MaximumExceptionDepth = 3;
    private const int MaximumFramesPerException = 12;

    public static string Format(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var builder = new StringBuilder();
        var current = exception;
        for (var depth = 0; current != null && depth < MaximumExceptionDepth;
             current = current.InnerException, depth++)
        {
            builder.Append("Exception type: ").AppendLine(current.GetType().FullName);
            builder.Append("HResult: 0x")
                .AppendLine(current.HResult.ToString("X8", CultureInfo.InvariantCulture));
            if (current is SqliteException sqliteException)
            {
                builder.Append("SQLite error code: ")
                    .AppendLine(sqliteException.SqliteErrorCode.ToString(CultureInfo.InvariantCulture));
                builder.Append("SQLite extended error code: ")
                    .AppendLine(sqliteException.SqliteExtendedErrorCode.ToString(CultureInfo.InvariantCulture));
            }

            if (current is DownloadStoreSchemaMismatchException mismatch
                && mismatch.SchemaDifferences.Count > 0)
            {
                builder.Append("Error message: ").AppendLine(mismatch.Message);
                builder.Append("Database user_version: ")
                    .AppendLine(mismatch.UserVersion.ToString(CultureInfo.InvariantCulture));
                foreach (var difference in mismatch.SchemaDifferences)
                {
                    builder.Append("Schema difference: ").AppendLine(difference);
                }
            }

            var frameCount = 0;
            foreach (var frame in new StackTrace(current, fNeedFileInfo: false)
                         .GetFrames() ?? [])
            {
                if (frame.GetMethod() is not { } method)
                {
                    continue;
                }

                var declaringType = method.DeclaringType?.FullName;
                if (declaringType is null
                    || !(declaringType == "DownKyi"
                         || declaringType.StartsWith("DownKyi.", StringComparison.Ordinal)))
                {
                    continue;
                }

                builder.Append("  at ")
                    .Append(declaringType)
                    .Append('.')
                    .AppendLine(method.Name);
                if (++frameCount == MaximumFramesPerException)
                {
                    break;
                }
            }
        }

        return builder.ToString().TrimEnd();
    }
}
