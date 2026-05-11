# FastBatchLevenshtein

FastBatchLevenshtein is a small, allocation-free fuzzy matching engine optimized for batch searching over many short keys using a bounded Levenshtein distance.

It is designed around a common workflow:

1. Normalize many names/keys once (to a compact ASCII representation)
2. Build an in-memory search engine over those normalized keys
3. Repeatedly search with normalized queries using a maximum edit distance and a minimum similarity threshold

The API is intentionally low-level to avoid allocations and to be fast.

## Targets

This library targets modern .NET runtimes (e.g. .NET 8 / .NET 9 / .NET 10).

## Core concepts

### Normalized keys

The engine searches over normalized keys represented as bytes (`ReadOnlyMemory<byte>` / `ReadOnlySpan<byte>`). The library includes helpers:

- `FBLHelper.Normalize(ReadOnlySpan<char> input, Span<byte> destination)`
- `FBLHelper.NormalizeQuery(string input)`
- `FBLHelper.NormalizeStringes(string[] names)`

The normalizer emits only:

- ASCII letters `a-z` (lowercased)
- digits `0-9`

Everything else is skipped.

For non-ASCII input it uses Unicode FormD decomposition and drops non-spacing marks (accents/diacritics), then applies the same ASCII-only filter.

### Buckets

Internally, the engine stores keys in three fixed-width buckets by length:

- bucket width 8 (keys of length 1..8)
- bucket width 16 (keys of length 9..16)
- bucket width 32 (keys of length 17..32)

This keeps candidate access fast and predictable.

### Similarity score

Each match includes both:

- Levenshtein edit distance (`EditDistance`)
- a similarity score (`Score`) in range 0..1

Score is computed as:

`1 - (distance / max(queryLength, candidateLength))`

The `MinRatio` option filters results by this score.

## Quick start

### 1) Normalize keys and build an engine

Below is an allocation-friendly pattern: use a stack buffer to normalize, then persist exactly the written bytes for storage.

```csharp
using FastBatchLevenshtein;

string[] names =
[
    "John Smith",
    "Jon Smyth",
    "Jane Doe",
];

ReadOnlyMemory<byte>[] keys = new ReadOnlyMemory<byte>[names.Length];
Span<byte> tmp = stackalloc byte[FBLEngine.MaxKeyLength];

for (int i = 0; i < names.Length; i++)
{
    int written = FBLHelper.Normalize(names[i].AsSpan(), tmp);
    keys[i] = tmp[..written].ToArray();
}

// Build engine with implicit ids (0..N-1).
var engine = new FBLEngine(keys);
```

If you prefer convenience (allocates), you can normalize the whole dataset in one call:

```csharp
ReadOnlyMemory<byte>[] keys = FBLHelper.NormalizeStringes(names);
var engine = new FBLEngine(keys);
```

### 2) Search

Normalize the query into a temporary buffer, then search into a caller-provided results buffer:

```csharp
Span<byte> qTmp = stackalloc byte[FBLEngine.MaxKeyLength];
int qLen = FBLHelper.Normalize("john smit".AsSpan(), qTmp);
ReadOnlySpan<byte> query = qTmp[..qLen];

Span<MatchResult> results = stackalloc MatchResult[16];
int count = engine.Search(query, results, new SearchOptions(MaxEdits: 2, MinRatio: 0.75f));

foreach (MatchResult r in results[..count])
    Console.WriteLine($"Id={r.CandidateId} dist={r.EditDistance} score={r.Score:0.000}");
```

If you prefer convenience for queries, you can use:

```csharp
byte[] query = FBLHelper.NormalizeQuery("john smit");
int count = engine.Search(query, results);
```

## Working with ids

If you want candidate ids to map to your own identifiers, pass them when constructing the engine:

```csharp
int[] ids = [ 1001, 1002, 1003 ];
var engine = new FBLEngine(keys, ids);
```

Or build from `NormalizedRecord`:

```csharp
NormalizedRecord[] records =
[
    new(1001, keys[0]),
    new(1002, keys[1]),
    new(1003, keys[2]),
];

var engine = new FBLEngine(records);
```

## Convenience helpers

`FBLHelper` includes allocating convenience helpers for common workflows:

### NormalizeQuery

Use when you want a normalized query as a freshly allocated byte array:

```csharp
byte[] query = FBLHelper.NormalizeQuery("john smit");
```

### NormalizeStringes

Use when you want to normalize a whole dataset of strings into keys suitable for constructing an engine:

```csharp
string[] names = [ "John Smith", "Jon Smyth", "Jane Doe" ];
ReadOnlyMemory<byte>[] keys = FBLHelper.NormalizeStringes(names);
var engine = new FBLEngine(keys);
```

## Important assumptions and constraints

This library makes several assumptions to stay fast and allocation-free.

### 1) Keys are already normalized

`FBLEngine` does not normalize strings. You must provide normalized byte keys.

You can use `FBLNameNormalizer` or implement your own normalization, but the engine assumes:

- the data you search is the same representation as the query (same normalization rules)

### 2) Maximum key length is 32 bytes

- `FBLEngine.MaxKeyLength` is 32.
- Empty keys are ignored.
- Keys longer than 32 bytes are ignored.
- Queries longer than 32 bytes return 0 matches.

This limit is also baked into the internal bounded Levenshtein implementation (it uses stackalloc buffers sized to `MaxKeyLength + 1`).

### 3) ASCII letters/digits only (recommended)

The provided `FBLNameNormalizer` emits only ASCII letters and digits.

Non-ASCII is handled by stripping diacritics and discarding non-ASCII characters. This means some scripts/characters may be removed entirely.

If your domain requires non-ASCII matching, you likely need a different normalization strategy (and potentially a different engine design).

### 4) Output buffers limit results

`Search` writes into a caller-provided `Span<MatchResult>`.

- If the span is too small, search stops early when it fills.
- Results are returned in the engine's internal iteration order (not sorted).

If you need sorting (e.g., by score or distance), sort the returned slice yourself:

```csharp
var slice = results[..count];
slice.Sort((a, b) => b.Score.CompareTo(a.Score));
```

### 5) Thread safety

`FBLEngine` is immutable after construction. Concurrent calls to `Search` are safe.

### 6) Memory / lifetime

When you build an engine from `ReadOnlyMemory<byte>[]`, it copies keys into internal arrays. Your input arrays do not need to remain alive after construction.

## Tuning search behavior

`SearchOptions`:

- `MaxEdits` (default: 2)
  - maximum allowed edit distance
  - larger values increase work and may reduce relevance

- `MinRatio` (default: 0.75)
  - filters by similarity score (0..1)
  - raise to reduce results, lower to include more

## FAQ

### Why bytes instead of strings?

Using bytes allows normalization to produce a compact representation and lets the engine avoid per-search allocations. `Span<T>` APIs also make it easy to integrate with high-performance code.

### Does it handle transpositions (Damerau-Levenshtein)?

No. The distance is standard Levenshtein (insert/delete/replace).

## License

See repository license information.
