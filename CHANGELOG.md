# Changelog

Notable changes per release. Versions follow [semantic versioning](https://semver.org); the release
workflow publishes a `win-x64` build when a `v*.*.*` tag is pushed.

This file starts at 0.1.5; releases 0.0.1 through 0.1.4 predate it and have no entries here.

## 0.1.6

### Fixed

- **Data columns rendered at zero width.** Every file opened to rows with no data columns visible
  at all — only the fixed select/view/delete columns showed, headers included. Column
  virtualization measured an auto-sized column against cells that did not exist yet and it settled
  at zero width, taking its header with it. Columns now have an explicit default width and column
  virtualization is off (row virtualization, which is what matters at millions of rows, is
  untouched).
- **"Fit to window" did nothing until the next resize.** Picking it from the rows-per-page menu left
  the grid on whatever page size it had before, until the window was manually resized. It now
  re-measures as soon as it's picked.
- **The filtering indicator stuck on the welcome screen.** With no file open, "Filtering…" and a
  Stop button sat at the bottom of the window regardless — a failed binding falling back to its
  default of visible, not an active filter. The whole paging/status bar now hides with the rest of
  the grid until a file is actually open.
- **Bulk files delivered as concatenated whole-file envelopes lost every section name after the
  first**, and a delivery with no outer `INAHDR`/`IMAHDR` at all was rejected outright. Some
  Bloomberg deliveries are not one envelope with several inner `START-OF-FIELDS` blocks but several
  complete envelopes — each its own `START-OF-FILE` … `DATA=<name>` … `END-OF-FILE` — laid end to
  end with no single outer header. Both shapes now parse correctly, sections keep their own
  `DATA=` name and `DATARECORDS=` count, and a file that opens with `START-OF-FILE` rather than a
  header marker is accepted.

### Changed

- **Copy confirms itself.** Copying showed nothing except a line in the status bar, out of sight of
  wherever you'd just pressed Ctrl+C. A toast now appears over the grid ("Copied 1,240 rows to
  clipboard.") and fades after a couple of seconds; the status bar still gets the same text.
- **Copy has variants**, behind a caret next to the Copy button: without headers, as CSV (quoted the
  same way the CSV exporter writes files), or every row the current filters leave in view — across
  all pages, not just the ticked rows. The default Copy button is unchanged.
- **The column filter popup explains itself.** Its two ways of filtering a column — matching by text
  and picking from a list of values — were stacked with no labels, reading as one confusing form.
  They're now headed "MATCH BY TEXT" and "OR PICK FROM VALUES", both boxes have placeholders, and
  the value list's "Clear" (which already meant "deselect everything") is now "Deselect all" so it
  isn't read as "clear the filter" next to the sort row's own "Clear".
- **A bulk file's section bar can split into tabs.** "Open all in tabs" gives every section its own
  tab at once, instead of clicking through the section bar one at a time. Any indexing and edits
  already done carry over — the same session moves to the new tab rather than the file being
  re-read — so nothing is lost or repeated by splitting.
- Ten more sample files in `test-files/`, covering both bulk shapes above.

## 0.1.5

The app opens, browses, edits and exports Bloomberg DIF/GETDATA files up to 2 GB, keeping the whole
file off the managed heap: an unmanaged row index, an LRU decoded-row cache, and a grid that only
ever builds the rows on screen. Everything below arrived in this release.

### Reading files

- **Bulk (multi-section) files.** A bulk export repeats the whole `START-OF-FIELDS` … `END-OF-DATA`
  block once per requested field, each block naming itself with a `DATA=<something>` attribute and
  declaring its own columns. Every section shows in a section bar as its own table. Detection is by
  content as well as by the "bulk" naming convention, so a bulk file that isn't named like one still
  opens. Sections are indexed the first time they're opened, so a ten-section file costs one
  structural scan plus the sections you actually look at.
- **Several files open at once, as tabs**, each with its own rows, edits, sort, filters and column
  layout; plus a second window for comparing files side by side.
- Opens from drag-and-drop, the command line, the recent-files list, or Ctrl+O.

### Finding things

- **Search with as many terms as you need** — each with its own scope (all columns or one),
  comparison (contains / does not contain / is / is not / starts with / ends with / regex) and case
  sensitivity, combined with match-all or match-any. Every term is a chip you can remove on its own.
- **Filter or highlight.** Filtering hides what doesn't match; highlighting keeps every row and marks
  the matching cells, with a match count and F3 / Shift+F3 to step through them. Matches in columns
  you've hidden are called out rather than left invisible.
- **Column menu** per column: sort either way, filter by text or regex, pick from the values actually
  present, or read a summary of what the column holds (blanks, distinct count, extremes, sum, mean).

### Working with rows

- **Inline editing with full undo**, cell edits included, reversed in the order you made them. Edits
  are an overlay on the original file — the source is never written to — and closing a file with
  edits that haven't been exported asks first.
- **Select at scale**: "select all" spans every row matching the current filters and is stored as a
  rule, not a list, so selecting millions costs nothing.
- **Extract** pulls the selected rows into their own view — same file, same columns, same edits, just
  those rows, with its own filters and sort.
- **Copy** the selection as tab-separated text, ready to paste into a spreadsheet.
- **Rows per page** from 50 up to *All rows*, which turns paging off and scrolls the whole result
  set.

### Getting data out

- Export to **DIF, CSV, TSV or JSON** with a live preview. Writes the rows in view (filters and sort
  applied), the ticked rows only, or every section of a bulk file as one file each. DIF export
  reproduces the source's header and trailer bytes verbatim, so anything the parser doesn't model
  survives a round trip.
- Output file names follow the Bloomberg convention — `{name}.{format}.{yyyyMMdd}`.

### Throughout

- Opening, filtering and exporting all report progress and can be stopped.
- A **File info** panel shows header and trailer metadata, the delimiter, the current section, and
  every parser warning with its byte offset.
- Light and dark themes, remembered along with your recent files and rows-per-page choice.
- Malformed files fail gracefully with diagnostics rather than crashing; the app never rejects rows
  for looking "wrong".

### Performance

Search, sort, filter and column statistics share one scan engine: every active filter is compiled
into a single predicate set and evaluated in one parallel pass, plain substring terms are answered
against raw bytes before a row is ever decoded, and rows are read in 1 MB runs when the scan is in
file order. Measured on a synthetic 82 MB file (2,000,000 rows, 4 cores) against the same operations
before that work:

| Operation                                      |   Before |   After |
| ---------------------------------------------- | -------: | ------: |
| Search all columns for a substring              |  1827 ms |   79 ms |
| Search one named column                         |  1775 ms |  151 ms |
| Search term + two column filters                |  2104 ms |  110 ms |
| Sort by an arbitrary column                     | 10539 ms | 1256 ms |
| Distinct values of a column                     |  1767 ms |  509 ms |
