using JobWatcher.Domain;
using Microsoft.Data.Sqlite;

namespace JobWatcher.Storage;

/// <summary>
/// <see cref="ISeenStore"/> backed by a single SQLite file. Each operation opens its own short-lived
/// connection, so the file is never held open between calls. The source is stored as its enum number.
/// </summary>
/// <remarks>
/// A seen.db from a version that stored the source as text is not migrated: its text rows never equal a number, so
/// they are simply ignored and each source silently remembers its current vacancies once more (first run).
/// </remarks>
public sealed class SqliteSeenStore(string dbPath) : ISeenStore
{
    private readonly string _dbPath = Path.GetFullPath(dbPath);

    // Pooling is off so that closing a connection really releases the file.
    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString();

    public async Task<bool> HasAnyAsync(VacancySource source, CancellationToken ct)
    {
        var key = KeyOf(source);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM seen WHERE source = $source)";
        command.Parameters.AddWithValue("$source", key);

        return await ExecuteExistsAsync(command, ct);
    }

    public async Task<bool> ContainsAsync(VacancySource source, string sourceId, CancellationToken ct)
    {
        var key = KeyOf(source);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM seen WHERE source = $source AND id = $id)";
        command.Parameters.AddWithValue("$source", key);
        command.Parameters.AddWithValue("$id", sourceId);

        return await ExecuteExistsAsync(command, ct);
    }

    public async Task AddAsync(VacancySource source, IEnumerable<string> sourceIds, CancellationToken ct)
    {
        var key = KeyOf(source);
        var ids = sourceIds.ToList();
        if (ids.Count == 0)
            return;

        await using var connection = await OpenAsync(ct);
        // One transaction for the whole batch: it is committed completely or, on cancellation or error, not at all.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT OR IGNORE INTO seen (source, id, first_seen) VALUES ($source, $id, $firstSeen)";
        command.Parameters.AddWithValue("$source", key);
        command.Parameters.AddWithValue("$firstSeen", DateTime.UtcNow.ToString("O"));
        var idParameter = command.Parameters.Add("$id", SqliteType.Text);

        foreach (var id in ids)
        {
            idParameter.Value = id;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        var connection = new SqliteConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS seen (
                    source     INTEGER NOT NULL,
                    id         TEXT NOT NULL,
                    first_seen TEXT NOT NULL,
                    PRIMARY KEY (source, id)
                )
                """;
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // An undefined value would be stored under a number no source owns, so it is refused before the file is touched.
    private static int KeyOf(VacancySource source) =>
        source.IsKnown()
            ? (int)source
            : throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown vacancy source.");

    private static async Task<bool> ExecuteExistsAsync(SqliteCommand command, CancellationToken ct) =>
        (long)(await command.ExecuteScalarAsync(ct))! == 1;
}
