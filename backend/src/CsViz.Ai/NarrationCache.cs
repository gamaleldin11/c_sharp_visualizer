using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CsViz.Ai;

/// Persistent cache of model responses.
///
/// This is the single thing that makes an AI feature viable on a free tier. A visualizer is
/// used on the same handful of teaching programs over and over: the same source, stepped to
/// the same step, produces the same key, so a demo program is narrated once ever and every
/// later visitor is served from disk.
///
/// Keyed by the trace's source hash plus the step index plus a signature of that step's delta.
/// Including the delta means a source edit that does not change a given step still hits.
public sealed class NarrationCache : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public NarrationCache(string path)
    {
        // Shared cache so several requests can read concurrently; writes are serialised by the
        // lock below because SQLite allows only one writer.
        _connection = new SqliteConnection($"Data Source={path};Cache=Shared");
        _connection.Open();

        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS narration (
                key        TEXT PRIMARY KEY,
                kind       TEXT NOT NULL,
                response   TEXT NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS usage (
                day   TEXT PRIMARY KEY,
                calls INTEGER NOT NULL
            );
            """;
        create.ExecuteNonQuery();
    }

    public static string KeyFor(string kind, string sourceHash, int stepIndex, string deltaSignature)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{kind}\0{sourceHash}\0{stepIndex}\0{deltaSignature}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public string? TryGet(string key)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT response FROM narration WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void Put(string key, string kind, string response)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO narration (key, kind, response, created_at)
                VALUES ($key, $kind, $response, $now)
                ON CONFLICT(key) DO UPDATE SET response = excluded.response
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$response", response);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
    }

    /// Records one upstream call and reports whether the daily budget still allows it.
    ///
    /// Counted here, in the same durable store as the cache, so a restart cannot reset the
    /// budget - which is exactly when an accidental loop would otherwise run up a bill.
    public bool TryConsumeDailyBudget(int budget)
    {
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");

        lock (_lock)
        {
            using var read = _connection.CreateCommand();
            read.CommandText = "SELECT calls FROM usage WHERE day = $day";
            read.Parameters.AddWithValue("$day", day);
            var used = read.ExecuteScalar() is long n ? n : 0;

            if (used >= budget) return false;

            using var write = _connection.CreateCommand();
            write.CommandText = """
                INSERT INTO usage (day, calls) VALUES ($day, 1)
                ON CONFLICT(day) DO UPDATE SET calls = calls + 1
                """;
            write.Parameters.AddWithValue("$day", day);
            write.ExecuteNonQuery();
            return true;
        }
    }

    public int CallsToday()
    {
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT calls FROM usage WHERE day = $day";
            command.Parameters.AddWithValue("$day", day);
            return command.ExecuteScalar() is long n ? (int)n : 0;
        }
    }

    public void Dispose() => _connection.Dispose();
}
