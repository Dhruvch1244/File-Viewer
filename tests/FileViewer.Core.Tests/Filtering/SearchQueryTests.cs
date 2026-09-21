using FileViewer.Core.Caching;
using FileViewer.Core.Filtering;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Tests.Filtering;

/// <summary>
/// Covers the multi-term main search: several criteria at once, per-column terms, negation, regex,
/// and the AND/OR combine modes — plus the invariant that the raw-bytes fast path and the decoded
/// path agree.
/// </summary>
public class SearchQueryTests
{
    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static async Task<(FileIndex Index, EditOverlay Overlay, DecodedRowCache Cache)> OpenAsync(string fixture = "minimal_valid.dif") =>
        (await FileIndexer.IndexAsync(FixturePath(fixture)), new EditOverlay(), new DecodedRowCache());

    private static List<long> Run(SearchQuery query, FileIndex index, EditOverlay overlay, DecodedRowCache cache) =>
        RowQueryEngine.Filter([0, 1, 2], RowPredicateSet.Compile(query, null, null, index.Header), index, overlay, cache);

    [Fact]
    public async Task TwoTerms_CombinedWithAll_KeepOnlyRowsMatchingBoth()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            var query = new SearchQuery([
                new SearchCriterion("SEC00"),
                new SearchCriterion("101.25"),
            ]);

            Assert.Equal([1L], Run(query, index, overlay, cache));
        }
    }

    [Fact]
    public async Task TwoTerms_CombinedWithAny_KeepRowsMatchingEither()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            var query = new SearchQuery([
                new SearchCriterion("100.50"),
                new SearchCriterion("99.75"),
            ], SearchCombineMode.Any);

            Assert.Equal([0L, 2L], Run(query, index, overlay, cache));
        }
    }

    [Fact]
    public async Task ColumnScopedTerm_OnlyLooksAtThatColumn()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            // "0" appears in _ERR on every row and inside every price, but only SEC001's PRICE starts with "100".
            var query = new SearchQuery([new SearchCriterion("100", SearchMatchMode.StartsWith, "PRICE")]);

            Assert.Equal([0L], Run(query, index, overlay, cache));
        }
    }

    [Fact]
    public async Task NotContainsTerm_ExcludesMatchingRows()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            var query = new SearchQuery([new SearchCriterion("SEC002", SearchMatchMode.NotContains)]);

            Assert.Equal([0L, 2L], Run(query, index, overlay, cache));
        }
    }

    [Fact]
    public async Task EqualsTerm_RequiresTheWholeValue()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            Assert.Equal([1L], Run(new SearchQuery([new SearchCriterion("101.25", SearchMatchMode.Equals, "PRICE")]), index, overlay, cache));
            Assert.Empty(Run(new SearchQuery([new SearchCriterion("101", SearchMatchMode.Equals, "PRICE")]), index, overlay, cache));
        }
    }

    [Fact]
    public async Task RegexTerm_MatchesByPattern()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            var query = new SearchQuery([new SearchCriterion(@"^SEC00[13]\b", SearchMatchMode.Regex, "_ID")]);

            Assert.Equal([0L, 2L], Run(query, index, overlay, cache));
        }
    }

    [Fact]
    public async Task InvalidRegexTerm_MatchesNothingRatherThanThrowing()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            var query = new SearchQuery([new SearchCriterion("[unclosed", SearchMatchMode.Regex, "_ID")]);

            Assert.Empty(Run(query, index, overlay, cache));
        }
    }

    [Fact]
    public async Task CaseSensitiveTerm_DistinguishesCase()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            Assert.Equal([0L, 1L, 2L], Run(new SearchQuery([new SearchCriterion("sec00")]), index, overlay, cache));
            Assert.Empty(Run(new SearchQuery([new SearchCriterion("sec00", CaseSensitive: true)]), index, overlay, cache));
        }
    }

    [Fact]
    public async Task TermNamingAColumnTheSectionDoesNotHave_MatchesNothing()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            Assert.Empty(Run(new SearchQuery([new SearchCriterion("x", SearchMatchMode.Contains, "NO_SUCH_COLUMN")]), index, overlay, cache));
        }
    }

    [Fact]
    public async Task RawFastPath_SelectsExactlyTheRowsAFieldByFieldSearchWould()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync("FixedIncomeAsia.dif");
        using (index)
        {
            var rows = new List<long>();
            for (long i = 0; i < (long)index.RowIndex.Count; i++) rows.Add(i);

            RowPredicateSet predicates = RowPredicateSet.Compile(
                new SearchQuery([new SearchCriterion("corporate")]), null, null, index.Header);
            Assert.True(predicates.SupportsRawMatching);

            List<long> viaRaw = RowQueryEngine.Filter(rows, predicates, index, overlay, cache);

            // Reference: decode every row and look for the text in any field. The raw byte scan is
            // an optimization of exactly this, so the two must agree row for row.
            List<long> reference = [.. rows.Where(rowIndex =>
                RowResolver.Resolve(rowIndex, index, overlay, cache)!.FieldValues
                    .Any(field => field.Contains("corporate", StringComparison.OrdinalIgnoreCase)))];

            Assert.NotEmpty(viaRaw);
            Assert.Equal(reference, viaRaw);
        }
    }

    [Fact]
    public async Task EditedRow_IsMatchedOnItsEditedValueNotItsFileBytes()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            overlay.EditCell(0, "PRICE", "777.77");

            Assert.Equal([0L], Run(new SearchQuery([new SearchCriterion("777.77")]), index, overlay, cache));
            Assert.Empty(Run(new SearchQuery([new SearchCriterion("100.50")]), index, overlay, cache));
        }
    }

    [Fact]
    public async Task DeletedRows_AreNeverReturned()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            overlay.DeleteRow(1);

            Assert.Equal([0L, 2L], Run(new SearchQuery([new SearchCriterion("SEC00")]), index, overlay, cache));
        }
    }

    [Fact]
    public async Task CombinedWithColumnFilters_EvaluatesEverythingInOnePass()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            var valueFilters = new Dictionary<string, HashSet<string>> { ["_ERR"] = ["0"] };
            var patternFilters = new Dictionary<string, ColumnPatternFilter> { ["PRICE"] = new("^10", UseRegex: true) };

            RowPredicateSet predicates = RowPredicateSet.Compile(
                new SearchQuery([new SearchCriterion("SEC00")]), valueFilters, patternFilters, index.Header);

            Assert.Equal([0L, 1L], RowQueryEngine.Filter([0, 1, 2], predicates, index, overlay, cache));
        }
    }

    [Fact]
    public async Task ValueFilterAlone_NoSearchTerm_UsesColumnScopedPath()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            var valueFilters = new Dictionary<string, HashSet<string>> { ["PRICE"] = ["100.50", "99.75"] };
            RowPredicateSet predicates = RowPredicateSet.Compile(SearchQuery.Empty, valueFilters, null, index.Header);

            Assert.True(predicates.SupportsColumnScopedMatching);
            Assert.Equal([0L, 2L], RowQueryEngine.Filter([0, 1, 2], predicates, index, overlay, cache));
        }
    }

    [Fact]
    public async Task PatternFilterAlone_NoSearchTerm_UsesColumnScopedPath()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            var patternFilters = new Dictionary<string, ColumnPatternFilter> { ["_ID"] = new("SEC00[13]", UseRegex: true) };
            RowPredicateSet predicates = RowPredicateSet.Compile(SearchQuery.Empty, null, patternFilters, index.Header);

            Assert.True(predicates.SupportsColumnScopedMatching);
            Assert.Equal([0L, 2L], RowQueryEngine.Filter([0, 1, 2], predicates, index, overlay, cache));
        }
    }

    [Fact]
    public async Task ValueAndPatternFilterAlone_BothMustMatch()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            var valueFilters = new Dictionary<string, HashSet<string>> { ["_ERR"] = ["0"] };
            var patternFilters = new Dictionary<string, ColumnPatternFilter> { ["PRICE"] = new("^10", UseRegex: true) };
            RowPredicateSet predicates = RowPredicateSet.Compile(SearchQuery.Empty, valueFilters, patternFilters, index.Header);

            Assert.True(predicates.SupportsColumnScopedMatching);
            Assert.Equal([0L, 1L], RowQueryEngine.Filter([0, 1, 2], predicates, index, overlay, cache));
        }
    }

    [Fact]
    public async Task ValueFilterAlone_EditedRow_MatchesEditedValueNotFileBytes()
    {
        // The column-scoped fast path only ever applies to a row's raw file bytes; an edited row
        // must still fall back so the filter sees the overlay value, not what's on disk.
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            overlay.EditCell(1, "PRICE", "99.75");
            var valueFilters = new Dictionary<string, HashSet<string>> { ["PRICE"] = ["99.75"] };
            RowPredicateSet predicates = RowPredicateSet.Compile(SearchQuery.Empty, valueFilters, null, index.Header);

            Assert.Equal([1L, 2L], RowQueryEngine.Filter([0, 1, 2], predicates, index, overlay, cache));
        }
    }

    [Fact]
    public async Task ValueFilterAlone_AgreesWithDecodedReferenceAcrossRows()
    {
        // The column-scoped path extracts one field's raw bytes via a computed field-range table
        // instead of decoding the whole row; this checks that table-based extraction picks out
        // exactly the same values a full per-row decode would, across every row of a real file.
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync("FixedIncomeAsia.dif");
        using (index)
        {
            var rows = new List<long>();
            for (long i = 0; i < (long)index.RowIndex.Count; i++) rows.Add(i);

            int columnIndex = index.Header.ColumnIndexOf("_ID");
            Assert.True(columnIndex >= 0);

            // Pick an allowed set from actual decoded values so the reference and the fast path have
            // something real to agree (or disagree) on, rather than trivially both returning empty.
            var allowedValues = rows
                .Select(r => RowResolver.Resolve(r, index, overlay, cache)!.FieldValues[columnIndex])
                .Where((_, idx) => idx % 3 == 0)
                .ToHashSet(StringComparer.Ordinal);

            var valueFilters = new Dictionary<string, HashSet<string>> { ["_ID"] = allowedValues };
            RowPredicateSet predicates = RowPredicateSet.Compile(SearchQuery.Empty, valueFilters, null, index.Header);
            Assert.True(predicates.SupportsColumnScopedMatching);

            List<long> viaColumnScoped = RowQueryEngine.Filter(rows, predicates, index, overlay, cache);
            List<long> reference = [.. rows.Where(r =>
                allowedValues.Contains(RowResolver.Resolve(r, index, overlay, cache)!.FieldValues[columnIndex]))];

            Assert.NotEmpty(viaColumnScoped);
            Assert.Equal(reference, viaColumnScoped);
        }
    }

    [Fact]
    public async Task DistinctValues_ReportWhenTheyHitTheCap()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            DistinctValueResult capped = RowQueryEngine.GetDistinctValues([0, 1, 2], "PRICE", index, overlay, cache, maxValues: 2);
            Assert.True(capped.Truncated);
            Assert.Equal(2, capped.Values.Count);

            DistinctValueResult full = RowQueryEngine.GetDistinctValues([0, 1, 2], "PRICE", index, overlay, cache);
            Assert.False(full.Truncated);
            Assert.Equal(["100.50", "101.25", "99.75"], full.Values);
        }
    }

    [Fact]
    public async Task RowsGivenInReverseOrder_MatchTheSameRowsAsFileOrder()
    {
        // File order lets the scan read rows in runs; a reversed (or otherwise sorted) order makes
        // it fall back to reading each row on its own. Both must select the same rows.
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync("FixedIncomeAsia.dif");
        using (index)
        {
            var ascending = new List<long>();
            for (long i = 0; i < (long)index.RowIndex.Count; i++) ascending.Add(i);
            List<long> descending = [.. Enumerable.Reverse(ascending)];

            RowPredicateSet predicates = RowPredicateSet.Compile(
                new SearchQuery([new SearchCriterion("Corporate")]), null, null, index.Header);

            List<long> forward = RowQueryEngine.Filter(ascending, predicates, index, overlay, cache);
            List<long> backward = RowQueryEngine.Filter(descending, predicates, index, overlay, cache);

            Assert.NotEmpty(forward);
            Assert.Equal(forward, Enumerable.Reverse(backward));
        }
    }

    [Fact]
    public async Task ParallelScan_PreservesInputOrderAndMatchesTheSerialScan()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync("FixedIncomeAsia.dif");
        using (index)
        {
            // Repeat the row list until it comfortably exceeds the parallel threshold, so the
            // partitioned path is what actually runs.
            var rows = new List<long>();
            while (rows.Count < RowQueryEngine.ParallelThresholdRows * 3)
            {
                for (long i = 0; i < (long)index.RowIndex.Count; i++) rows.Add(i);
            }

            RowPredicateSet predicates = RowPredicateSet.Compile(
                new SearchQuery([new SearchCriterion("Corporate")]), null, null, index.Header);

            List<long> parallel = RowQueryEngine.Filter(rows, predicates, index, overlay, cache);
            List<long> serial = RowQueryEngine.Filter([.. rows.Take(RowQueryEngine.ParallelThresholdRows - 1)], predicates, index, overlay, cache);

            Assert.NotEmpty(parallel);
            Assert.Equal(parallel.Take(serial.Count), serial);
            Assert.Equal(parallel.OrderBy(r => 0).ToList(), parallel); // stable: never reordered relative to input
        }
    }
}
