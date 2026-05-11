namespace FastBatchLevenshtein;

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;


/// <summary>
/// A allocation-free fuzzy matching engine optimized for batch searching over normalized keys using a bounded Levenshtein distance
/// </summary>
public sealed class FBLEngine
{
    /// <summary>
    /// The maximum supported length (in bytes) for a normalized key.
    /// </summary>
    /// <remarks>
    /// The engine is optimized for short, ASCII-only normalized keys and uses stack-allocated
    /// buffers sized to this limit.
    /// </remarks>
    public const int MaxKeyLength = 32;

    private readonly Bucket _bucket8;
    private readonly Bucket _bucket16;
    private readonly Bucket _bucket32;

    /// <summary>
    /// Initializes a new <see cref="FBLEngine"/> from pre-normalized records.
    /// </summary>
    /// <param name="records">Records containing an identifier and a normalized key.</param>
    /// <remarks>
    /// Keys longer than <see cref="MaxKeyLength"/> (or empty keys) are ignored.
    /// </remarks>
    public FBLEngine(ReadOnlySpan<NormalizedRecord> records) => Build(records, out _bucket8, out _bucket16, out _bucket32);

    /// <summary>
    /// Initializes a new <see cref="FBLEngine"/> from pre-normalized keys and optional ids.
    /// </summary>
    /// <param name="normalizedRecords">The normalized keys (ASCII letters and digits only).</param>
    /// <param name="ids">
    /// Optional ids associated with <paramref name="normalizedRecords"/>. If <see langword="null"/>,
    /// ids are assigned sequentially (0..N-1).
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="normalizedRecords"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="ids"/> is provided with a different length.</exception>
    public FBLEngine(ReadOnlyMemory<byte>[] normalizedRecords, int[]? ids = null)
    {
        ArgumentNullException.ThrowIfNull(normalizedRecords);

        if (ids is null)
        {
            ids = new int[normalizedRecords.Length];
            for (int i = 0; i < ids.Length; i++)
                ids[i] = i;
        }

        if (normalizedRecords.Length != ids.Length)
            throw new ArgumentException("records and ids must have the same length.", nameof(ids));

        NormalizedRecord[] records = new NormalizedRecord[normalizedRecords.Length];

        for (int i = 0; i < normalizedRecords.Length; i++)
            records[i] = new NormalizedRecord(ids[i], normalizedRecords[i]);

        Build(records, out _bucket8, out _bucket16, out _bucket32);
    }

    /// <summary>
    /// Searches for candidates within a bounded Levenshtein edit distance.
    /// </summary>
    /// <param name="normalizedQuery">The query key, already normalized (ASCII letters/digits only).</param>
    /// <param name="results">Destination buffer that receives matches.</param>
    /// <param name="options">Search parameters such as maximum edits and minimum score.</param>
    /// <returns>The number of results written to <paramref name="results"/>.</returns>
    /// <remarks>
    /// This method does not allocate. If <paramref name="results"/> is too small, the method
    /// returns early once the buffer is full.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="options"/> contains invalid values.
    /// </exception>
    public int Search(ReadOnlySpan<byte> normalizedQuery, Span<MatchResult> results, SearchOptions options = default)
    {
        int maxEdits = options.MaxEdits;

        ValidateOptions(options);
        if (normalizedQuery.Length is 0 or > MaxKeyLength) return 0;

        int written = 0;
        if (normalizedQuery.Length < 8 + 1 + maxEdits)
            SearchBucket(_bucket8, normalizedQuery, results, options, ref written);
        if (normalizedQuery.Length < 16 + 1 + maxEdits && normalizedQuery.Length > 8  - maxEdits)
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
        float minRatio = options.MinRatio;

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
            if (score < minRatio) continue;

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

    private static void ValidateOptions(SearchOptions options)
    {
        if (options.MaxEdits < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxEdits cannot be negative.");

        if (options.MaxEdits > MaxKeyLength)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxEdits is too large.");

        if (float.IsNaN(options.MinRatio) || options.MinRatio < 0f || options.MinRatio > 1f)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRatio must be between 0 and 1.");
    }
}

/// <summary>
/// Static class with helper methods
/// </summary>
public static class FBLHelper
{
    /// <summary>
    /// Normalizes a name into a compact ASCII byte representation suitable for <see cref="FBLEngine"/>.
    /// </summary>
    /// <param name="input">Input characters to normalize.</param>
    /// <param name="destination">
    /// Destination buffer that receives normalized bytes (lowercase letters a-z and digits 0-9).
    /// </param>
    /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
    /// <remarks>
    /// Normalization behavior:
    /// <list type="bullet">
    /// <item><description>Letters are lower-cased (ASCII only).</description></item>
    /// <item><description>Only letters and digits are emitted; all other characters are skipped.</description></item>
    /// <item><description>For non-ASCII input, the method uses FormD decomposition and drops non-spacing marks.</description></item>
    /// <item><description>Output is truncated if <paramref name="destination"/> is too small.</description></item>
    /// </list>
    /// </remarks>
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

    /// <summary>
    /// Normalizes a query string into a newly allocated byte array.
    /// </summary>
    /// <param name="input">The input string to normalize.</param>
    /// <returns>
    /// A new byte array containing the normalized query. The returned array length equals the number of
    /// bytes written (0..<see cref="FBLEngine.MaxKeyLength"/>).
    /// </returns>
    /// <remarks>
    /// This helper allocates. For allocation-free usage, prefer calling
    /// <see cref="Normalize(ReadOnlySpan{char}, Span{byte})"/> with a caller-provided buffer.
    /// </remarks>
    public static byte[] NormalizeQuery(ReadOnlySpan<char> input)
    {
        byte[] buffer = new byte[FBLEngine.MaxKeyLength];
        int length = Normalize(input, buffer);
        return buffer.AsSpan(0, length).ToArray();
    }

    /// <summary>
    /// Normalizes an array of strings into an array of normalized byte keys.
    /// </summary>
    /// <param name="names">Input strings to normalize.</param>
    /// <returns>
    /// An array of normalized keys suitable for constructing <see cref="FBLEngine"/>.
    /// Each element is a newly allocated array containing the normalized bytes for the corresponding input.
    /// </returns>
    /// <remarks>
    /// This helper allocates a new array per input string. It is intended for convenience when initially
    /// preparing a dataset.
    /// </remarks>
    public static ReadOnlyMemory<byte>[] NormalizeStrings(string[] names)
    {
        ReadOnlyMemory<byte>[] normalizedRecords = new ReadOnlyMemory<byte>[names.Length];
        Span<byte> buffer = stackalloc byte[32];

        for (int i = 0; i < names.Length; i++)
        {
            int len = Normalize(names[i].AsSpan(), buffer);
            normalizedRecords[i] = buffer[..len].ToArray();
        }
        return normalizedRecords;
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

/// <summary>
/// Options controlling the fuzzy-search behavior.
/// </summary>
/// <param name="MaxEdits">Maximum allowed Levenshtein edit distance.</param>
/// <param name="MinRatio">
/// Minimum acceptable similarity score in the range 0..1.
/// The score is computed as <c>1 - (distance / max(queryLength, candidateLength))</c>.
/// </param>
public readonly record struct SearchOptions(int MaxEdits = 2, float MinRatio = 0.75f);

/// <summary>
/// A pre-normalized record stored in the engine.
/// </summary>
/// <param name="Id">Application-defined identifier associated with the key.</param>
/// <param name="Key">Normalized key bytes.</param>
public readonly record struct NormalizedRecord(int Id, ReadOnlyMemory<byte> Key);

/// <summary>
/// A single match returned from <see cref="FBLEngine.Search"/>.
/// </summary>
/// <param name="CandidateId">The identifier of the matched candidate.</param>
/// <param name="EditDistance">Levenshtein distance between query and candidate.</param>
/// <param name="Score">Similarity score in the range 0..1 (higher is better).</param>
public readonly record struct MatchResult(int CandidateId, int EditDistance, float Score);