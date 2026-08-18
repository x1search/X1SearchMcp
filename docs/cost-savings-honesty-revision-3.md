# Cost-savings report — revision 3 (honesty hardening)

## Why

The report's job is to tell the truth about a retrieval's token cost versus the realistic
alternative. Revisions 1 and 2 each fixed a distortion and introduced another, always in the same
direction — the number came out bigger than the evidence supported. This revision's goal is not a
better number; it is a number whose every component is traceable to either a direct observation or
a labeled assumption carrying its own evidence.

### What revisions 1–2 got wrong, as a pattern

| Revision | The claim | Why it was wrong |
|---|---|---|
| 1 | Every call vs. base64 (`file_bytes ÷ 3`) | One counterfactual for all object types; metadata scored ~0, text overstated, scans understated |
| 2 | Per-category coefficients | Right idea; but priced a *path* against a *payload*, so a 3 MB fragment "saved" 1.6M tokens; claimed vision cost with no evidence a text layer was absent; counted cloud-synced OneDrive files as unreachable |

The through-line: **the model kept comparing non-equivalent outcomes and calling the difference a
saving.** Every remaining item below is a variant of that same failure, so the fixes are ordered by
how much fiction each removes.

### Already fixed (landed, `coefficientsVersion` 2026-07-30r3)

- **Path is not a payload.** `x1_generate_preview output=file/save` and `x1_get_content
  mode=preview` claim **zero** savings. The content never entered context, so there is no
  like-for-like comparison. Those tokens are reported as `estimatedTokensDeferred` — accurate,
  because reading the file later costs them then. This removed the single largest fictional line
  item in the report.
- **Vision cost requires evidence.** Per-page vision/OCR pricing applies only where x1's own
  extraction came back sparse against a substantial file (positive evidence of no text layer).
  Where text extracted cleanly, a local parse of the downloaded file would have reached the same
  text, so it is priced as text. Unverifiable cases never earn the vision multiple.
- **Capability gain excludes cloud-synced paths.** Files under OneDrive/Dropbox/Google Drive
  folders are the same item the connector reaches via its own API. In the first live report all
  four "capability gains" were OneDrive files — every one a false positive.
- **Capability gain no longer filters by object type.** The bar is literally "the connector could
  not reach it"; restricting to xlsx/pdf smuggled in "and it was hard to read".
- **No invented fallback.** An unmeasurable file used to be assigned a flat 50,000-token baseline.
  It now claims nothing.

---

## Revision 3 — the work

### 1. Fit the counterfactual's real shape: `fixed + slope × content`  ← highest impact

The current model is `connector_tokens = x1_tokens ÷ ratio`, which assumes the connector's excess
scales entirely with content length. Much of it doesn't. Two ~550-char tracking URLs,
`internetMessageId`, `conversationId`, and the duplicated id/uri are **fixed per item**. So a short
email is a large multiple and a long one a small multiple, and a single flat ratio over-credits long
items while under-crediting short ones. No uncertainty band repairs a wrong functional form.

- Replace each ratio with a two-term fit: `fixed_tokens + slope × x1_tokens`.
- Requires **≥2 paired samples per category at deliberately different content lengths** (a short
  and a long email; a small and a large workbook). Three or more is better.
- Report both terms so a reader can see which part of the claim is per-item overhead and which
  scales.

### 2. Replace hardcoded constants with an auditable evidence ledger

Coefficients currently live as `const double` with an explanatory comment. That makes `n=1`
invisible to anyone reading the output and makes re-derivation a manual archaeology exercise.

Ship `coefficients.json` beside the exe, recording per category:

```json
{
  "category": "TextContent",
  "fixedTokens": 180,
  "slope": 2.1,
  "n": 4,
  "measuredOn": "2026-07-30",
  "samples": [
    { "table": "MSMail", "x1Chars": 6989, "connectorChars": 15306, "itemHash": "sha256:…" }
  ]
}
```

`x1_cost_savings` then cites `n` **inline, per category row** rather than burying sample size in a
version string. A row backed by one sample and a row backed by forty should not look alike.
Item identifiers are hashed — the ledger records that a measurement happened and its magnitudes,
never mailbox content.

### 3. Ship the paired-measurement harness

"Re-derive periodically" only happens if it's cheap. Add a script (or `/x1` skill verb) that takes
a table plus a handful of URIs, runs both the x1 path and the managed-connector path, and appends
the result to the ledger with its `n` incremented.

This also converts the report from a self-assessment into something reproducible: a customer can
derive coefficients from **their own** mail and files rather than inheriting ours. That is the
honest answer to "these numbers describe the tool measuring itself."

### 4. Report a real interval, or none

The current `±10` band is a hardcoded placeholder. It signals uncertainty, which is good, but it
implies we know the uncertainty *is* ±10, which we don't.

- With `n ≥ 3` per category: compute an actual interval from the samples and say so.
- With `n < 3`: publish the point estimate labeled **indicative, not interval-bounded**, and show
  `n`. An honest "we have one sample" beats a fabricated confidence interval.

### 5. Fix or drop the dollar figure

`estimatedCostSaving` prices every saved token at full Sonnet input rate. It ignores prompt caching,
which bills cached input far lower — plausibly overstating by ~10× for cached content. It is also
the most quotable number in the report and the least defensible.

Options, in order of preference: (a) drop it, since tokens are the honest unit; (b) show a range
spanning cached and uncached rates; (c) keep a single figure but label it explicitly as an
uncached-rate upper bound. Currently the note says (c) — moving to (a) or (b) is the improvement.

### 6. Measure bytes instead of deriving them

`bytesTotal` is computed as `x1Tokens × 4`, then reported as `totalBytesReturned` as though it were
observed. It is circular. Plumb the actual serialized response length through the `Record*` methods
and report the measured value. Small change; removes a fake measurement from the "observed" column.

### 7. Model the connector's real call chain, or state that we don't

The methodology calls for counting the full realistic chain: the connector's path to an email body
is `search → read_resource` (two round trips), and to a binary is `download → parse`. We compare
one x1 call against one connector call. This is the one place the report is **conservative**, and
it should either be modeled or named as a known under-count — right now it's neither.

### 8. Restructure the report around provenance

Three kinds of number are currently interleaved. Separate them explicitly:

1. **Observed** — x1 tokens, latency, items, measured bytes. No modeling; always true.
2. **Modeled** — the counterfactual, each figure carrying its coefficient, `n`, and fit form.
3. **Neither** — `estimatedTokensDeferred`, `capabilityGainCount`. Not savings; never blended in.

Lead with (1). The current report leads with a modeled hero figure, which inverts the confidence
ordering: the least certain number is the largest thing on the page.

### 9. Encode the never-claim rules as tests

The invariants that keep re-breaking deserve to fail loudly. Four are already covered by tests;
add the fifth and sixth:

- ✅ Never claim savings when content didn't enter context.
- ✅ Never claim vision cost without no-text-layer evidence.
- ✅ Never claim a capability gain for a cloud-reachable item.
- ✅ Never invent a baseline for an unmeasurable file.
- ☐ Never report a point estimate without its `n`.
- ☐ Never let the blended figure exceed the highest per-category figure (a composition sanity check
  that would have caught the 1.6M fragment claim immediately).

### 10. Make the dashboard live, or label it a mockup

`scratch_x1_dashboard.html` has numbers typed in by hand, including reduction multiples computed
outside the code. It will silently show stale figures with nothing indicating staleness. Either
render it from the live tool response, or put the snapshot timestamp and the word "snapshot" where
a reader cannot miss it.

### 11. Known limitation to keep stating: cloud-table classification

OneDrive/GDrive/SP365 URIs are opaque item ids with no filename, so extension-based classification
can't see them and they fall back to text pricing. This **under**-credits genuinely tabular or
scanned cloud content. It needs the search result's extension threaded through to
`x1_get_content`, whose input schema has no slot for it. Until then it stays a documented
under-count, which is the acceptable direction.

---

## Verification

1. Unit tests for the two-term fit, the ledger loader, and the two new invariants.
2. **Held-out validation**, as the methodology asks: fit coefficients on one set of paired samples,
   then check the blended estimate against a *different* set the fit never saw. Report the miss.
3. Re-run the live smoke test and confirm every reported number is traceable to the observed /
   modeled / neither bucket, with `n` visible on each modeled row.
4. Sanity check: the blended figure must sit inside the range of per-category figures.

## The honest summary of where this lands

Even fully executed, this is an estimate of a counterfactual that was never run. The strongest
claims the report can make are the *observed* ones — x1 returned this many tokens, in this much
time, and reached content no cloud connector could see. The modeled comparison is worth reporting
because the question is worth answering, but it should be visibly the softest number on the page,
not the headline.
