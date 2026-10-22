using JobWatcher.Domain;
using JobWatcher.Storage;
using Microsoft.Data.Sqlite;

namespace JobWatcher.Tests.Storage;

public sealed class SqliteSeenStoreTests : IDisposable
{
    // xUnit builds a new instance per test, so every test gets its own directory and database file.
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jobwatcher-tests-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_directory, "seen.db");

    private SqliteSeenStore CreateStore() => new(DbPath);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task HasAny_false_for_empty_store()
    {
        var store = CreateStore();

        Assert.False(await store.HasAnyAsync(VacancySource.Dou, CancellationToken.None));
    }

    [Fact]
    public async Task Add_then_Contains_true()
    {
        var store = CreateStore();

        await store.AddAsync(VacancySource.Dou, ["1", "2"], CancellationToken.None);

        Assert.True(await store.ContainsAsync(VacancySource.Dou, "1", CancellationToken.None));
        Assert.True(await store.ContainsAsync(VacancySource.Dou, "2", CancellationToken.None));
        Assert.True(await store.HasAnyAsync(VacancySource.Dou, CancellationToken.None));
    }

    [Fact]
    public async Task Contains_false_for_unknown_id()
    {
        var store = CreateStore();
        await store.AddAsync(VacancySource.Dou, ["1"], CancellationToken.None);

        Assert.False(await store.ContainsAsync(VacancySource.Dou, "2", CancellationToken.None));
    }

    [Fact]
    public async Task Same_id_in_different_sources_is_distinct()
    {
        var store = CreateStore();
        await store.AddAsync(VacancySource.Dou, ["42"], CancellationToken.None);

        Assert.True(await store.ContainsAsync(VacancySource.Dou, "42", CancellationToken.None));
        Assert.False(await store.ContainsAsync(VacancySource.Djinni, "42", CancellationToken.None));
    }

    [Fact]
    public async Task HasAny_is_per_source()
    {
        var store = CreateStore();
        await store.AddAsync(VacancySource.Dou, ["1"], CancellationToken.None);

        Assert.True(await store.HasAnyAsync(VacancySource.Dou, CancellationToken.None));
        Assert.False(await store.HasAnyAsync(VacancySource.Djinni, CancellationToken.None));
    }

    [Fact]
    public async Task Add_twice_does_not_throw()
    {
        var store = CreateStore();
        await store.AddAsync(VacancySource.Dou, ["1", "2"], CancellationToken.None);

        await store.AddAsync(VacancySource.Dou, ["2", "3", "3"], CancellationToken.None);

        Assert.True(await store.ContainsAsync(VacancySource.Dou, "1", CancellationToken.None));
        Assert.True(await store.ContainsAsync(VacancySource.Dou, "2", CancellationToken.None));
        Assert.True(await store.ContainsAsync(VacancySource.Dou, "3", CancellationToken.None));
    }

    [Fact]
    public async Task Data_survives_new_instance_on_same_file()
    {
        await CreateStore().AddAsync(VacancySource.Dou, ["1"], CancellationToken.None);

        var reopened = CreateStore();

        Assert.True(await reopened.HasAnyAsync(VacancySource.Dou, CancellationToken.None));
        Assert.True(await reopened.ContainsAsync(VacancySource.Dou, "1", CancellationToken.None));
    }

    [Fact]
    public async Task Creates_missing_directory()
    {
        var nestedPath = Path.Combine(_directory, "a", "b", "seen.db");
        var store = new SqliteSeenStore(nestedPath);

        await store.AddAsync(VacancySource.Dou, ["1"], CancellationToken.None);

        Assert.True(File.Exists(nestedPath));
        Assert.True(await store.ContainsAsync(VacancySource.Dou, "1", CancellationToken.None));
    }

    [Fact]
    public async Task Add_empty_sequence_adds_nothing()
    {
        var store = CreateStore();

        await store.AddAsync(VacancySource.Dou, [], CancellationToken.None);

        Assert.False(await store.HasAnyAsync(VacancySource.Dou, CancellationToken.None));
    }

    [Fact]
    public async Task Add_cancelled_throws_and_stores_nothing()
    {
        var store = CreateStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.AddAsync(VacancySource.Dou, ["1", "2"], cts.Token));

        Assert.False(await store.HasAnyAsync(VacancySource.Dou, CancellationToken.None));
    }

    [Fact]
    public async Task Source_is_stored_as_its_integer_number()
    {
        await CreateStore().AddAsync(VacancySource.Dou, ["1"], CancellationToken.None);

        var (value, type) = await ReadSourceColumnAsync("1");

        Assert.Equal(1L, value);
        Assert.Equal("integer", type);
    }

    [Fact]
    public async Task Different_sources_with_the_same_id_stay_distinct()
    {
        var store = CreateStore();

        await store.AddAsync(VacancySource.Dou, ["42"], CancellationToken.None);
        await store.AddAsync(VacancySource.Jooble, ["42"], CancellationToken.None);

        Assert.True(await store.ContainsAsync(VacancySource.Dou, "42", CancellationToken.None));
        Assert.True(await store.ContainsAsync(VacancySource.Jooble, "42", CancellationToken.None));
        Assert.False(await store.ContainsAsync(VacancySource.Djinni, "42", CancellationToken.None));
        Assert.Equal(2L, await CountRowsAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task Undefined_source_is_rejected_and_nothing_is_written(int value)
    {
        var store = CreateStore();
        var source = (VacancySource)value;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AddAsync(source, ["1"], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.HasAnyAsync(source, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ContainsAsync(source, "1", CancellationToken.None));

        // Rejected before the database is touched: the file is not even created.
        Assert.False(File.Exists(DbPath));
    }

    private async Task<(object Value, string Type)> ReadSourceColumnAsync(string id)
    {
        await using var connection = await OpenDirectAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source, typeof(source) FROM seen WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return (reader.GetValue(0), reader.GetString(1));
    }

    private async Task<long> CountRowsAsync()
    {
        await using var connection = await OpenDirectAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM seen";

        return (long)(await command.ExecuteScalarAsync())!;
    }

    // Pooling is off, like in the store, so the file is released when the test deletes its directory.
    private async Task<SqliteConnection> OpenDirectAsync()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();

        return connection;
    }
}
