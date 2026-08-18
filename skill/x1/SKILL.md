---
name: x1
description: >-
  Find and preview the user's own content through the x1-search MCP connector — a single
  local index over their local files, Outlook/Exchange/Gmail email and email attachments,
  cloud files (OneDrive, GDrive, Dropbox, SharePoint/SP365), and Teams/Slack messages.
  Use this whenever the user wants to locate, search for, or preview something of theirs:
  "find the most recent X", "what did someone send me", "search my files/email for …",
  "pull up that contract/deck/spreadsheet", "preview/open that document", or any request to
  surface a file, email, attachment, or message by topic, sender, date, or type — even if
  they never say "x1" or "search". Strongly prefer this over generic email/file APIs for
  attachments and large files: x1 returns a local file path instead of streaming megabytes
  of base64 through the conversation, so it is far cheaper and avoids blowing the context window.
---

# x1 — find and preview your stuff

The `x1-search` connector is a local X1 Search index covering many of the user's sources at
once: local **Files**, email (**MSMail** for Microsoft 365, **Email** for local Outlook and
IMAP, **Exchange**, **Gmail**) and their
**attachments**, cloud files (**OneDrive**, **GDrive**, **Dropbox**, **SharePoint**/**SP365**,
**Box**), and chat (**Teams**, **Slack**). Everything is already indexed locally, so finding
and previewing the user's own content is fast and — importantly — cheap in tokens.

## Why prefer x1 (the token-efficiency principle)

The biggest reason to reach for x1 instead of a generic email/file API: **x1 hands back a local
file path, never the file's bytes.** A 3 MB PowerPoint fetched from a mail API as base64 is
~1.1M tokens — it overflows the context window. The same deck via x1 is a one-line path
(~70 tokens); you read it locally. So for anything involving an **attachment** or a **large
file**, x1 is not just convenient, it's often the only approach that fits in context.

## Workflow

### 1. Discover sources — ALWAYS call this first

Call `x1_list_sources` before every search session. It tells you which tables are actually
configured and indexed on this machine.

**Only search tables with at least one `accounts` entry where `itemCount > 0`.** Tables with no
`accounts` entries, or where every entry is `itemCount == 0`, are not configured for this user —
searching them will time out. Never attempt to search a table that doesn't appear in the active set.

A **named account is not required**. Local sources report `accountName: null` and are still fully
configured — `Files` is the usual case. Judge by `itemCount`, never by whether an account is named.

Example: if `x1_list_sources` reports `itemCount > 0` only on `Files`, `MSCalendar`, `OneDrive`,
`SP365`, and `Teams`, then **only those five tables exist for this user**. Do not search
`MSMail`, `Exchange`, `Gmail`, `Slack`, etc. — they are not connected.

**`itemCount` vs `totalCount` — do not add these up.** `itemCount` is that account's own count.
`totalCount` is **scanner-wide**: when several accounts share one schema (two Outlook or IMAP
accounts, say), every one of them repeats the *same* combined number. Summing `totalCount` across
a table's accounts therefore multiplies the real figure. Quote `itemCount` for "how much is in
this account", and a single entry's `totalCount` for "how much is in this source". Where a
scanner has just one account the two are equal — which is why a wrong assumption here reads as
correct on most machines.

**Match the user's words to a source with `displayName`, then search its `schemas` — not its
`name`.** The three fields do different jobs, and conflating them is the most common way to
search the wrong thing or nothing at all:

| Field | What it is | Use it for |
|---|---|---|
| `displayName` | human label — "MS Mail", "SharePoint 365", "Outlook Calendar" | matching what the user asked for, and telling them what you searched |
| `schemas` | the actual searchable table(s) | **the `tables` parameter** |
| `name` | the scanner's own label | identifying the source in `x1_list_sources` output — **not** guaranteed to be searchable |

**`name` is not always a table.** The Gmail source reports `name: "Modern Gmail"` with
`schemas: ["Gmail"]` — `tables: ["Modern Gmail"]` errors, `tables: ["Gmail"]` returns results. It
holds for most sources only because their `name` and first schema happen to be spelled the same
(`Files`, `MSMail`, `OneDrive`…), which is exactly why passing `name` looks correct right up until
it doesn't. Take the table from `schemas`.

Prefer matching on `displayName` over the table below: it comes from the live index, so it stays
right as scanners are added or renamed, and the table cannot. `displayName` is per-account, so
sibling accounts under one scanner can legitimately differ (`IMAP - Gmail` vs `IMAP - Yahoo`).

**Multiple `schemas`** means multiple searchable tables for one source; the first is the primary.
The PST scanner has several — `PSTFile`, `PSTEmail`, `PSTCalendar`, `PSTContact`, `PSTNote` —
with `PSTFile` (listed first) holding the indexed `.pst` files themselves. `Teams` likewise
carries `Teams`, `TeamsChannel`, `TeamsChat`. Pass any schema name to `x1_get_schema_fields` to
see its fields.

### 2. Search — one table or several in one call, active tables only

`x1_search` accepts one table, or several tables in one call — e.g. `tables: ["Files", "MSMail"]`
searches both and returns a single merged response, so a request spanning multiple source types
no longer needs a separate call per table. The bridge fans this out internally as one sequential
search per table (never in parallel — an X1 service-side limitation), then merges: `totalResults`/
`returned` are summed across tables, `results` is every table's hits concatenated (each already
tagged with its own `table` field), and a new `byTable` array reports `{table, totalResults,
returned, error?}` per table, so one table failing doesn't lose the others' results. Rows are
deliberately jagged — a result's `fields` reflect whatever `displayFields` resolved for *that*
result's own table, which can differ per table; use the `table` field to know how to read a row.

**Latency tradeoff**: `limit` and `timeoutMs` both apply **per table**, not divided across them,
so an N-table call can take up to `N × timeoutMs` in the worst case. Prefer fewer tables per call,
or a shorter `timeoutMs`, when latency matters more than completeness. Pick tables from the
**active tables identified in step 1**.

**When to reach for multi-table vs. separate single-table calls** — multi-table isn't strictly
better; it's the right choice specifically when the request has one shared intent across sources.
Use single-table (or several separate calls) instead when:
- **`query` or `displayFields` genuinely need to differ per table.** Both are shared across the
  whole fan-out with no per-table override (unlike `filters`/`sort`, which do support one) — a
  merged call is stuck using one query string and either one shared `displayFields` list or each
  table's configured defaults.
- **The next step depends on what the first table returns.** Fan-out commits to every requested
  table up front, sequentially — there's no "search Files, and only search MSMail if Files came
  back empty." That's an adaptive flow, not a merge.
- **One requested table is dramatically bigger/slower than the others** (e.g. Teams' 300K+ items
  vs. Files' 10K). Fan-out is strictly sequential (never parallel — an X1 service-side
  concurrency limitation), so the whole merged response waits for the slowest table's turn even
  if you only needed a fast answer from a small one.
- **Only one table is actually relevant.** Adding others "for completeness" costs latency and
  returns noise, not signal — e.g. recently-modified `Files` results are rarely meaningful
  for a "what changed" or todo-style question compared to `MSMail`/`Teams`, which is where that
  kind of actionable signal actually tends to live.

**Match on `displayName` from that live output first.** It is what the user's words actually
resemble ("my SharePoint" → `displayName: "SharePoint 365"`), and it is current for this machine.
Then pass that source's `schemas` entry — not its `name` — to `tables`.

The table below is a fallback for when the live output is ambiguous. It is a snapshot of common
spellings and can go stale; `x1_list_sources` cannot.

| User wants… | usual table (if active) |
|---|---|
| local files / documents on disk | `Files` |
| **local Outlook / Windows Mail** (and attachments) | `Email` — add `source:outlook` |
| Microsoft 365 / Outlook Online mail | `MSMail` |
| Exchange mail | `Exchange` |
| Gmail | `Gmail` — note the source is named `Modern Gmail`; the table is `Gmail` |
| OneDrive / Google Drive / Dropbox / SharePoint | `OneDrive` / `GDrive` / `Dropbox` / `SP365` |
| Teams / Slack messages | `Teams` / `Slack` |

**Local Outlook is `Email`, not `MSMail`.** `MSMail` is the Microsoft 365 scanner (mail held in
the cloud mailbox); local Outlook and Windows Mail are indexed into `Email`. Reaching for `MSMail`
for "my Outlook mail" searches the wrong store, and on a machine with only local Outlook it
searches nothing at all.

`Email` is shared with the IMAP scanner, so **always add `source:outlook`** when the user means
Outlook — otherwise IMAP messages come back mixed in with no way to tell them apart. Use
`source:imap` for the other side.

If the preferred table for a request has no entry with `itemCount > 0`, tell the user that source
isn't indexed rather than attempting the search. If a request spans sources, make a separate call
per **active** table only.

**Query operators** (put them in the `query` string, not the `filters` param):

- `type:pptx` / `type:pdf` / `type:docx` / `type:xlsx` — filter by file type. Always use
  `type:` in the query for extensions; the `filters` param is unreliable for that.
- `att:pptx` — emails that **have** a `.pptx` attachment (matches the attachment-name field).
- `from:alice` / `subject:invoice` — email sender / subject.
- `path:woodworking` / `name:budget` — folder / filename for files.
- `Tags:isocert` — items carrying an X1 tag. **Note the capital-T display name**; the internal
  field name `x1tag` does **not** work here (see the naming rule below).
- `source:outlook` / `source:imap` — which scanner an item came from. Required on `Email`, where
  local Outlook and IMAP share the table and are otherwise indistinguishable in results.
- Terms are implicitly AND-ed. Use `AND`/`OR`/`NOT` and parentheses for control:
  `project status (failed OR threatened)`.

#### Field names: three different conventions

`x1_get_schema_fields` returns both a `name` (internal, e.g. `x1tag`) and a `displayName`
(e.g. `Tags`) for every field. **Which one you pass depends on the parameter:**

| Parameter | Takes | Example |
| --- | --- | --- |
| `query` string `field:value` | **`displayName`** only | `Tags:isocert`, `Received:2025` |
| `filters` | either — the bridge translates | `{"column":"x1tag","term":"isocert"}` |
| `displayFields`, `sort` | **`name`** (internal) only | `["subject","x1tag"]` |

The query string matches `displayName` **case-insensitively by prefix**. That is why the
operators above work at all: X1 usually names the internal field as a lowercase truncation of
the display name (`from` → `From`, `att` → `Attachments`), so the internal name happens to be a
valid prefix. They are not special-cased — there is no operator table in the product. `x1tag` →
`Tags` is the one common field where that convention breaks, which is exactly why it fails.

**Two traps that follow from prefix matching:**

- **An unrecognized `field:` prefix does not error — it silently returns 0 results.** The term
  degrades to a literal content search for the text `x1tag:isocert`, which matches nothing. This
  is indistinguishable from "no items match", so *do not conclude a search found nothing until
  you have confirmed the field prefix is a real display name.* Prefer `filters` (which accepts
  either name) whenever you are not certain.
- **Ambiguous prefixes resolve to the alphabetically-least display name, silently.** On `Files`,
  `date:` matches `Date Accessed` — not `Date Created`/`Date Modified`/`Date Taken`. Spell
  ambiguous fields out (`DateModified:`, spaces are stripped) or use `filters`.

**Unsure which field names are valid for a table?** Call `x1_get_schema_fields` with that
table (or PST sub-schema) name instead of guessing — it returns the real field list
(`name`, `displayName`, `fieldType`, `isIndexed`/`isContent`/`isStored`). Use it for tables
not covered by the hardcoded examples in this skill (e.g. `JIRA`, `Skype`, or any schema
surfaced via `x1_list_sources`' `schemas` array), or whenever `x1_search` errors on an
unrecognized `displayFields`/`filters`/`sort` column — and read the table above to pick the
right column of its output for the parameter you are filling in.

Use `displayFields` to ask for just the columns you'll show, e.g.
`["subject","from","date","att"]` for mail or `["name","path","modified"]` for files.

**`include_snippets` (default `true`) drives the `snippet` field** on each result — the
bridge requests keyword-match statistics from the service only when this is `true` (gathering
them costs extra processing), so `snippet` is populated with the actual matched-keyword text
for that result. Pass `include_snippets: false` when you don't need snippets, to skip that
extra cost.

**`progenitorSearch` (default `false`) controls how child items are counted.** Attachments are
indexed as their own items, with their own URIs (`<parent-uri>/<child-id>`) and their own copy of
the parent's `subject`/`from`/`date`:

- `false` (default) — every item counts separately. An email with 2 attachments contributes
  **3** rows, all sharing the same subject and date.
- `true` — results are rolled up to the **progenitor** (the parent email), so that same email
  contributes **1** row.

**Use `progenitorSearch: true` whenever you are counting, or reporting "the last N", or
presenting a list to the user** — otherwise attachments show up as apparent duplicates of their
parent and can crowd out real results from a top-N. Use the default `false` when you specifically
need to reach an attachment as its own item (e.g. to preview or extract it).

**⚠ `progenitorSearch: true` changes the default result order to OLDEST-first.** With the default
`false` an unsorted search comes back newest-first, so a "last N" request accidentally works; with
`true` and no `sort`, the same request silently returns the N *oldest* items. **Always pass an
explicit `sort` alongside `progenitorSearch: true`** — e.g.
`sort: [{"column":"date_received","direction":"desc"}]` (internal field name). Verified: with the
sort applied, `true` and `false` return the same newest-first top rows.

Worked example: `from:dweber` on MSMail returns **108** by default but **92** with
`progenitorSearch: true` — 92 real emails plus 16 attachment children.

**Tagging applies to the whole family.** `x1_add_tags` on a parent URI also tags its attachment
children, so a later `Tags:x` search returns more items than you tagged (92 tagged → 106 matched
in the example above). This is expected — search with `progenitorSearch: true` to get the count
back to the number of emails.

### 3. Sort

`x1_search` takes a `sort` array of `{ column, direction, table? }`. For newest-first email use
`sort: [{ "column": "date", "direction": "desc" }]` (`desc`/`descending`/`backwards` = descending;
`asc`/`ascending`/`forwards` is the default). The bridge **enforces the order itself** — it auto-
fetches the sort column, over-fetches a small buffer, and re-sorts — so results come back reliably
ordered even when X1 would otherwise pin a "current" item to the top. Use a real indexed field for
`column` (e.g. `date`).

Dates are **OA-format serial numbers** (days since 1899‑12‑30); **higher = more recent**. To read
one: `powershell -c "[datetime]::FromOADate(46184.69337)"`. So "most recent" = the result with the
largest `date`, after filtering to the sender/type the user asked for.

### 4. Act — actions are already in the result

Every result includes an `actions` array (because `includeActions` defaults to `true`), so you
**don't** need `x1_list_actions`. To act, call `x1_execute_action` with the result's
`table`+`uri` and one of:

- `get_path` — return the local file path (data; no side effect).
- `open` — open the file / message / cloud cached copy in its app.
- `show_in_folder` — reveal it in Explorer.
- `get_url` / `open_url` — web URL for Gmail/GDrive/cloud items.

`open`/`show_in_folder`/`open_url` launch apps or the browser, so they may prompt — that's by
design.

**Need specific field values for one item rather than a full preview?** Call `x1_get_metadata`
with that result's `table`+`uri` (and optionally `fields` to restrict which ones come back) —
cheaper than `x1_generate_preview` when you just need a couple of field values, not the rendered
document.

### 5. Preview — and RENDER it

Call `x1_generate_preview` (table + uri + **maxChars** + output). **Always render the result —
don't dump the markup.**

**Always pass `maxChars`.** Omit or `0` for the full document; `4000-8000` gives a readable
~2-3 page summary — pass a smaller value when you only need a skim or the item is huge (a long
PDF or email thread). It bounds both inline HTML size and how much gets written to a `file`/`save`
fragment.

#### Choose the output mode based on intent

| You need to… | `output=` | Returns | Token cost |
|---|---|---|---|
| Display it to the user (default) | `"file"` | `{ mode:"file", path, title, previewType, contentType, bytes }` | ~80 tokens (path only) |
| Read / reason over the content (summarise, quote, compare) | `"inline"` | `{ html, contentType, previewType, title }` | Full HTML in context |
| Persist for later use / company archive | `"save"` | `{ mode:"save", path, title, previewType, contentType, bytes }` | ~80 tokens (path only) |

**Default to `output="file"` and render as an Artifact.** Only switch to `output="inline"` when
you must read the content yourself to answer the user's question (e.g. "summarise this doc",
"what does this email say?", "quote the key clause"). If the user says "show me", "preview",
"open", or "pull up" — use `output="file"` and publish the returned `path` as an Artifact.
Never dump raw HTML into the chat.

**Use `output="save"` when the user says "save this", "keep a copy", "archive", or is batch-
saving a set of items** (e.g. "save all my expense receipts"). The HTML fragment is written to
a persistent configured directory (default: `Documents\X1 Saved\{date}\`) that survives reboots
and can be configured company-wide. The returned `path` points to the permanent location — render
it as an Artifact the same way as `output="file"`. An append-only `manifest.json` is updated in
the save directory for auditing and indexing.

#### Rendering

- **Default (file mode) and save mode:** call the `Artifact` tool with `file_path = path`
  returned by the tool. The file is a self-contained HTML fragment; bytes never enter context.
- **Inline mode (read-and-respond only):** render the returned `html` as an **Artifact** using
  `title` as the artifact title. Use this only when you need to read the content before replying.
- **Never use `show_widget`** for previews — always publish as an Artifact so the user gets a
  hosted page they can keep open or share.

#### Binary objects (images and PDFs)

When the source is a local image (png/jpg/gif/webp/bmp/svg) **under 1 MB**, the bridge embeds the
bytes as a `data:` URI directly in the `html` — so the preview is fully self-contained with no
external references. Images appear as `<img>`. Images over 1 MB fall back to a metadata card.

**PDFs never go through that embed path at all — the 1 MB cap doesn't apply to them.** PDF
handling extracts the text and reflows it into a formatted "book"-style HTML article (headings,
paragraphs, a table of contents for longer documents), the same as `.docx`. There is no raw
`<embed>`/`<object>` for PDFs to be blocked by the Artifact sandbox's CSP — confirmed live: calling
`x1_generate_preview` on a real PDF returns clean, readable formatted HTML directly, at any size.
Treat `previewType: "pdf"` exactly like `docx`/`html`/`text` (routing table below) — no manual
composed-preview fallback needed. (Older guidance here said otherwise; that was true once but the
PDF branch was rewritten to extract-and-reflow instead of embedding raw bytes.)

#### Check `previewType` — routing table

| `previewType` | Action |
|---|---|
| `docx` / `html` / `text` / `pdf` | Render natively — `x1_generate_preview` already returns clean, formatted HTML (PDF as a reflowed "book" article), renders correctly in the Artifact sandbox. |
| `image` | Render natively — embedded as `<img data:…>`, renders correctly. |
| `metadata_card` | **Use composed preview** — no document body in the card. Call `x1_get_content mode:"content"` and build a styled HTML artifact from the extracted text. |

#### Composed preview — fallback when `x1_generate_preview` gives `metadata_card` (or is absent)

When a real preview is unavailable, build a formatted preview from extracted text:

1. **Extract text** — call `x1_get_content` with `mode: "content"` on the same `table` + `uri`.
   This returns the item's full plain text (already indexed by X1) in a few hundred tokens.
2. **Compose a styled preview** — format the extracted text into a well-structured HTML
   artifact using the `artifact-design` skill guidance:
   - Open with a **provenance strip** (filename, folder/source, page count or size if known).
   - Use a narrow (≤740px) single-column document card with a 3px accent top border.
   - Apply a type scale: condensed sans (`"Arial Narrow"` or similar) for section labels and
     headings; a serif or readable sans for body text; monospace for tabular data.
   - Choose a palette grounded in the document's subject — avoid generic warm-cream or
     purple-to-blue defaults. Pick 4–6 named hex values before writing any code.
   - Render tables (filter classes, pricing grids, schedules) in their own `overflow-x: auto`
     wrapper with `font-variant-numeric: tabular-nums`.
   - Close with a **document footer** showing source, date, and any licence information
     found in the text.
3. **Publish as an Artifact** — write the HTML to a temp file and call the `Artifact` tool
   with that path. Never dump the formatted content as raw chat text.

**When to use this pattern:**
- `x1_generate_preview` returns `previewType: "metadata_card"` and the user wants the content.
- The user explicitly asks to "preview", "show", or "format" a document for which no cached
  preview exists (common for cloud-only files, large PDFs over the 1 MB embed cap, or items
  indexed but not locally cached).
- As a general enrichment: even when a `pdf`/`docx` preview is available, you may compose a
  text-based preview alongside it when the user wants a readable, annotated, or structured
  rendering rather than the raw file display.

**Token cost note:** this path is very efficient — `x1_get_content` delivers pre-extracted
plain text in ~1,000–5,000 tokens; producing the formatted artifact adds only the output
tokens for the HTML. Compare to reading the raw binary via a file API (~50,000–500,000
tokens for a typical PDF). The composed preview is almost always the right fallback.

### 6. Extracting text from documents

Two tools extract plain text, and they are **not interchangeable** — they solve different
problems:

| Tool | Requires the item to be indexed? | Input |
|---|---|---|
| `x1_get_content` (mode `"content"`) | **Yes.** Looks up the item by its indexed `table`+`uri`. Fails on anything not in the X1 index — even if the file exists on disk. | `table`, `uri` (from an `x1_search` result) |
| `x1_extract_file` | **No.** Works on any local file, indexed or not — same extraction pipeline X1 uses at index time. | `file` (a raw local path) |

**Decision rule — pick based on where the path came from, not by trial and error:**
- The item came from an `x1_search` result (you have a real `table` + `uri`) → use
  `x1_get_content mode:"content"`. This is the token-efficient default and hits the content
  store on repeat calls.
- The user gave you a **raw local file path directly** (typed in chat, or from a source outside
  X1 search — a download, a cached preview path, an arbitrary folder) and you have **not**
  confirmed it's indexed → go straight to `x1_extract_file`. Don't try `x1_get_content` first
  and wait for it to fail — a path that isn't indexed returns a generic
  `"Content extraction failed or timed out"` error from `x1_get_content` that doesn't distinguish
  "not indexed" from "actually timed out," so there's nothing to learn from trying it first.
- **Uncertain whether a path is indexed?** Run a quick `x1_search` with a `path:` filter for the
  file's folder first — if it comes back with a hit, use that result's `uri` with
  `x1_get_content`; if it comes back empty, use `x1_extract_file` directly on the raw path.
- **Both tools return the same underlying text** for a given file — `x1_extract_file` on an
  unindexed path returns byte-identical content to what `x1_get_content` would return once that
  same file is indexed. Neither is "better" text; the choice is purely about which one the path
  in hand is compatible with.

#### `x1_get_content` — modes explained

`x1_get_content` takes `table`, `uri`, and `mode`. Valid modes:

| Mode | What it returns | When to use |
|------|----------------|-------------|
| `"content"` | `{ text, state, cached }` — full plain extracted text of the item | **Preferred** when you need to read, summarise, quote, or reason over the document body |
| `"auto"` | Falls back to `"content"` if available; otherwise tries `"preview"` | Use when you're unsure whether content extraction is available; slightly slower |
| `"preview"` | `{ preview }` — local file path only (no text) | Fallback: get a cached local path for `extract-office-text.ps1` when `"content"` mode is empty/errors/times out on a `.pptx`/`.xlsx` |
| `"internal"` | Raw X1 index metadata (dates, size, PII flags, extraction info) | Diagnostics / showing metadata to the user; not document content |

**Always use `mode: "content"` when the user asks to extract, read, or get the text of an
indexed item.** Do not use `"extract"` — it is not a valid mode and will error. If the item is
not indexed, `x1_get_content` is the wrong tool entirely — use `x1_extract_file` instead (below),
not a different mode of this tool.

**Token cost note:** `x1_get_content` with `mode: "content"` is by far the most token-efficient
way to read a document — the bridge delivers pre-extracted plain text directly, avoiding the
megabytes of base64 that a direct file/API fetch would cost. It is responsible for the majority
of token savings in `x1_cost_savings` reports.

**Timeouts or failures on indexed items:** Very large PDFs (many MB) may time out during
extraction even when indexed. If `x1_get_content` times out or errors on an item you know is
indexed, try `x1_extract_file` on the same underlying local path as a fallback — it calls a
different extraction path and may succeed where the content-store path didn't.

#### `x1_extract_file` — works whether or not the file is indexed

Use `x1_extract_file` any time you have a **raw local file path** — indexed or not. It takes a
single `file` parameter (the full local path) and always extracts fresh, independent of the
X1 index.

```
x1_extract_file  file: "C:\path\to\document.pdf"
```

Returns `{ text, path, truncated? }` on success or `{ error }` on failure. Use this when:
- The user gives you a raw local path directly (not from an `x1_search` result) — this is the
  **default choice** for that case, not a fallback.
- The file was obtained via `x1_get_content mode:"preview"` (gives a cached local path) as a
  fallback after `mode:"content"` came back empty or errored, and you want to read its content.
- The file is not in the X1 index (e.g. a newly downloaded file, or one that lives outside the
  scanned folders) — `x1_extract_file` has no dependency on the index at all.
- `x1_get_content` timed out or errored on an item you believed was indexed — `x1_extract_file`
  uses a different extraction path and may succeed.

#### Save directory — ask once, remember for the session

Whenever you are about to **write extracted content to disk** (i.e. the user asks you to save
the extracted text, or the natural next step after extraction is writing a file), follow this
protocol:

1. **Check session memory first.** If a save directory has already been established in this
   conversation (the user answered this question earlier), use that directory — do **not** ask
   again.

2. **If no directory is known**, ask before writing **anything**:
   > "Where should I save the extracted file(s)? I'll remember this location for the rest of
   > the session."
   Use `AskUserQuestion` with a text input option so the user can type a path, or offer their
   `Documents` folder as a sensible default. Accept a bare folder path (e.g. `C:\Users\Stewart\Extracts`).

3. **Remember the answer** — store it as the session save directory. Every subsequent extraction
   in this conversation writes to that directory without prompting again.

4. **Construct the output path** as `<save_dir>\<original_filename_stem>_extracted.txt` (or
   `.html` if the output is formatted). If that file already exists, append a counter suffix:
   `_extracted_2.txt`, `_extracted_3.txt`, etc.

5. **Confirm after writing** — report the full path where the file was saved so the user can
   find it immediately.

**Exception:** if the user explicitly names a destination in their request ("save it to
`D:\output\`"), use that path directly and update the session save directory to match.

#### `x1_export_html` — native HTML export (tables, formatting, embedded images)

Use `x1_export_html` instead of `x1_get_content` when the user wants a **faithful visual
rendering** of a document rather than flattened plain text — e.g. a `.docx` with tables and
embedded images, where text extraction would lose the layout. It calls X1's own HTML export
engine server-side, which can produce a more accurate rendering than a bridge-composed preview
for complex documents.

```
x1_export_html  table: "Files"  uri: "file://C:\...\report.docx"
x1_export_html  file: "C:\path\to\local\file.docx"       # arbitrary local file, not indexed
```

Pass either `table` + `uri` (an indexed item) or `file` (a local path) — not both. Returns
`{ html, path, assetFolder }` on success or `{ error }` on failure. `assetFolder` is the
directory containing `path`; if the export produced sibling images, they live alongside the
HTML file in that same folder — resolve any relative `<img src="...">` references against
`assetFolder` when rendering (inline them as `data:` URIs the same way `x1_generate_preview`
does for images under 1 MB).

**When to prefer this over `x1_get_content` mode `"content"`:** the user asks to "preview",
"show me the formatting", "keep the tables", or the document is known to have embedded images
that matter to the request. For simple read/summarize/quote requests, `x1_get_content
mode:"content"` remains the more token-efficient default.

#### Attachments and the extraction gap

**Attachments are first-class indexed items.** An email's `.pptx`/`.pdf`/`.xlsx` shows up as its
own searchable item — find it with `type:pptx` (etc.) and it has its own **nested URI** of the
form `msmail://<account>/<emailId>/<attachmentId>`. Previewing the *email* URI shows the email
envelope; preview the *attachment* URI to get the file.

**Try `x1_get_content mode:"content"` first — even for `.pptx`.** Earlier guidance here said the
connector only extracts `.docx` natively and routed every `.pptx`/`.xlsx` through a two-step
mode:"preview" + local-script extraction. Verified 2026-07-16: `mode:"content"` on an indexed
`.pptx` (Files table) returns full, clean per-slide text directly — the same `ppt/slides/slideN.xml`
structure the script parses — with no local caching step and at normal content-mode token cost.
Treat `.pptx` the same as `.docx`: call `mode:"content"` and build the composed preview (§5) from
the result.

**Fall back to the local-cache + script route only when `mode:"content"` comes back empty, errors,
or times out** — this is still the primary path for `.xlsx` (large workbooks in particular) and
may be needed for an attachment's nested URI if its content hasn't been extracted yet:

1. Call `x1_get_content` with `mode: "preview"` on the attachment/file URI. X1's own engine
   caches the real file and returns a **local file path** in the `preview` field (no bytes in
   context).
2. Extract the text locally from that path with the bundled script:
   `powershell -File <skill>/scripts/extract-office-text.ps1 -Path "<that path>"`
   It handles `.docx`, `.pptx` (per-slide), and `.xlsx`, and prints readable text.
3. Render that text as a composed-preview artifact (§5) the same way as a normal preview.

> If `mode: "preview"` times out for a local file, remember the `Files` URI **is** the path —
> strip the `file://` prefix and read it directly.

## Worked example — "the most recent pptx someone at work sent me"

> The examples below name `MSMail` as the mail table because it is the common case. Substitute
> whichever mail table is actually active per step 1 — for local Outlook that is `Email` with
> `source:outlook`, not `MSMail`.

1. `x1_search` table `MSMail`, query `att:pptx`, `displayFields ["subject","from","date","att"]`,
   a generous `limit` (e.g. 25). Returns the emails carrying a `.pptx`.
2. Read the `date` field of each, drop any from the user themselves or non-colleagues, and pick
   the **largest** `date` (most recent).
3. `x1_search` table `MSMail`, query `<deck name> type:pptx` to get the **attachment** item with
   its nested URI.
4. `x1_get_content` `mode: "content"` on that attachment URI → full per-slide text directly. Only
   if that comes back empty/errors, fall back to `mode: "preview"` → a local
   `...\\X1 Search\\...\\Deck.pptx` path → `extract-office-text.ps1 -Path "<path>"`.
5. Render the text as a composed-preview artifact (§5).

That whole flow runs in a few thousand tokens. Fetching the same 3 MB deck through a mail API as
base64 would be ~1.1M tokens and overflow context — which is exactly why x1 is the right tool.

## Example user stories

These are typical requests x1 handles well — use them as patterns when deciding which table and
query to use.

---

**"What's in the Cox error log analysis PDF I have saved?"**
`x1_search` → `Files`, query `analysis cox error logs type:pdf`. Take the top hit. Call
`x1_generate_preview` — PDFs render natively (formatted, reflowed text, `previewType: "pdf"`) —
and publish the returned `html`/`path` via the `Artifact` tool, same as any other doc.

---

**"Show me the last five emails from David about the Henderson project"**
`x1_search` → `MSMail`, query `Henderson from:David`, sort `date desc`, limit 5.
Display subject/date/from as a list. For whichever one the user picks, call `x1_generate_preview`
`maxChars=0` (full email), `output="file"` and render as an Artifact, or `output="inline"` only
if you need to summarise it.

---

**"Did anyone send me a PowerPoint about Q3 planning?"**
`x1_search` → `MSMail`, query `Q3 planning att:pptx`, `displayFields ["subject","from","date","att"]`.
Find the attachment item's URI (nested `msmail://.../<attachmentId>`). Call `x1_get_content`
`mode:"content"` directly to get full per-slide text; only fall back to `mode:"preview"` → local
path → `extract-office-text.ps1` if content mode is empty or errors. Render the text as an
artifact.

---

**"Find the budget spreadsheet I updated this week"**
`x1_search` → `Files`, query `budget type:xlsx`, sort `modified desc`. First result is
the most-recently-modified match. `x1_execute_action` with `open` to open it in Excel, or
`x1_get_content mode:"content"` to read its contents — fall back to `mode:"preview"` +
`extract-office-text.ps1` only if content mode is empty or errors (unlike `.pptx`, `.xlsx`
native content extraction is less consistently verified, so the script fallback matters more here).

---

**"What did the team decide in the Teams channel about the new auth design?"**
`x1_search` → `Teams`, query `auth design decision`. Preview the top hits with
`x1_generate_preview` `output="inline"` to read the messages and synthesise the decision.

---

**"Pull up the proposal document I was working on last week on OneDrive"**
`x1_search` → `OneDrive`, query `proposal`, sort `modified desc`, limit 10. Identify the
right one from subject/date. If `previewType` is `metadata_card` (cloud-only, not cached),
use the composed preview fallback: `x1_get_content mode:"content"` → format the extracted
text as a styled HTML artifact (provenance strip, section headings, document card) and
publish via the `Artifact` tool. Only fall back to `open_url` if content extraction also fails.

---

**"Show me the Air Quality report PDF — it's a large file"**
`x1_search` → `Files`, query `air quality report type:pdf`. Call `x1_generate_preview
output="file"` — the 1 MB embed cap only applies to images, not PDFs, so a large PDF still
renders fine as extracted, reflowed text (`previewType: "pdf"`). Publish the returned `path` as
an Artifact. Only fall back to `x1_get_content mode:"content"` + a hand-built composed preview
if `previewType` comes back `metadata_card` (extraction itself failed). The whole flow costs
~2,000–5,000 tokens vs ~500,000+ for base64 binary.

---

**"Search my files and email for anything about the Acme contract"**
One call: `x1_search` → `tables: ["Files", "MSMail"]`, query `Acme contract`. The bridge merges
both tables' hits into one response (with a `byTable` breakdown of each table's own counts).
Present a unified list — file hits and email hits together — before generating any preview.

---

**"Show me the image Stewart attached in that Gmail thread about the logo redesign"**
`x1_search` → `Gmail`, query `logo redesign att:png OR att:jpg`. Find the attachment URI,
call `x1_generate_preview` `output="file"` — the image embeds as a `data:` URI in the fragment;
render from path as an Artifact, zero bytes in context.

---

**"Save all my expense receipts from the last year to a folder"**
`x1_search` → `MSMail`, query `receipt OR invoice`, sort `date desc`, large limit. For each
result, call `x1_generate_preview` with `output="save"`. The fragments land in `Documents\X1 Saved\{date}\`
(or the configured company directory). Render each returned `path` as an Artifact. When done,
note that `manifest.json` in the save directory lists every saved file with title, date, and URI.

---

## Cost savings report

Call `x1_cost_savings` (no parameters) to get a report of accumulated token-cost statistics. The
bridge records every tool call — search, preview, get_content, execute_action — and compares the
actual tokens used against what equivalent direct Microsoft Graph / Gmail / OneDrive API calls
would have cost.

**When to use it:** any time the user asks about token savings, cost efficiency, ROI, or "how much
has x1 saved me?".

**What it returns:**
```json
{
  "recordingSince": "2026-06-28T10:00:00Z",
  "totalCalls": 45,
  "estimatedX1Tokens": 12340,
  "estimatedApiBaselineTokens": 890000,
  "estimatedTokensSaved": 877660,
  "estimatedSavingsFraction": 98.6,
  "estimatedCostSaving": "Estimated $2.63 at Claude Sonnet input pricing ($3/M tokens)",
  "avgDurationMs": 62,
  "totalBytesReturned": 153680,
  "breakdownByCategory": [
    { "category": "x1_generate_preview  pdf / output=file", "calls": 3, "tokensSaved": 750000, "avgDurationMs": 340, "bytesReturned": 320, "itemsReturned": 0 },
    ...
  ]
}
```

**`avgDurationMs`/`bytesReturned` (XS-1594)** — the bridge's own elapsed time per call (X1 WCF
round-trip + JSON formatting), not including any time Claude spends after the response comes
back. Useful for answering "is X1 slow, or is it something after the data comes back?" — a low
`avgDurationMs` with a slow-feeling interaction points at Claude-side processing. Every call also
writes a one-line `PERF tool=... elapsedMs=... bytes=... estTokens=...` entry to `X1McpBridge.log` for
per-call (not just aggregated) diagnosis.

**`x1_set_query_log` (XS-1578)** — off by default; the `PERF` line above never includes the actual
query text unless this is turned on. If the user asks to "turn on query logging" (e.g. while
diagnosing a slow/timed-out search), call `x1_set_query_log` with `enabled:true` — it appends the
query/filter text to that same `PERF` line, takes effect immediately (no restart), and is the
supported way to do this (not by hand-editing the registry). Call it with no arguments to just
report the current state. Turn it back off the same way when done, since it's a diagnostic aid, not
a standing setting.

**Rendering — always publish as an Artifact, never use `show_widget`.**

Write the dashboard HTML to a temp file then call the `Artifact` tool with that path. The
dashboard must include all of the following:

1. **Header** — title "X1 Token Savings", call count + recording-since date, "theoretical
   estimates" note, and a light/dark theme toggle button (default: dark; auto-detect OS pref).

2. **KPI row (3 cards)** — Tokens Saved (green, `estimatedTokensSaved`), API Baseline (orange,
   `estimatedApiBaselineTokens`), Actual x1 (blue, `estimatedX1Tokens`). Show raw numbers, not
   abbreviated. Sub-line on Saved card: the `estimatedCostSaving` string.

3. **Savings bar** — labelled "Context Efficiency", percentage from `estimatedSavingsFraction`,
   animated fill on load. Legend shows saved vs x1 used token counts. Bar fills to the savings
   fraction; a right-side sliver in accent color shows x1 tokens used.

4. **Breakdown table** — one row per category. Columns: Category (name + mode badge), Calls,
   Saved, Share (% of total saved), Reduction (× multiplier — estimate from known ratios:
   get_content≈220×, pdf/file≈2370×, docx/inline≈4×, docx/file≈4×, html/inline≈1.2×,
   html/file≈1.2×, metadata_card/file≈6×, metadata_card/inline≈1.3×, extract_file≈20×,
   search≈1.1×, get_metadata≈1×, text/inline≈1×; for zero-savings rows like `x1_cost_savings`,
   `x1_execute_action`, `x1_list_sources`, and tag ops, show "—" rather than 1×), Avg Time
   (`avgDurationMs`, formatted as "N ms" — this is the row's X1-side timing, distinct from the
   Reduction/token columns). These ratios are static estimates, not computed per-report — actual
   x1 tokens per category aren't broken out by the tool, so don't try to derive Reduction from
   `bytesReturned`, which is 0 for many categories (rows recorded before XS-1594 added byte
   tracking, or output=file modes that only return a path).

5. **Reset reminder row** — a styled "↺ Reset statistics" button that, when clicked, shows
   instructional text: tell Claude "reset my x1 token savings stats" — this calls `x1_reset_stats`
   (no parameters), which zeroes the accumulated counters.

6. **Methodology footnote** — explain each category's baseline calculation:
   - PDF/image file mode: bridge returns a path (~80 tokens); API baseline = base64 bytes ÷ 3.
   - DOCX inline: bridge extracts clean HTML text; API baseline ≈ 4× compressed binary size.
   - Metadata card file mode: bridge returns a small HTML card (~80 tokens); API = full body.
   - HTML email/search: both paths similar volume, ratio ≈ 1.
   - Pricing reference: Claude Sonnet input at $3.00/M tokens.

**Color scheme:** dark default — `--ground:#111318`, `--surface:#1B1E26`, `--saved:#43C880`
(green), `--baseline:#D97B3E` (orange), `--accent:#5B8DEF` (blue). Light mode swaps to
pale slate grounds with the same semantic hues darkened. Do NOT use the inline `show_widget`
tool for this — the user expects a hosted Artifact page they can share.

## Troubleshooting

Recognize these from the tool response itself and self-resolve rather than escalating to support
or giving up — this list grows as new recurring issues surface.

### Two connector processes disagree on a tool's contract

If a tool errors or behaves in a way that contradicts this skill's documented behavior (e.g. a
parameter this skill says is optional gets rejected as required), call `x1_version`. It reports
which build is actually answering — the version and path serving this session, the install
flavor, and every connector process running on the machine. Two connector processes disagreeing
on a tool's contract is a real failure mode this connector has hit before, not a hypothetical:
the shared relay listens on a fixed port regardless of which install started it, so a leftover
one serves normally rather than erroring. Check `runningBridges`/`runningDaemons` for more than
one version, and `mismatch` where present.

### Service host not running

**Symptom:** a tool call fails with a message like *"The X1 service may be unavailable - confirm
X1ServiceHost is running and retry"*, a raw transport error (mentions of named pipes, WCF
endpoints, `TimeoutException`, `CommunicationException`), or `x1_list_sources` comes back
completely empty (`{"sources":[]}`) rather than individual tables merely showing `itemCount: 0`.

**Fix:** tell the user X1 Search isn't running and to start it — X1ServiceHost (the background
indexing service every tool call depends on) launches automatically with X1 Search, so starting
the app is the whole fix. Retry the call once they confirm it's running.

### Search results look stale, or a source that should have data shows nothing

**Symptom:** a table the user expects to be indexed shows `itemCount: 0` in `x1_list_sources`, or
results are missing items the user knows exist.

**Fix:** check that source's `accounts[]` entry for `isScanning` and `lastScanTime` (both already
returned by `x1_list_sources`, alongside `itemCount`). If `isScanning: true`, a scan is already
running — tell the user to wait and try again shortly. Otherwise, tell the user to open X1 Search
and trigger a rescan of that source themselves — **this connector has no way to start or control a
scan**, so don't imply you're handling it; just point them to the right place.

### Not licensed for the MCP connector

**Symptom:** a tool response with `"status":"error"` and a message containing "isn't licensed for
the MCP connector", "not available on this license tier", or "requires the MCP-full license
entitlement".

**Fix:** this is a licensing wall, not a usage mistake — don't retry the call or troubleshoot it
as if different parameters would help. The message already includes the exact URL to send the
user to for upgrading/enabling the entitlement; relay it as a clickable link along with a plain
explanation of what's gated (the whole connector, or just the non-Files sources on a Files-only
tier).

## Quick reference

- **Always call `x1_list_sources` first.** Only search tables with an entry where `accounts[].itemCount > 0`; a named account is not required (`Files` has `accountName: null`). Tables where every entry is `itemCount == 0` are not configured — skip them.
- **Match the user's words to `displayName`, then pass that source's `schemas` to `tables`.** `name` is the scanner's label and is not always a table — the Gmail source is `name: "Modern Gmail"` but the searchable table is `Gmail`.
- **Never sum `totalCount` across a table's accounts** — it is scanner-wide and repeats the same number on each. Use `itemCount` for per-account figures.
- One table, or several in one call (`tables: ["Files","MSMail"]` merges both, with a `byTable` breakdown) — pick from the active tables identified by `x1_list_sources`. Multi-table calls run sequentially internally, so latency scales with table count (`limit`/`timeoutMs` apply per table).
- **Local Outlook / Windows Mail → `Email` with `source:outlook`**, not `MSMail` (that is the Microsoft 365 scanner). `Email` is shared with IMAP, so without `source:` the two come back mixed.
- File-type filtering → `type:ext` in the query (never the `filters` param).
- Emails with an attachment of a type → `att:ext`.
- **`field:value` in a `query` takes the `displayName`** (prefix-matched) — tag search is
  `Tags:isocert`, **not** `x1tag:isocert`, which silently returns 0 results rather than erroring.
  `filters` accepts either name; `displayFields`/`sort` take the internal `name`.
- **Counting, "last N", or listing for the user → `progenitorSearch: true`**, or attachments
  appear as duplicate rows of their parent email. Tagging a parent also tags its attachments.
  **Always pair it with an explicit `sort`** — `progenitorSearch: true` defaults to oldest-first,
  so an unsorted "last N" silently returns the oldest N.
- Sorting: pass `sort: [{column:"date", direction:"desc"}]` — the bridge enforces the order
  reliably (OA serial dates; higher = newer).
- Don't call `x1_list_actions` — actions are inline on every result.
- Preview output mode — **default `output="file"`**, render Artifact from the returned `path`
  (zero bytes in context). Only use `output="inline"` when you must read the content to answer
  (summarise, quote, compare) — and even then render the result as an Artifact, not in chat.
  Use `output="save"` when the user explicitly wants to archive or keep the item persistently.
  Also pass `maxChars` — omit or `0` for the full document, `4000-8000` for a readable
  summary (~2-3 pages).
- Images (png/jpg/gif/webp/bmp/svg) embed as `data:` URIs and render correctly as `<img>`
  (only images are subject to the 1 MB embed cap).
- **PDFs render natively, same as docx** — `x1_generate_preview` extracts and reflows PDF text
  into a formatted article (`previewType: "pdf"`); it never embeds raw bytes, so there's no
  Artifact-sandbox blank-page issue and no size cap. Just render the result directly; no manual
  composed-preview fallback needed.
- `metadata_card` = fallback (cloud-only or extraction failed), not the document body.
  **When the user wants content:** composed preview pattern — `x1_get_content
  mode:"content"` → format extracted text as a styled HTML Artifact (provenance strip,
  section labels, document card, palette grounded in the subject).
- **Extracting text — pick by whether the item is indexed:** `x1_get_content mode:"content"`
  requires an indexed `table`+`uri` (from `x1_search`) and fails outright on anything not in
  the index. `x1_extract_file` works on **any** local path regardless of index status — use it
  directly whenever the user gives you a raw path you haven't confirmed is indexed, not only as
  a fallback. `"auto"` mode on `x1_get_content` falls back to preview, not to `x1_extract_file`.
  Never use `"extract"` as a mode (invalid). `"preview"` returns only a local file path.
  `"internal"` returns index metadata only.
- **`x1_extract_file`:** pass the full local path in the `file` parameter — indexed or not.
  **When writing extracted content to disk:** ask the user where to save it (once per session),
  remember the answer, and reuse it for all subsequent saves without asking again. See the
  save-directory protocol in §6.
- **`x1_export_html`:** native HTML export (table+uri or file) — preserves formatting/tables and
  may emit sibling images into the returned `assetFolder`. Prefer over `x1_get_content` when
  the user wants a faithful visual rendering, not flattened text.
- `.pptx` content → try `x1_get_content mode:"content"` first (verified 2026-07-16 to work
  directly, same as `.docx` — no local cache round-trip needed). `.xlsx` content → same, but the
  script fallback is more likely to be needed. Fall back to `mode:"preview"` (cached local path)
  → `extract-office-text.ps1` only when `mode:"content"` is empty, errors, or times out.
- Prefer x1 for attachments/large files: it returns paths, not megabytes of base64.
- **Tagging:** `x1_add_tags` / `x1_remove_tags` take **positional** arrays — `uris[i]` gets `tags[i]` and the two arrays **must be the same length**. To apply one tag to N items, pass N copies of the tag string. Never pass a single-element `tags` array for multiple URIs — it will error. `x1_clear_tags` takes only `uris` (no `tags`) and removes all tags from those items.
