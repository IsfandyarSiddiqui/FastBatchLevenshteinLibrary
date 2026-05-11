namespace FastBatchLevenshtein.Tests;

public class UnitTest1
{
    [Fact]
    public void Normalize_AsciiLowercasesAndDropsNonAlphaNumeric()
    {
        Span<byte> buffer = stackalloc byte[FBLEngine.MaxKeyLength];
        int len = FBLHelper.Normalize("John.Smith-123".AsSpan(), buffer);
        Assert.Equal("johnsmith123"u8.ToArray(), buffer[..len].ToArray());
    }

    [Fact]
    public void Normalize_NonAsciiStripsDiacritics()
    {
        Span<byte> buffer = stackalloc byte[FBLEngine.MaxKeyLength];
        int len = FBLHelper.Normalize("Jöhn Smíth".AsSpan(), buffer);
        Assert.Equal("johnsmith"u8.ToArray(), buffer[..len].ToArray());
    }

    [Fact]
    public void Normalize_TruncatesWhenDestinationTooSmall()
    {
        Span<byte> buffer = stackalloc byte[4];
        int len = FBLHelper.Normalize("abcdef".AsSpan(), buffer);

        Assert.Equal(4, len);
        Assert.Equal("abcd"u8.ToArray(), buffer[..len].ToArray());
    }

    [Fact]
    public void Normalize_NonAsciiFallbackHonorsDestinationLimit()
    {
        Span<byte> buffer = stackalloc byte[5];
        int len = FBLHelper.Normalize("ÀBCDÉFG".AsSpan(), buffer);

        Assert.Equal(5, len);
        Assert.Equal("abcde"u8.ToArray(), buffer[..len].ToArray());
    }

    [Fact]
    public void Normalize_DropsNonAsciiCharactersThatDoNotDecomposeToAscii()
    {
        Span<byte> buffer = stackalloc byte[FBLEngine.MaxKeyLength];
        int len = FBLHelper.Normalize("東京-ABC-123".AsSpan(), buffer);

        Assert.Equal("abc123"u8.ToArray(), buffer[..len].ToArray());
    }

    [Fact]
    public void NormalizeQuery_ReturnsTightlySizedNormalizedBytes()
    {
        byte[] actual = FBLHelper.NormalizeQuery(" A.B C! ".AsSpan());

        Assert.Equal("abc"u8.ToArray(), actual);
    }

    [Fact]
    public void NormalizeStrings_NormalizesEachInputIndependently()
    {
        ReadOnlyMemory<byte>[] actual = FBLHelper.NormalizeStrings(["ALPHA!", "Béta 2", "***"]);

        Assert.Equal("alpha"u8.ToArray(), actual[0].ToArray());
        Assert.Equal("beta2"u8.ToArray(), actual[1].ToArray());
        Assert.Empty(actual[2].ToArray());
    }

    [Fact]
    public void Constructor_ThrowsWhenIdsLengthDoesNotMatchRecordsLength()
    {
        ReadOnlyMemory<byte>[] keys = [Bytes("one"), Bytes("two")];

        Assert.Throws<ArgumentException>(() => new FBLEngine(keys, ids: [1]));
    }

    [Fact]
    public void Constructor_AssignsSequentialIdsWhenIdsAreNotProvided()
    {
        var engine = new FBLEngine([Bytes("alpha"), Bytes("bravo")]);

        MatchResult match = Assert.Single(Search(engine, "bravo", new SearchOptions(MaxEdits: 0, MinRatio: 1.0f)));

        Assert.Equal(1, match.CandidateId);
        Assert.Equal(0, match.EditDistance);
        Assert.Equal(1.0f, match.Score);
    }

    [Fact]
    public void Constructor_IgnoresEmptyAndTooLongKeys()
    {
        var engine = new FBLEngine([
            new NormalizedRecord(1, Bytes("valid")),
            new NormalizedRecord(2, ReadOnlyMemory<byte>.Empty),
            new NormalizedRecord(3, RawBytes(new string('a', FBLEngine.MaxKeyLength + 1)))
        ]);

        MatchResult[] validMatches = Search(engine, "valid", new SearchOptions(MaxEdits: 0, MinRatio: 1.0f));
        MatchResult[] emptyMatches = Search(engine, string.Empty, new SearchOptions(MaxEdits: 0, MinRatio: 0.0f));
        MatchResult[] tooLongMatches = SearchRaw(engine, RawBytes(new string('a', FBLEngine.MaxKeyLength + 1)), new SearchOptions(MaxEdits: FBLEngine.MaxKeyLength, MinRatio: 0.0f));

        Assert.Equal(1, Assert.Single(validMatches).CandidateId);
        Assert.Empty(emptyMatches);
        Assert.Empty(tooLongMatches);
    }

    [Fact]
    public void Search_ReturnsExpectedMatchWithinMaxEdits()
    {
        ReadOnlyMemory<byte>[] keys = FBLHelper.NormalizeStrings(["john smith", "jane doe", "jon smyth"]);
        var engine = new FBLEngine(keys, ids: [10, 20, 30]);

        MatchResult[] matches = Search(engine, "john smit", new SearchOptions(MaxEdits: 2, MinRatio: 0.75f));

        Assert.Contains(matches, r => r.CandidateId == 10);
    }

    [Fact]
    public void Search_RespectsMinRatioFilter()
    {
        ReadOnlyMemory<byte>[] keys = FBLHelper.NormalizeStrings(["john smith", "jon smyth", "zzzzzzzz"]);
        var engine = new FBLEngine(keys);

        MatchResult[] matches = Search(engine, "john smith", new SearchOptions(MaxEdits: 2, MinRatio: 1.0f));

        Assert.NotEmpty(matches);
        Assert.All(matches, r => Assert.Equal(0, r.EditDistance));
    }

    [Theory]
    [InlineData("abcdefgh", 8)]
    [InlineData("abcdefghi", 9)]
    [InlineData("abcdefghijklmnop", 16)]
    [InlineData("abcdefghijklmnopq", 17)]
    [InlineData("abcdefghijklmnopqrstuvwxyz123456", 32)]
    public void Search_FindsExactMatchesAtBucketBoundaryLengths(string key, int expectedLength)
    {
        Assert.Equal(expectedLength, key.Length);
        var engine = new FBLEngine([Bytes(key)], ids: [expectedLength]);

        MatchResult match = Assert.Single(Search(engine, key, new SearchOptions(MaxEdits: 0, MinRatio: 1.0f)));

        Assert.Equal(expectedLength, match.CandidateId);
        Assert.Equal(0, match.EditDistance);
        Assert.Equal(1.0f, match.Score);
    }

    [Fact]
    public void Search_UsesCustomIdsWithNormalizedRecordConstructor()
    {
        var engine = new FBLEngine([
            new NormalizedRecord(101, Bytes("alpha")),
            new NormalizedRecord(202, Bytes("bravo"))
        ]);

        MatchResult match = Assert.Single(Search(engine, "alpha", new SearchOptions(MaxEdits: 0, MinRatio: 1.0f)));

        Assert.Equal(101, match.CandidateId);
    }

    [Theory]
    [InlineData("abcdef", "abcxef", 1)]
    [InlineData("abcdef", "abcde", 1)]
    [InlineData("abcdef", "abcdefg", 1)]
    [InlineData("abcdef", "abcfde", 2)]
    public void Search_ComputesExpectedEditDistances(string candidate, string query, int expectedDistance)
    {
        var engine = new FBLEngine([Bytes(candidate)], ids: [42]);

        MatchResult match = Assert.Single(Search(engine, query, new SearchOptions(MaxEdits: expectedDistance, MinRatio: 0.0f)));

        Assert.Equal(42, match.CandidateId);
        Assert.Equal(expectedDistance, match.EditDistance);
        Assert.Equal(1.0f - ((float)expectedDistance / Math.Max(candidate.Length, query.Length)), match.Score, precision: 6);
    }

    [Fact]
    public void Search_DoesNotReturnCandidatesOutsideMaxEditsEvenWhenMinRatioWouldAllowThem()
    {
        var engine = new FBLEngine([Bytes("abcdef")]);

        MatchResult[] matches = Search(engine, "abcxyz", new SearchOptions(MaxEdits: 2, MinRatio: 0.0f));

        Assert.Empty(matches);
    }

    [Fact]
    public void Search_SkipsCandidatesWhoseLengthDifferenceExceedsMaxEdits()
    {
        var engine = new FBLEngine([Bytes("abcdef")]);

        MatchResult[] matches = Search(engine, "abc", new SearchOptions(MaxEdits: 2, MinRatio: 0.0f));

        Assert.Empty(matches);
    }

    [Fact]
    public void Search_StopsWhenResultBufferIsFull()
    {
        var engine = new FBLEngine([Bytes("alpha"), Bytes("alpha"), Bytes("alpha")], ids: [10, 20, 30]);
        Span<MatchResult> results = stackalloc MatchResult[1];

        int count = engine.Search(Bytes("alpha").Span, results, new SearchOptions(MaxEdits: 0, MinRatio: 1.0f));

        Assert.Equal(1, count);
        Assert.Equal(10, results[0].CandidateId);
    }

    [Fact]
    public void Search_WithEmptyResultBufferReturnsZeroWithoutThrowing()
    {
        var engine = new FBLEngine([Bytes("alpha")]);
        Span<MatchResult> results = Span<MatchResult>.Empty;

        int count = engine.Search(Bytes("alpha").Span, results, new SearchOptions(MaxEdits: 0, MinRatio: 1.0f));

        Assert.Equal(0, count);
    }

    [Fact]
    public void Search_ReturnsZeroForEmptyOrTooLongQuery()
    {
        var engine = new FBLEngine([Bytes("alpha")]);

        Assert.Empty(Search(engine, string.Empty, new SearchOptions(MaxEdits: 0, MinRatio: 0.0f)));
        Assert.Empty(SearchRaw(engine, RawBytes(new string('a', FBLEngine.MaxKeyLength + 1)), new SearchOptions(MaxEdits: FBLEngine.MaxKeyLength, MinRatio: 0.0f)));
    }

    [Theory]
    [InlineData(-1, 0.75f)]
    [InlineData(FBLEngine.MaxKeyLength + 1, 0.75f)]
    [InlineData(2, -0.01f)]
    [InlineData(2, 1.01f)]
    public void Search_ThrowsForInvalidOptions(int maxEdits, float minRatio)
    {
        var engine = new FBLEngine([Bytes("alpha")]);
        MatchResult[] results = new MatchResult[4];
        ReadOnlyMemory<byte> query = Bytes("alpha");

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Search(query.Span, results, new SearchOptions(maxEdits, minRatio)));
    }

    [Fact]
    public void Search_ThrowsForNaNMinRatio()
    {
        var engine = new FBLEngine([Bytes("alpha")]);
        MatchResult[] results = new MatchResult[4];
        ReadOnlyMemory<byte> query = Bytes("alpha");

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Search(query.Span, results, new SearchOptions(MaxEdits: 1, MinRatio: float.NaN)));
    }

    private static MatchResult[] Search(FBLEngine engine, string query, SearchOptions options)
    {
        Span<byte> buffer = stackalloc byte[FBLEngine.MaxKeyLength];
        int length = FBLHelper.Normalize(query.AsSpan(), buffer);
        Span<MatchResult> results = stackalloc MatchResult[32];
        int count = engine.Search(buffer[..length], results, options);
        return results[..count].ToArray();
    }

    private static MatchResult[] SearchRaw(FBLEngine engine, ReadOnlyMemory<byte> query, SearchOptions options)
    {
        Span<MatchResult> results = stackalloc MatchResult[32];
        int count = engine.Search(query.Span, results, options);
        return results[..count].ToArray();
    }

    private static ReadOnlyMemory<byte> Bytes(string value) => FBLHelper.NormalizeQuery(value.AsSpan());

    private static ReadOnlyMemory<byte> RawBytes(string value) => System.Text.Encoding.ASCII.GetBytes(value);
}