namespace FastBatchLevenshtein;

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

public sealed class FBLEngine
{
    public const int MaxKeyLength = 32;

    private readonly Bucket _bucket8;
    private readonly Bucket _bucket16;
    private readonly Bucket _bucket32;

    public FBLEngine(ReadOnlySpan<NormalizedRecord> records)
    {
        Build(records, out _bucket8, out _bucket16, out _bucket32);
    }

    public FBLEngine(ReadOnlyMemory<byte>[] normalizedRecords, int[]? ids = null)
    {
        ArgumentNullException.ThrowIfNull(normalizedRecords);

        ids ??= Enumerable.Range(0, normalizedRecords.Length).ToArray();

        if (normalizedRecords.Length != ids.Length)
            throw new ArgumentException("records and ids must have the same length.", nameof(ids));

        NormalizedRecord[] records = new NormalizedRecord[normalizedRecords.Length];

        for (int i = 0; i < normalizedRecords.Length; i++)
            records[i] = new NormalizedRecord(ids[i], normalizedRecords[i]);

        Build(records, out _bucket8, out _bucket16, out _bucket32);
    }

    public int Search(ReadOnlySpan<byte> normalizedQuery, Span<MatchResult> results, SearchOptions options = default)
    {
        int maxEdits = options.MaxEdits;

        if (normalizedQuery.Length == 0 || normalizedQuery.Length > MaxKeyLength)
            return 0;

        int written = 0;
        if (normalizedQuery.Length < 8 + maxEdits)
            SearchBucket(_bucket8, normalizedQuery, results, options, ref written);
        if (normalizedQuery.Length < 16 + maxEdits && normalizedQuery.Length > 8 - maxEdits)
            SearchBucket(_bucket16, normalizedQuery, results, options, ref written);
        if (normalizedQuery.Length > 16 - maxEdits)
            SearchBucket(_bucket32, normalizedQuery, results, options, ref written);

        return written;
    }

    private static void SearchBucket(Bucket bucket, ReadOnlySpan<byte> query, Span<MatchResult> results, SearchOptions options, ref int written)
    {
        if (bucket.Count == 0)
            return;

        int maxEdits = options.MaxEdits;
        float maxRatio = options.MaxRatio;

        int queryLength = query.Length;
        int width = bucket.Width;
        byte[] data = bucket.Data;
        byte[] lengths = bucket.Lengths;
        int[] ids = bucket.Ids;

        for (int i = 0; i < bucket.Count; i++)
        {
            int candidateLength = lengths[i];

            if (Math.Abs(queryLength - candidateLength) > maxEdits)
                continue;

            int offset = i * width;
            ReadOnlySpan<byte> candidate = data.AsSpan(offset, candidateLength);
            int distance = BoundedLevenshtein(query, candidate, maxEdits);

            if (distance > maxEdits) continue;
            if (written >= results.Length) return;
            float score = ComputeScore(queryLength, candidateLength, distance);
            if (score < maxRatio) continue;

            results[written++] = new MatchResult(ids[i], distance, score);

        }
    }

    private static void Build(ReadOnlySpan<NormalizedRecord> records, out Bucket bucket8, out Bucket bucket16, out Bucket bucket32)
    {
        int count8 = 0;
        int count16 = 0;
        int count32 = 0;

        for (int i = 0; i < records.Length; i++)
        {
            int len = records[i].Key.Length;

            if (len == 0)
                continue;

            if (len <= 8)
                count8++;
            else if (len <= 16)
                count16++;
            else if (len <= MaxKeyLength)
                count32++;
        }

        bucket8 = new Bucket(8, count8);
        bucket16 = new Bucket(16, count16);
        bucket32 = new Bucket(32, count32);

        int index8 = 0;
        int index16 = 0;
        int index32 = 0;

        for (int i = 0; i < records.Length; i++)
        {
            ReadOnlySpan<byte> key = records[i].Key.Span;

            if (key.Length is 0 or > MaxKeyLength)
                continue;

            if (key.Length <= 8)
                bucket8.Write(index8++, key, records[i].Id);
            else if (key.Length <= 16)
                bucket16.Write(index16++, key, records[i].Id);
            else
                bucket32.Write(index32++, key, records[i].Id);
        }
    }

    private static int BoundedLevenshtein(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int maxDistance)
    {
        int n = a.Length;
        int m = b.Length;

        // It should be Redundant as stuff should be handled before
        //if (Math.Abs(n - m) > maxDistance) return maxDistance + 1;
        //if (n == 0) return m <= maxDistance ? m : maxDistance + 1;
        //if (m == 0) return n <= maxDistance ? n : maxDistance + 1;

        Span<int> previous = stackalloc int[MaxKeyLength + 1];
        Span<int> current = stackalloc int[MaxKeyLength + 1];

        for (int j = 0; j <= m; j++)
            previous[j] = j;

        for (int i = 1; i <= n; i++)
        {
            current[0] = i;
            int rowMin = current[0];
            int from = Math.Max(1, i - maxDistance);
            int to = Math.Min(m, i + maxDistance);

            for (int j = 1; j < from; j++)
                current[j] = maxDistance + 1;

            for (int j = from; j <= to; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int deletion = previous[j] + 1;
                int insertion = current[j - 1] + 1;
                int substitution = previous[j - 1] + cost;
                int value = deletion < insertion ? deletion : insertion;

                value = value < substitution ? value : substitution;
                current[j] = value;

                if (value < rowMin)
                    rowMin = value;
            }

            for (int j = to + 1; j <= m; j++)
                current[j] = maxDistance + 1;

            if (rowMin > maxDistance)
                return maxDistance + 1;

            Span<int> swap = previous;
            previous = current;
            current = swap;
        }

        return previous[m];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeScore(int queryLength, int candidateLength, int distance)
    {
        int maxLen = Math.Max(queryLength, candidateLength);
        return maxLen == 0 ? 1.0f : 1.0f - ((float)distance / maxLen);
    }

    private sealed class Bucket
    {
        public readonly int Width;
        public readonly int Count;
        public readonly byte[] Data;
        public readonly byte[] Lengths;
        public readonly int[] Ids;

        public Bucket(int width, int count)
        {
            Width = width;
            Count = count;
            Data = GC.AllocateUninitializedArray<byte>(width * count);
            Lengths = GC.AllocateUninitializedArray<byte>(count);
            Ids = GC.AllocateUninitializedArray<int>(count);
            Array.Clear(Data);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int index, ReadOnlySpan<byte> key, int id)
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            if (key.Length > Width)
                throw new ArgumentOutOfRangeException(nameof(key));

            int offset = index * Width;
            key.CopyTo(Data.AsSpan(offset, key.Length));
            Lengths[index] = (byte)key.Length;
            Ids[index] = id;
        }
    }
}

public static class FBLNameNormalizer
{
    public static int Normalize(ReadOnlySpan<char> input, Span<byte> destination)
    {
        int written = 0;

        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];

            if (ch <= 0x7F)
            {
                if (TryWriteAscii(ch, destination, ref written))
                    continue;

                continue;
            }

            return NormalizeNonAsciiFallback(input, destination);
        }

        return written;
    }

    private static int NormalizeNonAsciiFallback(ReadOnlySpan<char> input, Span<byte> destination)
    {
        string normalized = input.ToString().Normalize(NormalizationForm.FormD);
        int written = 0;

        foreach (char ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (ch <= 0x7F)
                TryWriteAscii(ch, destination, ref written);
        }

        return written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryWriteAscii(char ch, Span<byte> destination, ref int written)
    {
        byte b = (byte)ch;

        if ((uint)(b - 'A') <= 'Z' - 'A')
            b = (byte)(b | 0x20);

        bool isLetter = (uint)(b - 'a') <= 'z' - 'a';
        bool isDigit = (uint)(b - '0') <= '9' - '0';

        if (!isLetter && !isDigit)
            return false;

        if (written >= destination.Length)
            return false;

        destination[written++] = b;
        return true;
    }
}

public readonly record struct SearchOptions(int MaxEdits = 2, float MaxRatio = 0.75f);
public readonly record struct NormalizedRecord(int Id, ReadOnlyMemory<byte> Key);
public readonly record struct MatchResult(int CandidateId, int EditDistance, double Score);