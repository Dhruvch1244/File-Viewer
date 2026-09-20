# Changelog

Notable changes per release. Versions follow [semantic versioning](https://semver.org); the release
workflow publishes a `win-x64` build when a `v*.*.*` tag is pushed.

This file starts at 0.1.5; releases 0.0.1 through 0.1.4 predate it and have no entries here.

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
