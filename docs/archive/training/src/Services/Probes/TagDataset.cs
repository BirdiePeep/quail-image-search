using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Parquet;
using Parquet.Schema;

namespace QuailImageSearch.Services.Probes;

/// <summary>
/// A tagged image dataset stored as Parquet files, the layout Hugging Face uses for image datasets:
/// one row per image, with the encoded image in a binary column (usually the
/// "bytes" field of an "image" struct) and its tags in a text column (space-separated) or a list
/// of strings.
/// </summary>
public static class TagDataset
{
    // Tried in order; the first text/list column with one of these names holds the tags.
    private static readonly string[] TagColumnNames = ["tags", "text", "tag_string", "caption"];

    /// <summary>The columns used from one Parquet file.</summary>
    public sealed record Columns(DataField Image, DataField Tags)
    {
        public override string ToString() => $"image: {Image.Path}, tags: {Tags.Path}{(Tags.MaxRepetitionLevel > 0 ? " (list)" : "")}";
    }

    /// <summary>One row group: encoded images (null if missing) and each image's tags.</summary>
    public sealed record RowGroup(byte[]?[] Images, string[][] Tags);

    public sealed record Summary(IReadOnlyList<string> Files, long Rows, string ColumnsText);

    /// <summary>All .parquet files under <paramref name="folder"/>, in a stable order.</summary>
    public static List<string> FindFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.parquet", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(folder, f).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.')))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    /// <summary>Counts the rows and checks the columns of every file (reads only the file footers).</summary>
    public static async Task<Summary> InspectAsync(string folder, CancellationToken ct)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"folder not found: {folder}");
        var files = FindFiles(folder);
        if (files.Count == 0) throw new InvalidDataException("no .parquet files in this folder (or its subfolders)");
        long rows = 0;
        string? columns = null;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await using var reader = await ParquetReader.CreateAsync(file, cancellationToken: ct);
            columns ??= FindColumns(reader.Schema, file).ToString();
            rows += reader.RowGroups.Sum(g => g.RowCount);
        }
        return new Summary(files, rows, columns!);
    }

    public static Columns FindColumns(ParquetSchema schema, string file)
    {
        var fields = schema.GetDataFields();
        // Parquet.Net reports binary and text columns as ReadOnlyMemory<byte>/<char> (or byte[]/string).
        var binary = fields.Where(f => (f.ClrType == typeof(byte[]) || f.ClrType == typeof(ReadOnlyMemory<byte>)) && f.MaxRepetitionLevel == 0).ToList();
        var image = binary.FirstOrDefault(f => f.Path.ToString().Contains("image", StringComparison.OrdinalIgnoreCase) && f.Name == "bytes")
                    ?? binary.FirstOrDefault(f => f.Name is "bytes" or "image" or "jpg" or "png")
                    ?? binary.FirstOrDefault()
                    ?? throw new InvalidDataException($"{Path.GetFileName(file)}: no binary image column (columns: {string.Join(", ", fields.Select(f => f.Path))})");
        var strings = fields.Where(f => f.ClrType == typeof(string) || f.ClrType == typeof(ReadOnlyMemory<char>)).ToList();
        var tags = TagColumnNames.Select(n => strings.FirstOrDefault(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)
                                                                          || string.Equals(f.Path.ToList()[0], n, StringComparison.OrdinalIgnoreCase)))
                       .FirstOrDefault(f => f is not null)
                   ?? throw new InvalidDataException($"{Path.GetFileName(file)}: no tags column (looked for {string.Join(", ", TagColumnNames)}; " +
                                                     $"columns: {string.Join(", ", fields.Select(f => f.Path))})");
        return new Columns(image, tags);
    }

    /// <summary>Reads one row group's images and tags.</summary>
    public static async Task<RowGroup> ReadRowGroupAsync(ParquetReader reader, Columns columns, int index, CancellationToken ct)
    {
        using var group = reader.OpenRowGroupReader(index);
        var rows = checked((int)group.RowCount);

        var images = new byte[]?[rows];
        await group.ReadAsync(columns.Image, images.AsMemory(), null, ct);

        string[][] tags;
        if (columns.Tags.MaxRepetitionLevel == 0)
        {
            var text = new string?[rows];
            await group.ReadAsync(columns.Tags, text.AsMemory(), null, ct);
            tags = text.Select(Split).ToArray();
        }
        else
        {
            // A list column. Read raw: the non-null values, plus a definition and repetition level per
            // entry. A new row starts wherever the repetition level is 0; an entry holds a value only
            // at the maximum definition level (lower means an empty or missing list, or a null element).
            // (ReadAsync with repetition levels sizes a buffer by row count and fails on lists.)
            using var raw = (Parquet.Data.RawColumnData<ReadOnlyMemory<char>>)await group.ReadRawColumnDataBaseAsync(columns.Tags, ct);
            var values = raw.Values;
            var definitions = raw.DefinitionLevels;
            var repetitions = raw.RepetitionLevels;
            var maxDefinition = columns.Tags.MaxDefinitionLevel;
            var lists = new List<string[]>(rows);
            var current = new List<string>();
            var next = 0;
            for (var i = 0; i < repetitions.Length; i++)
            {
                if (repetitions[i] == 0 && i > 0)
                {
                    lists.Add(current.ToArray());
                    current.Clear();
                }
                if (definitions.Length > 0 && definitions[i] < maxDefinition) continue;
                var tag = values[next++].ToString().Trim();
                if (tag.Length > 0) current.Add(tag);
            }
            if (repetitions.Length > 0) lists.Add(current.ToArray());
            while (lists.Count < rows) lists.Add([]);
            tags = lists.ToArray();
        }
        return new RowGroup(images, tags);
    }

    private static string[] Split(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
