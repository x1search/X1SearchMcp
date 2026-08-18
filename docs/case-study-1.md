case study 1

prompt:
please use x1 to find the most recent pptx I have received from anyone else in x1 and show me a preview

analysis:
Good question — let me ground it in real numbers. First, the size of the attachment, since that's the crux of the comparison:

Here's the comparison, grounded in the actual artifact (the 3.17 MB deck):

## The decisive factor: how the attachment reaches the model

The whole cost difference comes down to one thing — **does the 3.17 MB file pass through the model's context, or not?**

- **x1-search:** `x1_get_content` returned a **local file path** (~70 tokens). X1 had already extracted/cached the attachment, so the bytes never entered context. I then read the slide text locally.
- **The normal path:** the conventional way to "preview an email attachment" is to fetch it through the email API (Microsoft Graph, which the managed Outlook connector and x1mcpoutlook sit on top of). Graph's attachment endpoint returns `contentBytes` as **base64 inline**. For this file that's:

| | bytes | as tokens |
|---|---|---|
| pptx file | 3,324,497 | — |
| **base64 inline** (Graph `contentBytes`) | 4,432,664 chars | **~1,108,000 tokens** |
| extracted slide text (what you actually need) | 4,765 chars | ~1,200 tokens |

~1.1M tokens **exceeds the context window outright** — so the naive normal path doesn't just cost more, it *can't complete*. (And the read-only managed M365 connector may not expose attachment download at all, so it couldn't preview the deck regardless.)

## Token tally for this task

| Step | x1-search (measured-ish) | Normal path (est.) |
|---|---|---|
| Find pptx emails from a colleague | ~2,400 (one targeted `att:pptx` search, 13 hits) | ~3,000–8,000 (attachment type isn't natively searchable, so list + inspect many messages) |
| Pin down the attachment item | ~200 (`type:pptx`) | included above |
| Get the file for preview | ~100 (**returns a path**) | **~1,100,000 (base64)** → over context, or save-to-disk if the connector supports it |
| Extract slide text locally | ~1,200 | ~1,200 (only if save-to-disk worked) |
| **Total** | **~3,900** (the one redundant `generate_preview` envelope call added ~900 more) | **infeasible**, or **~5,000–10,000** best-case with a disk-saving connector |

## Why x1-search wins here
1. **Attachments are first-class indexed items** — I found "most recent pptx from an x1 colleague" with a single `att:pptx` search, rather than listing inboxes and opening messages to inspect attachments.
2. **It hands back a path, not bytes** — the 3.17 MB never touches context. That's the ~1.1M-token saving and the difference between "works" and "can't."

## Fair caveats
- The x1-search numbers are measured from this session; the normal-path numbers are estimates with the stated assumptions (Graph-style base64 retrieval, less-targeted search).
- For a *small* attachment (say <50 KB), the gap shrinks — base64 would be ~15–20K tokens, costly but not fatal. The advantage scales with file size, and decks/PDFs are usually large.
- One honest asterisk: in this run x1-search spent ~900 tokens on a `generate_preview` call that returned the email envelope instead of slides (the connector doesn't extract `.pptx` yet). Adding native `.pptx` extraction — which I offered — would make the x1 path both cheaper and a single clean `x1_generate_preview` call.

Bottom line: for "find and preview a received attachment," x1-search ran at **~4K tokens and succeeded**, where the conventional route's attachment fetch alone is **~1.1M tokens** — roughly **200× the entire x1-search flow** — and typically overflows context unless a special save-to-disk path exists.
