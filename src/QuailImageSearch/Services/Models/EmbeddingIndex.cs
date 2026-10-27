using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace QuailImageSearch.Services.Models;

/// <summary>Scores one stored image embedding (higher is better).</summary>
public delegate double EmbeddingScorer(ReadOnlySpan<float> embedding);

/// <summary>
/// Per-model store of image embeddings (SQLite), keyed by absolute file path plus size/mtime so only
/// new or changed files get re-embedded. Folders share one database per model, so indexing a folder
/// also covers later searches in any of its subfolders.
/// </summary>
public sealed class EmbeddingIndex : IDisposable
{
    private readonly SqliteConnection _db;

    /// <param name="version">
    /// Identifies the model + preprocessing that produced the embeddings. If it differs from what the
    /// database was built with, the stored embeddings are discarded and images get re-embedded.
    /// </param>
    public EmbeddingIndex(string dbPath, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        // embedding is NULL for files that couldn't be decoded, so they aren't retried until they change.
        Exec("CREATE TABLE IF NOT EXISTS images (path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime INTEGER NOT NULL, embedding BLOB);");
        Exec("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");

        using var get = _db.CreateCommand();
        get.CommandText = "SELECT value FROM meta WHERE key = 'version'";
        if (get.ExecuteScalar() as string != version)
        {
            using var count = _db.CreateCommand();
            count.CommandText = "SELECT EXISTS (SELECT 1 FROM images)";
            WasReset = Convert.ToInt64(count.ExecuteScalar()) == 1;
            Exec("DELETE FROM images;");
            using var set = _db.CreateCommand();
            set.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('version', $v)";
            set.Parameters.AddWithValue("$v", version);
            set.ExecuteNonQuery();
        }
    }

    /// <summary>True if existing embeddings were discarded because the version changed.</summary>
    public bool WasReset { get; }

    public sealed record Entry(long Size, long MTime);

    /// <summary>Indexed files under <paramref name="folder"/> (recursively).</summary>
    public Dictionary<string, Entry> ListUnder(string folder)
    {
        var (lo, hi) = Range(folder);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path, size, mtime FROM images WHERE path >= $lo AND path < $hi";
        cmd.Parameters.AddWithValue("$lo", lo);
        cmd.Parameters.AddWithValue("$hi", hi);
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetString(0)] = new Entry(r.GetInt64(1), r.GetInt64(2));
        return result;
    }

    /// <summary>
    /// The stored embedding of <paramref name="path"/>, or null if it isn't indexed, has changed
    /// since (size or modification time differ), or couldn't be decoded.
    /// </summary>
    public float[]? TryGet(string path, long size, long mtime)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT embedding FROM images WHERE path = $p AND size = $s AND mtime = $m AND embedding IS NOT NULL";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$s", size);
        cmd.Parameters.AddWithValue("$m", mtime);
        return cmd.ExecuteScalar() is byte[] blob ? MemoryMarshal.Cast<byte, float>(blob).ToArray() : null;
    }

    /// <summary>Embeddings of the images under <paramref name="folder"/> (recursively) that have <paramref name="dim"/> values.</summary>
    public List<float[]> EmbeddingsUnder(string folder, int dim)
    {
        var (lo, hi) = Range(folder);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT embedding FROM images WHERE path >= $lo AND path < $hi AND embedding IS NOT NULL";
        cmd.Parameters.AddWithValue("$lo", lo);
        cmd.Parameters.AddWithValue("$hi", hi);
        var result = new List<float[]>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var embedding = MemoryMarshal.Cast<byte, float>((byte[])r.GetValue(0));
            if (embedding.Length == dim) result.Add(embedding.ToArray());
        }
        return result;
    }

    public void Upsert(IEnumerable<(string Path, long Size, long MTime, float[]? Embedding)> rows)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO images (path, size, mtime, embedding) VALUES ($p, $s, $m, $e)";
        var p = cmd.Parameters.Add("$p", SqliteType.Text);
        var s = cmd.Parameters.Add("$s", SqliteType.Integer);
        var m = cmd.Parameters.Add("$m", SqliteType.Integer);
        var e = cmd.Parameters.Add("$e", SqliteType.Blob);
        foreach (var row in rows)
        {
            p.Value = row.Path;
            s.Value = row.Size;
            m.Value = row.MTime;
            e.Value = row.Embedding is null ? DBNull.Value : MemoryMarshal.AsBytes(row.Embedding.AsSpan()).ToArray();
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Delete(IEnumerable<string> paths)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM images WHERE path = $p";
        var p = cmd.Parameters.Add("$p", SqliteType.Text);
        foreach (var path in paths)
        {
            p.Value = path;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Scores every embedded image under <paramref name="folder"/> whose embedding has
    /// <paramref name="dim"/> values, and returns the <paramref name="top"/> best.
    /// </summary>
    public List<SearchResult> Search(string folder, int dim, EmbeddingScorer score, int top)
    {
        var (lo, hi) = Range(folder);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path, embedding FROM images WHERE path >= $lo AND path < $hi AND embedding IS NOT NULL";
        cmd.Parameters.AddWithValue("$lo", lo);
        cmd.Parameters.AddWithValue("$hi", hi);

        // Keep the best `top` in a min-heap so memory stays small for huge folders.
        var heap = new PriorityQueue<SearchResult, double>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var embedding = MemoryMarshal.Cast<byte, float>((byte[])r.GetValue(1));
            if (embedding.Length != dim) continue; // written by a different model
            var s = score(embedding);
            if (heap.Count < top) heap.Enqueue(new SearchResult(r.GetString(0), s), s);
            else if (s > heap.Peek().Score) heap.EnqueueDequeue(new SearchResult(r.GetString(0), s), s);
        }
        var results = new List<SearchResult>(heap.Count);
        while (heap.Count > 0) results.Add(heap.Dequeue());
        results.Reverse();
        return results;
    }

    /// <summary>[lo, hi) key range covering every path inside <paramref name="folder"/>.</summary>
    private static (string Lo, string Hi) Range(string folder)
    {
        var prefix = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        // Every string starting with "folder\" sorts before "folder]" ('\' + 1), whatever follows.
        return (prefix, prefix[..^1] + (char)(Path.DirectorySeparatorChar + 1));
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
