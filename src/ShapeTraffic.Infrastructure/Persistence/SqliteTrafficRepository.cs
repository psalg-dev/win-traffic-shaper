using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ShapeTraffic.Core.Abstractions;
using ShapeTraffic.Core.Models;

namespace ShapeTraffic.Infrastructure.Persistence;

public sealed class SqliteTrafficRepository : ITrafficRepository
{
    private readonly string _databasePath;
    private readonly ILogger<SqliteTrafficRepository> _logger;

    public SqliteTrafficRepository(string databasePath, ILogger<SqliteTrafficRepository> logger)
    {
        _databasePath = databasePath;
        _logger = logger;

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SQLitePCL.Batteries_V2.Init();
        InitializeDatabase();
    }

    public Task<IReadOnlyList<TrafficLimitRule>> GetRulesAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync(async connection =>
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT process_key, process_name, upload_limit_bps, download_limit_bps, is_enabled, updated_at_utc
                FROM traffic_rules
                ORDER BY process_name;
                """;

            var rules = new List<TrafficLimitRule>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rules.Add(new TrafficLimitRule(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.GetInt64(4) == 1,
                    DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)));
            }

            return (IReadOnlyList<TrafficLimitRule>)rules;
        }, cancellationToken);
    }

    public Task UpsertRuleAsync(TrafficLimitRule rule, CancellationToken cancellationToken)
    {
        return ExecuteAsync(async connection =>
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO traffic_rules(process_key, process_name, upload_limit_bps, download_limit_bps, is_enabled, updated_at_utc)
                VALUES($processKey, $processName, $upload, $download, $enabled, $updatedAt)
                ON CONFLICT(process_key) DO UPDATE SET
                    process_name = excluded.process_name,
                    upload_limit_bps = excluded.upload_limit_bps,
                    download_limit_bps = excluded.download_limit_bps,
                    is_enabled = excluded.is_enabled,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$processKey", rule.ProcessKey);
            command.Parameters.AddWithValue("$processName", rule.ProcessName);
            command.Parameters.AddWithValue("$upload", (object?)rule.UploadLimitBytesPerSecond ?? DBNull.Value);
            command.Parameters.AddWithValue("$download", (object?)rule.DownloadLimitBytesPerSecond ?? DBNull.Value);
            command.Parameters.AddWithValue("$enabled", rule.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", rule.UpdatedAt.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task DeleteRuleAsync(string processKey, CancellationToken cancellationToken)
    {
        return ExecuteAsync(async connection =>
        {
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM traffic_rules WHERE process_key = $processKey;";
            command.Parameters.AddWithValue("$processKey", processKey);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task AppendAggregateSampleAsync(TrafficSample sample, CancellationToken cancellationToken)
    {
        return ExecuteAsync(async connection =>
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO aggregate_samples(timestamp_utc, upload_bytes, download_bytes)
                VALUES($timestamp, $upload, $download);
                """;
            command.Parameters.AddWithValue("$timestamp", sample.Timestamp.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$upload", sample.UploadBytes);
            command.Parameters.AddWithValue("$download", sample.DownloadBytes);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<TrafficSample>> GetAggregateSamplesAsync(DateTimeOffset fromInclusive, CancellationToken cancellationToken)
    {
        return ExecuteAsync(async connection =>
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT timestamp_utc, upload_bytes, download_bytes
                FROM aggregate_samples
                WHERE timestamp_utc >= $fromInclusive
                ORDER BY timestamp_utc;
                """;
            command.Parameters.AddWithValue("$fromInclusive", fromInclusive.UtcDateTime.ToString("O"));

            var samples = new List<TrafficSample>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                samples.Add(new TrafficSample(
                    DateTimeOffset.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.GetInt64(1),
                    reader.GetInt64(2)));
            }

            return (IReadOnlyList<TrafficSample>)samples;
        }, cancellationToken);
    }

    public Task PruneAggregateSamplesAsync(DateTimeOffset beforeExclusive, CancellationToken cancellationToken)
    {
        return ExecuteAsync(async connection =>
        {
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM aggregate_samples WHERE timestamp_utc < $beforeExclusive;";
            command.Parameters.AddWithValue("$beforeExclusive", beforeExclusive.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task ExecuteAsync(Func<SqliteConnection, Task> operation, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await operation(connection).ConfigureAwait(false);
    }

    private async Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> operation, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await operation(connection).ConfigureAwait(false);
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        pragma.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS traffic_rules(
                process_key TEXT PRIMARY KEY,
                process_name TEXT NOT NULL,
                upload_limit_bps INTEGER NULL,
                download_limit_bps INTEGER NULL,
                is_enabled INTEGER NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS aggregate_samples(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                upload_bytes INTEGER NOT NULL,
                download_bytes INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_aggregate_samples_timestamp_utc
            ON aggregate_samples(timestamp_utc);
            """;

        command.ExecuteNonQuery();
        _logger.LogInformation("SQLite database initialized at {DatabasePath}.", _databasePath);
    }
}