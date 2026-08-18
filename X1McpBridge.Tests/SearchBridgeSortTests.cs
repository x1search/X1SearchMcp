// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using X1.Service;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// Unit tests for the bridge-side sort enforcement that corrects X1's habit of pinning a
    /// "current" item to the top of a result window regardless of the requested sort order.
    /// </summary>
    [TestFixture]
    public class SearchBridgeSortTests
    {
        private static SearchResult Result(string uri, string dateValue)
        {
            return new SearchResult { uri = uri, table = "MSMail", keywords = null, fields = new[] { dateValue } };
        }

        private static Column[] DateCols => new[] { new Column("", "date") };

        private static SortColumn[] DateSort(SortDirection dir)
        {
            return new[] { new SortColumn { table = "", name = "date", direction = dir } };
        }

        [Test]
        public void ApplySortClientSide_NumericDescending_OrdersHighToLow()
        {
            // The pinned out-of-order "44250" should sink to the bottom.
            var all = new List<SearchResult>
            {
                Result("a", "44250.00"),
                Result("b", "45552.33"),
                Result("c", "45393.96"),
            };
            SearchBridge.ApplySortClientSide(all, DateSort(SortDirection.Backwards), DateCols);
            Assert.That(all.Select(r => r.uri), Is.EqualTo(new[] { "b", "c", "a" }));
        }

        [Test]
        public void ApplySortClientSide_NumericAscending_OrdersLowToHigh()
        {
            var all = new List<SearchResult>
            {
                Result("b", "45552.33"),
                Result("a", "44250.00"),
                Result("c", "45393.96"),
            };
            SearchBridge.ApplySortClientSide(all, DateSort(SortDirection.Forwards), DateCols);
            Assert.That(all.Select(r => r.uri), Is.EqualTo(new[] { "a", "c", "b" }));
        }

        [Test]
        public void ApplySortClientSide_ColumnNotFetched_LeavesOrderUntouched()
        {
            var all = new List<SearchResult>
            {
                Result("a", "44250.00"),
                Result("b", "45552.33"),
            };
            // Sort by a column that isn't in the display layout -> can't compare -> no reorder.
            var sort = new[] { new SortColumn { table = "", name = "subject", direction = SortDirection.Backwards } };
            SearchBridge.ApplySortClientSide(all, sort, DateCols);
            Assert.That(all.Select(r => r.uri), Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void ApplySortClientSide_NonNumeric_FallsBackToLexical()
        {
            var cols = new[] { new Column("", "subject") };
            var all = new List<SearchResult>
            {
                new SearchResult { uri = "a", fields = new[] { "Banana" } },
                new SearchResult { uri = "b", fields = new[] { "apple" } },
                new SearchResult { uri = "c", fields = new[] { "cherry" } },
            };
            var sort = new[] { new SortColumn { table = "", name = "subject", direction = SortDirection.Forwards } };
            SearchBridge.ApplySortClientSide(all, sort, cols);
            // Case-insensitive ascending: apple, Banana, cherry
            Assert.That(all.Select(r => r.uri), Is.EqualTo(new[] { "b", "a", "c" }));
        }

        [Test]
        public void EnsureSortColumnsFetched_AppendsMissingSortColumn()
        {
            var display = new[] { new Column("", "name") };
            var cols = SearchBridge.EnsureSortColumnsFetched(display, DateSort(SortDirection.Backwards));
            var names = cols.Select(c => c.name).ToArray();
            Assert.That(names, Does.Contain("name"));
            Assert.That(names, Does.Contain("date"));
        }

        [Test]
        public void EnsureSortColumnsFetched_AlreadyPresent_NoDuplicate()
        {
            var display = new[] { new Column("", "date"), new Column("", "subject") };
            var cols = SearchBridge.EnsureSortColumnsFetched(display, DateSort(SortDirection.Backwards));
            Assert.That(cols.Count(c => string.Equals(c.name, "date", System.StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
        }

        [Test]
        public void EnsureSortColumnsFetched_AddsToEmptyDisplay()
        {
            // When the caller asked for bare results, sorting still forces the sort column to be fetched.
            var cols = SearchBridge.EnsureSortColumnsFetched(new Column[0], DateSort(SortDirection.Backwards));
            Assert.That(cols.Select(c => c.name), Does.Contain("date"));
        }
    }
}
