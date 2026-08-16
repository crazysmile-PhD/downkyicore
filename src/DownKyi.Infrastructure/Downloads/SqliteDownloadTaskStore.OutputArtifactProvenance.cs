using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

public sealed partial class SqliteDownloadTaskStore
{
    public async Task<OperationResult> RecordPublishedAsync(
        DownloadOutputArtifactProvenance provenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediateTransaction(connection);
            try
            {
                if (!await TaskExistsAsync(connection, transaction, provenance.TaskId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return ProvenanceTaskNotFound(provenance.TaskId);
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO download_output_artifact_provenance(
                        task_id,
                        artifact_key,
                        artifact_kind,
                        canonical_path,
                        byte_length,
                        sha256,
                        identity_provider,
                        filesystem_identity,
                        published_at_utc)
                    VALUES(
                        @task_id,
                        @artifact_key,
                        @artifact_kind,
                        @canonical_path,
                        @byte_length,
                        @sha256,
                        @identity_provider,
                        @filesystem_identity,
                        @published_at_utc)
                    """;
                BindProvenance(command, provenance);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return OperationResult.Success();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ProvenanceConflict(provenance.TaskId, provenance.ArtifactKey);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (SqliteException)
        {
            return ProvenanceWriteFailed(provenance.TaskId);
        }
        catch (IOException)
        {
            return ProvenanceWriteFailed(provenance.TaskId);
        }
        catch (UnauthorizedAccessException)
        {
            return ProvenanceWriteFailed(provenance.TaskId);
        }
        catch (InvalidOperationException)
        {
            return ProvenanceWriteFailed(provenance.TaskId);
        }
    }

    public async Task<OperationResult<IReadOnlyList<DownloadOutputArtifactProvenance>>> GetPublishedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    task_id,
                    artifact_key,
                    artifact_kind,
                    canonical_path,
                    byte_length,
                    sha256,
                    identity_provider,
                    filesystem_identity,
                    published_at_utc
                FROM download_output_artifact_provenance
                WHERE task_id = @task_id
                ORDER BY artifact_key ASC
                """;
            command.Parameters.AddWithValue("@task_id", taskId.Value);

            var provenance = new List<DownloadOutputArtifactProvenance>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                provenance.Add(ReadProvenance(reader));
            }

            return OperationResult.Success<IReadOnlyList<DownloadOutputArtifactProvenance>>(provenance);
        }
        catch (ArgumentException)
        {
            return ProvenanceCorrupt(taskId);
        }
        catch (InvalidCastException)
        {
            return ProvenanceCorrupt(taskId);
        }
        catch (InvalidOperationException)
        {
            return ProvenanceCorrupt(taskId);
        }
        catch (IOException)
        {
            return ProvenanceCorrupt(taskId);
        }
        catch (NotSupportedException)
        {
            return ProvenanceCorrupt(taskId);
        }
        catch (SqliteException)
        {
            return ProvenanceReadFailed(taskId);
        }
    }

    private static async Task<bool> TaskExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM download_base
            WHERE id = @task_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@task_id", taskId.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static void BindProvenance(
        SqliteCommand command,
        DownloadOutputArtifactProvenance provenance)
    {
        command.Parameters.AddWithValue("@task_id", provenance.TaskId.Value);
        command.Parameters.AddWithValue("@artifact_key", provenance.ArtifactKey);
        command.Parameters.AddWithValue("@artifact_kind", provenance.ArtifactKind);
        command.Parameters.AddWithValue("@canonical_path", provenance.CanonicalPath);
        command.Parameters.AddWithValue("@byte_length", provenance.ByteLength);
        command.Parameters.AddWithValue("@sha256", provenance.Sha256);
        command.Parameters.AddWithValue("@identity_provider", provenance.IdentityProvider);
        command.Parameters.AddWithValue("@filesystem_identity", provenance.FilesystemIdentity);
        command.Parameters.AddWithValue("@published_at_utc", provenance.PublishedAtUtc.ToUnixTimeMilliseconds());
    }

    private static DownloadOutputArtifactProvenance ReadProvenance(SqliteDataReader reader)
    {
        return new DownloadOutputArtifactProvenance(
            new DownloadTaskId(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            new OutputArtifactPublicationEvidence(
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)));
    }

    private static OperationResult ProvenanceTaskNotFound(DownloadTaskId taskId)
    {
        return OperationResult.Failure(new OperationError(
            "download.output_provenance.task_not_found",
            $"Download task '{taskId.Value}' was not found while recording final-output provenance.",
            OperationErrorKind.NotFound));
    }

    private static OperationResult ProvenanceConflict(DownloadTaskId taskId, string artifactKey)
    {
        return OperationResult.Failure(new OperationError(
            "download.output_provenance.conflict",
            $"Final-output provenance already exists for artifact '{artifactKey}' on download task '{taskId.Value}'.",
            OperationErrorKind.Conflict));
    }

    private static OperationResult ProvenanceWriteFailed(DownloadTaskId taskId)
    {
        return OperationResult.Failure(new OperationError(
            "download.output_provenance.write_failed",
            $"Final-output provenance for download task '{taskId.Value}' could not be recorded.",
            OperationErrorKind.Unexpected));
    }

    private static OperationResult<IReadOnlyList<DownloadOutputArtifactProvenance>> ProvenanceCorrupt(
        DownloadTaskId taskId)
    {
        return OperationResult.Failure<IReadOnlyList<DownloadOutputArtifactProvenance>>(
            new OperationError(
                "download.output_provenance.corrupt",
                $"Final-output provenance for download task '{taskId.Value}' is corrupt.",
                OperationErrorKind.Validation));
    }

    private static OperationResult<IReadOnlyList<DownloadOutputArtifactProvenance>> ProvenanceReadFailed(
        DownloadTaskId taskId)
    {
        return OperationResult.Failure<IReadOnlyList<DownloadOutputArtifactProvenance>>(
            new OperationError(
                "download.output_provenance.read_failed",
                $"Final-output provenance for download task '{taskId.Value}' could not be read.",
                OperationErrorKind.Unexpected));
    }
}
