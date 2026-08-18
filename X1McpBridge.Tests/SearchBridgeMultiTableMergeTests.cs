// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// Unit tests for SearchBridge.MergeTableResults - the pure aggregation step of the multi-table
    /// fan-out feature. No WCF/async involved: these hand-build the per-table JObjects that
    /// SearchSingleTableAsync would normally produce, so the merge logic itself gets real coverage
    /// without needing a live X1ServiceHost.
    /// </summary>
    [TestFixture]
    public class SearchBridgeMultiTableMergeTests
    {
        private static JObject FakeResult(int totalResults, int returned, params (string uri, string table)[] items)
        {
            var results = new JArray();
            foreach (var (uri, table) in items)
                results.Add(new JObject { ["uri"] = uri, ["table"] = table });

            return new JObject
            {
                ["totalResults"] = totalResults,
                ["returned"] = returned,
                ["results"] = results,
                ["highlightTerms"] = new JArray()
            };
        }

        [Test]
        public void MergeTableResults_TwoSuccessfulTables_SumsTotalsAndConcatenatesResults()
        {
            var outcomes = new List<SearchBridge.TableSearchOutcome>
            {
                new SearchBridge.TableSearchOutcome("Files", FakeResult(2, 2, ("a", "Files"), ("b", "Files")), null),
                new SearchBridge.TableSearchOutcome("MSMail", FakeResult(1, 1, ("c", "MSMail")), null),
            };

            var merged = SearchBridge.MergeTableResults(outcomes);

            Assert.That(merged.Value<int>("totalResults"), Is.EqualTo(3));
            Assert.That(merged.Value<int>("returned"), Is.EqualTo(3));

            var results = (JArray)merged["results"];
            Assert.That(results.Count, Is.EqualTo(3));
            // Table order preserved, not interleaved: Files' results first, then MSMail's.
            Assert.That(results.Select(r => r.Value<string>("uri")), Is.EqualTo(new[] { "a", "b", "c" }));
            Assert.That(results.Select(r => r.Value<string>("table")), Is.EqualTo(new[] { "Files", "Files", "MSMail" }));

            var byTable = (JArray)merged["byTable"];
            Assert.That(byTable.Count, Is.EqualTo(2));
            Assert.That(byTable[0].Value<string>("table"), Is.EqualTo("Files"));
            Assert.That(byTable[0].Value<int>("totalResults"), Is.EqualTo(2));
            Assert.That(byTable[1].Value<string>("table"), Is.EqualTo("MSMail"));
            Assert.That(byTable[1].Value<int>("totalResults"), Is.EqualTo(1));
        }

        [Test]
        public void MergeTableResults_OneTableErrors_OtherStillMerged()
        {
            var outcomes = new List<SearchBridge.TableSearchOutcome>
            {
                new SearchBridge.TableSearchOutcome("Files", FakeResult(5, 5, ("a", "Files")), null),
                new SearchBridge.TableSearchOutcome("Teams", null, "Search did not return results within 60000 ms."),
            };

            var merged = SearchBridge.MergeTableResults(outcomes);

            // Merged totals reflect only the successful table - the failed table contributes nothing.
            Assert.That(merged.Value<int>("totalResults"), Is.EqualTo(5));
            Assert.That(merged.Value<int>("returned"), Is.EqualTo(5));
            var results = (JArray)merged["results"];
            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results[0].Value<string>("uri"), Is.EqualTo("a"));

            var byTable = (JArray)merged["byTable"];
            Assert.That(byTable.Count, Is.EqualTo(2));
            Assert.That(byTable[0].Value<string>("table"), Is.EqualTo("Files"));
            Assert.That(byTable[0]["error"], Is.Null);
            Assert.That(byTable[1].Value<string>("table"), Is.EqualTo("Teams"));
            Assert.That(byTable[1].Value<int>("totalResults"), Is.EqualTo(0));
            Assert.That(byTable[1].Value<int>("returned"), Is.EqualTo(0));
            Assert.That(byTable[1].Value<string>("error"), Is.EqualTo("Search did not return results within 60000 ms."));
        }

        [Test]
        public void MergeTableResults_HighlightTermsDedupedAcrossTables()
        {
            JObject Highlight(string term) => new JObject { ["term"] = term, ["column"] = "", ["findType"] = 2 };

            var filesResult = FakeResult(1, 1, ("a", "Files"));
            filesResult["highlightTerms"] = new JArray { Highlight("acme") };
            var mailResult = FakeResult(1, 1, ("b", "MSMail"));
            mailResult["highlightTerms"] = new JArray { Highlight("acme"), Highlight("contract") };

            var outcomes = new List<SearchBridge.TableSearchOutcome>
            {
                new SearchBridge.TableSearchOutcome("Files", filesResult, null),
                new SearchBridge.TableSearchOutcome("MSMail", mailResult, null),
            };

            var merged = SearchBridge.MergeTableResults(outcomes);
            var highlights = ((JArray)merged["highlightTerms"]).Select(h => h.Value<string>("term")).ToList();

            // "acme" appears in both tables' highlightTerms but should be deduped to one entry.
            Assert.That(highlights.Count(t => t == "acme"), Is.EqualTo(1));
            Assert.That(highlights, Does.Contain("contract"));
        }

        [Test]
        public void MergeTableResults_EmptyOutcomes_ReturnsZeroedEmptyShape()
        {
            var merged = SearchBridge.MergeTableResults(new List<SearchBridge.TableSearchOutcome>());

            Assert.That(merged.Value<int>("totalResults"), Is.EqualTo(0));
            Assert.That(merged.Value<int>("returned"), Is.EqualTo(0));
            Assert.That(((JArray)merged["results"]).Count, Is.EqualTo(0));
            Assert.That(((JArray)merged["highlightTerms"]).Count, Is.EqualTo(0));
            Assert.That(((JArray)merged["byTable"]).Count, Is.EqualTo(0));
        }

        [Test]
        public void MergeTableResults_PreservesInputOrder()
        {
            var outcomes = new List<SearchBridge.TableSearchOutcome>
            {
                new SearchBridge.TableSearchOutcome("Teams", FakeResult(1, 1, ("t", "Teams")), null),
                new SearchBridge.TableSearchOutcome("Files", FakeResult(1, 1, ("f", "Files")), null),
                new SearchBridge.TableSearchOutcome("MSMail", FakeResult(1, 1, ("m", "MSMail")), null),
            };

            var merged = SearchBridge.MergeTableResults(outcomes);

            var byTable = (JArray)merged["byTable"];
            Assert.That(byTable.Select(b => b.Value<string>("table")), Is.EqualTo(new[] { "Teams", "Files", "MSMail" }));

            var results = (JArray)merged["results"];
            Assert.That(results.Select(r => r.Value<string>("uri")), Is.EqualTo(new[] { "t", "f", "m" }));
        }

        [Test]
        public void MergeTableResults_ResultFieldsAreJagged_NotNormalizedAcrossTables()
        {
            // Files rows carry name/path fields, MSMail rows carry subject/from - a deliberately
            // different "fields" shape per table. The merge must not try to unify these; it should
            // pass each result's own fields object through unchanged, distinguishable via "table".
            var filesResult = FakeResult(1, 1);
            filesResult["results"] = new JArray
            {
                new JObject { ["uri"] = "a", ["table"] = "Files", ["fields"] = new JObject { ["name"] = "budget.xlsx", ["path"] = "C:\\docs" } }
            };
            var mailResult = FakeResult(1, 1);
            mailResult["results"] = new JArray
            {
                new JObject { ["uri"] = "b", ["table"] = "MSMail", ["fields"] = new JObject { ["subject"] = "Q3 review", ["from"] = "alice@x1.com" } }
            };

            var outcomes = new List<SearchBridge.TableSearchOutcome>
            {
                new SearchBridge.TableSearchOutcome("Files", filesResult, null),
                new SearchBridge.TableSearchOutcome("MSMail", mailResult, null),
            };

            var merged = SearchBridge.MergeTableResults(outcomes);
            var results = (JArray)merged["results"];

            var filesFields = (JObject)results[0]["fields"];
            var mailFields = (JObject)results[1]["fields"];
            Assert.That(filesFields.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "name", "path" }));
            Assert.That(mailFields.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "subject", "from" }));
        }
    }
}
