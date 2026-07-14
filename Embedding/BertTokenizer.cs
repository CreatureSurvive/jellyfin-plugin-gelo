// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.Gelo.Embedding;

/// <summary>
/// Uncased BERT WordPiece tokenizer for all-MiniLM-L6-v2 (shares the bert-base-uncased vocab).
/// Hand-rolled to avoid native deps; faithfully implements Google's reference BasicTokenizer +
/// WordpieceTokenizer. Special token ids are resolved from vocab.txt at load time.
/// </summary>
internal sealed class BertTokenizer
{
    private const string ClsToken = "[CLS]";
    private const string SepToken = "[SEP]";
    private const string PadToken = "[PAD]";
    private const string UnkToken = "[UNK]";
    private const int MaxInputCharsPerWord = 200;

    private readonly Dictionary<string, int> _vocab;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _padId;
    private readonly int _unkId;

    private BertTokenizer(Dictionary<string, int> vocab, int clsId, int sepId, int padId, int unkId)
    {
        _vocab = vocab;
        _clsId = clsId;
        _sepId = sepId;
        _padId = padId;
        _unkId = unkId;
    }

    public int PadId => _padId;
    public int VocabSize => _vocab.Count;

    public static BertTokenizer Load(string vocabPath)
    {
        var vocab = new Dictionary<string, int>(32000, StringComparer.Ordinal);
        var lines = File.ReadAllLines(vocabPath);
        for (var i = 0; i < lines.Length; i++)
        {
            // vocab.txt lines are tokens; trailing whitespace already trimmed by ReadAllLines.
            if (!vocab.ContainsKey(lines[i]))
            {
                vocab[lines[i]] = i;
            }
        }

        return new BertTokenizer(
            vocab,
            clsId: IdOr(vocab, ClsToken, 101),
            sepId: IdOr(vocab, SepToken, 102),
            padId: IdOr(vocab, PadToken, 0),
            unkId: IdOr(vocab, UnkToken, 100));
    }

    private static int IdOr(IReadOnlyDictionary<string, int> vocab, string token, int fallback)
        => vocab.TryGetValue(token, out var id) ? id : fallback;

    /// <summary>Tokenize + truncate + add [CLS]/[SEP]; returns ids and attention mask.</summary>
    public void Encode(string text, int maxSequenceLength, List<int> inputIds, List<int> attentionMask)
    {
        inputIds.Clear();
        attentionMask.Clear();

        inputIds.Add(_clsId);
        attentionMask.Add(1);

        foreach (var token in Tokenize(text))
        {
            if (inputIds.Count >= maxSequenceLength - 1)
            {
                break; // leave room for [SEP]
            }

            inputIds.Add(_vocab.TryGetValue(token, out var id) ? id : _unkId);
            attentionMask.Add(1);
        }

        inputIds.Add(_sepId);
        attentionMask.Add(1);
    }

    private IEnumerable<string> Tokenize(string text)
    {
        foreach (var word in BasicTokenize(text))
        {
            foreach (var sub in WordpieceTokenize(word))
            {
                yield return sub;
            }
        }
    }

    private static IEnumerable<string> BasicTokenize(string text)
    {
        text = CleanText(text);
        // CJK isolation improves handling of non-Latin titles; harmless for English metadata.
        text = IsolateCjk(text);

        var lowered = text.ToLowerInvariant();
        var stripped = StripAccents(lowered);

        var sb = new StringBuilder();
        foreach (var ch in stripped)
        {
            if (IsControl(ch) && ch is not '\t' and not '\n' and not '\r')
            {
                continue;
            }

            if (IsWhiteSpace(ch))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            else if (IsPunctuation(ch))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }

                yield return ch.ToString();
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private IEnumerable<string> WordpieceTokenize(string word)
    {
        if (word.Length > MaxInputCharsPerWord)
        {
            yield return UnkToken;
            yield break;
        }

        var isBad = false;
        var start = 0;
        var subTokens = new List<string>();

        while (start < word.Length)
        {
            var end = word.Length;
            var curSubstring = (string?)null;

            while (start < end)
            {
                var substring = word.Substring(start, end - start);
                var candidate = start == 0 ? substring : "##" + substring;
                if (_vocab.ContainsKey(candidate))
                {
                    curSubstring = candidate;
                    break;
                }

                end--;
            }

            if (curSubstring is null)
            {
                isBad = true;
                break;
            }

            subTokens.Add(curSubstring);
            start = end;
        }

        if (isBad)
        {
            yield return UnkToken;
        }
        else
        {
            foreach (var t in subTokens)
            {
                yield return t;
            }
        }
    }

    private static string CleanText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var cp = (int)ch;
            if (cp == 0 || cp == 0xFFFD || (IsControl(ch) && ch is not '\t' and not '\n' and not '\r'))
            {
                continue;
            }

            sb.Append(IsWhiteSpace(ch) ? ' ' : ch);
        }

        return sb.ToString();
    }

    private static string IsolateCjk(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var ch in text)
        {
            var cp = (int)ch;
            if (IsCjkCodePoint(cp))
            {
                sb.Append(' ').Append(ch).Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static bool IsCjkCodePoint(int cp)
        => (cp >= 0x4E00 && cp <= 0x9FFF)
           || (cp >= 0x3400 && cp <= 0x4DBF)
           || (cp >= 0x20000 && cp <= 0x2A6DF)
           || (cp >= 0x2A700 && cp <= 0x2B73F)
           || (cp >= 0x2B740 && cp <= 0x2B81F)
           || (cp >= 0x2B820 && cp <= 0x2CEAF)
           || (cp >= 0xF900 && cp <= 0xFAFF)
           || (cp >= 0x2F800 && cp <= 0x2FA1F);

    private static string StripAccents(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static bool IsWhiteSpace(char ch)
        => ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == (char)0x00A0 || (int)ch == 0x200B;

    private static bool IsControl(char ch)
    {
        if (char.IsControl(ch))
        {
            return true;
        }

        var cp = (int)ch;
        return (cp >= 0x200B && cp <= 0x200F) || cp == 0xFEFF;
    }

    private static bool IsPunctuation(char ch)
    {
        var cp = (int)ch;
        if ((cp >= 33 && cp <= 47) || (cp >= 58 && cp <= 64) || (cp >= 91 && cp <= 96) || (cp >= 123 && cp <= 126))
        {
            return true;
        }

        return CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.OtherPunctuation
               || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.ConnectorPunctuation
               || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.DashPunctuation
               || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.OpenPunctuation
               || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.ClosePunctuation
               || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.InitialQuotePunctuation
               || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.FinalQuotePunctuation;
    }
}
