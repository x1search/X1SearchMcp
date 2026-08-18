// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1694: SearchSingleTableAsync's two empty-response retry loops (count-only recovery, and
    /// mid-pagination retry) used to give up after a fixed ~3 attempts (~900ms-1.2s), regardless of
    /// how much of the caller's own timeoutMs budget remained - a slow/large table (e.g. a 193K-hit
    /// Teams search) could be abandoned in under 2 seconds even when given a generous 60s timeout.
    /// This traced back to the ticket's "byTable count correct, rows missing" symptom.
    ///
    /// The fix replaced the fixed attempt count with SearchBridge.RemainingBudgetMs - both loops now
    /// poll until this drops to MinRetryBudgetMs instead of after N tries. These tests pin that pure
    /// budget arithmetic directly, since the loops themselves need a live search session to drive
    /// end-to-end (see SearchBridgeSessionGateTests' doc comment on why X1MCPSearchConnection can't
    /// be redirected to a fake channel in this test project).
    /// </summary>
    [TestFixture]
    public class SearchBridgeRetryBudgetTests
    {
        [Test]
        public void RemainingBudgetMs_JustStarted_ReturnsCloseToFullTimeout()
        {
            var searchStart = DateTime.UtcNow;
            int remaining = SearchBridge.RemainingBudgetMs(searchStart, timeoutMs: 60000);

            // Close to the full 60000ms budget - proves the loop isn't bounded by a small fixed
            // window the way the old ~900ms-1.2s attempt cap was.
            Assert.That(remaining, Is.GreaterThan(59000));
        }

        [Test]
        public void RemainingBudgetMs_MostOfBudgetElapsed_StillPositiveAndUsable()
        {
            // 58 of a 60-second budget already spent - the old fixed-attempt-count loops would have
            // given up long before this point; the budget-driven loop should still see ~2s left.
            var searchStart = DateTime.UtcNow.AddMilliseconds(-58000);
            int remaining = SearchBridge.RemainingBudgetMs(searchStart, timeoutMs: 60000);

            Assert.That(remaining, Is.GreaterThan(0));
            Assert.That(remaining, Is.LessThan(3000));
        }

        [Test]
        public void RemainingBudgetMs_BudgetExhausted_ReturnsNonPositive()
        {
            var searchStart = DateTime.UtcNow.AddMilliseconds(-61000);
            int remaining = SearchBridge.RemainingBudgetMs(searchStart, timeoutMs: 60000);

            Assert.That(remaining, Is.LessThanOrEqualTo(0));
        }
    }
}
