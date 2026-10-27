using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuailImageSearch.Services.Models;

public interface ITextTokenizer
{
    /// <summary>Token ids for one query, including special tokens, truncated to the model's context.</summary>
    long[] Encode(string text);
}

/// <summary>
/// CLIP's byte-level BPE tokenizer (OpenAI CLIP, MobileCLIP), ported from OpenAI's
/// simple_tokenizer.py / Hugging Face's CLIPTokenizer. Reads vocab and merges from tokenizer.json.
/// </summary>
public sealed class ClipTokenizer : ITextTokenizer
{
    private const string StartToken = "<|startoftext|>";
    private const string EndToken = "<|endoftext|>";

    private static readonly Regex Pattern = new(
        @"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly Dictionary<string, int> _encoder;
    private readonly Dictionary<(string, string), int> _bpeRanks = new();
    private readonly string[] _byteEncoder = BytesToUnicode();
    private readonly Dictionary<string, string> _cache = new();
    private readonly int _contextLength;

    public ClipTokenizer(string tokenizerJsonPath, int contextLength = 77)
    {
        _contextLength = contextLength;
        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJsonPath));
        var model = doc.RootElement.GetProperty("model");
        if (model.GetProperty("type").GetString() != "BPE")
            throw new InvalidDataException("tokenizer.json isn't a BPE tokenizer");

        _encoder = model.GetProperty("vocab").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        var rank = 0;
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            // Older tokenizer.json files store "a b", newer ones ["a", "b"].
            var (a, b) = merge.ValueKind == JsonValueKind.Array
                ? (merge[0].GetString()!, merge[1].GetString()!)
                : Split(merge.GetString()!);
            _bpeRanks.TryAdd((a, b), rank++);
        }
        StartId = _encoder[StartToken];
        EndId = _encoder[EndToken];

        static (string, string) Split(string s)
        {
            var i = s.IndexOf(' ');
            return (s[..i], s[(i + 1)..]);
        }
    }

    public int StartId { get; }
    public int EndId { get; }

    public long[] Encode(string text)
    {
        text = WebUtility.HtmlDecode(text).Normalize(NormalizationForm.FormC);
        text = Whitespace.Replace(text, " ").Trim().ToLowerInvariant();

        var ids = new List<long> { StartId };
        foreach (Match m in Pattern.Matches(text))
        {
            var mapped = string.Concat(Encoding.UTF8.GetBytes(m.Value).Select(b => _byteEncoder[b]));
            foreach (var piece in Bpe(mapped).Split(' '))
                if (_encoder.TryGetValue(piece, out var id)) ids.Add(id);
        }
        if (ids.Count > _contextLength - 1) ids.RemoveRange(_contextLength - 1, ids.Count - (_contextLength - 1));
        ids.Add(EndId);
        return ids.ToArray();
    }

    private string Bpe(string token)
    {
        if (_cache.TryGetValue(token, out var cached)) return cached;

        // One entry per character (the byte-to-unicode table is all BMP), "</w>" marking the word end.
        var word = token.Select((c, i) => i == token.Length - 1 ? c + "</w>" : c.ToString()).ToList();

        while (word.Count > 1)
        {
            (string, string)? best = null;
            var bestRank = int.MaxValue;
            for (var i = 0; i < word.Count - 1; i++)
            {
                if (_bpeRanks.TryGetValue((word[i], word[i + 1]), out var r) && r < bestRank)
                {
                    bestRank = r;
                    best = (word[i], word[i + 1]);
                }
            }
            if (best is not var (first, second)) break;

            var merged = new List<string>(word.Count);
            for (var i = 0; i < word.Count; i++)
            {
                if (i < word.Count - 1 && word[i] == first && word[i + 1] == second)
                {
                    merged.Add(first + second);
                    i++;
                }
                else
                {
                    merged.Add(word[i]);
                }
            }
            word = merged;
        }

        var result = string.Join(' ', word);
        if (_cache.Count < 10_000) _cache[token] = result;
        return result;
    }

    /// <summary>GPT-2/CLIP mapping of the 256 byte values to printable unicode characters.</summary>
    private static string[] BytesToUnicode()
    {
        var bs = new List<int>();
        bs.AddRange(Enumerable.Range('!', '~' - '!' + 1));
        bs.AddRange(Enumerable.Range('¡', '¬' - '¡' + 1));
        bs.AddRange(Enumerable.Range('®', 'ÿ' - '®' + 1));
        var cs = new List<int>(bs);
        var n = 0;
        for (var b = 0; b < 256; b++)
        {
            if (bs.Contains(b)) continue;
            bs.Add(b);
            cs.Add(256 + n++);
        }
        var table = new string[256];
        for (var i = 0; i < bs.Count; i++)
            table[bs[i]] = ((char)cs[i]).ToString();
        return table;
    }
}

/// <summary>
/// SigLIP's SentencePiece Unigram tokenizer, from tokenizer.json. Text is canonicalized as in
/// Hugging Face's SiglipTokenizer (lowercase, ASCII punctuation removed, whitespace collapsed),
/// then split into "▁"-prefixed words and segmented with Viterbi over the piece log-probabilities.
/// Ends with &lt;/s&gt; like the reference; padding is applied by the model (see TextSpec).
/// </summary>
public sealed class SiglipTokenizer : ITextTokenizer
{
    private const char Metaspace = '▁';
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly Dictionary<string, (int Id, double Score)> _pieces = new(StringComparer.Ordinal);
    private readonly int _maxPieceLength;
    private readonly int _unkId;
    private readonly double _unkScore;
    private readonly int _eosId;
    private readonly int _maxLength;

    public SiglipTokenizer(string tokenizerJsonPath, int maxLength = 64)
    {
        _maxLength = maxLength;
        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJsonPath));
        var model = doc.RootElement.GetProperty("model");
        if (model.GetProperty("type").GetString() != "Unigram")
            throw new InvalidDataException("tokenizer.json isn't a Unigram tokenizer");

        var id = 0;
        var minScore = double.MaxValue;
        foreach (var entry in model.GetProperty("vocab").EnumerateArray())
        {
            var piece = entry[0].GetString()!;
            var score = entry[1].GetDouble();
            _pieces.TryAdd(piece, (id, score));
            _maxPieceLength = Math.Max(_maxPieceLength, piece.Length);
            minScore = Math.Min(minScore, score);
            id++;
        }
        _unkId = model.GetProperty("unk_id").GetInt32();
        _unkScore = minScore - 10.0; // same penalty as the tokenizers library
        _eosId = _pieces.TryGetValue("</s>", out var eos) ? eos.Id : 1;
    }

    public long[] Encode(string text)
    {
        text = Canonicalize(text);
        var ids = new List<long>();
        if (text.Length > 0)
        {
            // Metaspace: every word gets a leading "▁" and is segmented on its own.
            foreach (var word in text.Split(' '))
                Segment(Metaspace + word, ids);
        }
        if (ids.Count > _maxLength - 1) ids.RemoveRange(_maxLength - 1, ids.Count - (_maxLength - 1));
        ids.Add(_eosId);
        return ids.ToArray();
    }

    private static string Canonicalize(string text)
    {
        text = text.ToLowerInvariant();
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!(c < 128 && char.IsPunctuation(c) || c < 128 && char.IsSymbol(c))) // Python's string.punctuation
                sb.Append(c);
        // SentencePiece's nmt_nfkc normalization, approximated by NFKC.
        return Whitespace.Replace(sb.ToString().Normalize(NormalizationForm.FormKC), " ").Trim();
    }

    /// <summary>Viterbi segmentation maximizing the summed piece log-probabilities.</summary>
    private void Segment(string word, List<long> ids)
    {
        var n = word.Length;
        var best = new double[n + 1];
        var from = new int[n + 1];
        var pieceId = new int[n + 1];
        Array.Fill(best, double.NegativeInfinity);
        best[0] = 0;

        for (var start = 0; start < n; start++)
        {
            if (double.IsNegativeInfinity(best[start])) continue;
            if (char.IsLowSurrogate(word[start])) continue; // never start inside a surrogate pair
            var any = false;
            for (var len = 1; len <= _maxPieceLength && start + len <= n; len++)
            {
                if (_pieces.TryGetValue(word.Substring(start, len), out var p))
                {
                    any |= len == CharLength(word, start);
                    var score = best[start] + p.Score;
                    if (score > best[start + len])
                    {
                        best[start + len] = score;
                        from[start + len] = start;
                        pieceId[start + len] = p.Id;
                    }
                }
            }
            if (!any)
            {
                // No piece for this character: emit <unk> for it.
                var end = start + CharLength(word, start);
                var score = best[start] + _unkScore;
                if (score > best[end])
                {
                    best[end] = score;
                    from[end] = start;
                    pieceId[end] = _unkId;
                }
            }
        }

        var path = new List<int>();
        for (var pos = n; pos > 0; pos = from[pos]) path.Add(pieceId[pos]);
        path.Reverse();
        foreach (var p in path)
        {
            if (p == _unkId && ids.Count > 0 && ids[^1] == _unkId) continue; // consecutive unknowns fuse
            ids.Add(p);
        }
    }

    private static int CharLength(string s, int i) => char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
}
