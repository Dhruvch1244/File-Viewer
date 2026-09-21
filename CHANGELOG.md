# Changelog

Notable changes per release. Versions follow [semantic versioning](https://semver.org); the release
workflow publishes a `win-x64` build when a `v*.*.*` tag is pushed.

This file starts at 0.1.5; releases 0.0.1 through 0.1.4 predate it and have no entries here.

## 0.1.8

### Fixed

- **Exporting "every section" of a bulk file could silently drop rows.** Whichever sections had
  already been opened and browsed exported through that grid's *current* filtered view, while
  sections never opened exported in full — so a filter left active while looking at one section
  (even one long since forgotten about) meant that section came out short, with no warning, the
  next time "every section" was exported. Every section now always exports every data record,
  regardless of any filter active on its grid; the filtered subset of what's on screen is still
  available via the existing "rows in view" scope.
- **Indexing a large bulk file could throw and fail to open at all.** A line-ending probe narrowed
  the whole file's length to a 32-bit integer before checking it was small — any bulk file over
  roughly 2.1 GB hit an `OverflowException` and never opened, rather than just taking a while.
- **The value-filter popup's search box and Select all/Deselect all, dropped by an earlier merge,
  and the value checklist's missing UI virtualization** are both restored/fixed as of the previous
  release's last commit but hadn't been written up here yet — included for the record.

### Changed

- **Adding, duplicating, deleting or undoing a row now shows what's happening.** These ran their
  row-list refresh synchronously on the UI thread with no visual feedback — on a large, filtered
  file this could freeze the window for the whole recompute with nothing telling you why. They now
  run the same background-recompute path search and column filters already used, and the status
  line names the actual operation ("Deleting…", "Undoing…", "Adding row…", …) instead of always
  reading "Filtering…".
- **Indexing a large file is significantly faster.** Profiling a 71-million-row/3.2 GB synthetic
  file found the final step — merging the parallel-scanned chunks into the row index — running
  single-threaded and taking roughly two-thirds of total index time. Chunk row counts are known
  ahead of the merge (a cheap prefix sum), so that merge is now split across chunks and run in
  parallel the same way the scan itself already is: ~9s down to ~1.3s on a 4-core machine for that
  file.
- **`RowResolver.Resolve` no longer copies a row's decoded fields unless it actually has an edit to
  apply** — the shared row-resolution path behind grid rendering, export, and the filter/sort
  engine's decode path, called on the order of millions of times for a large file. Lazy
  copy-on-write instead of an unconditional array copy measured ~5% faster and ~14% less GC
  pressure on that path.

## 0.1.7

### Fixed

- **The column filter's value list could be squeezed down to one visible row, unscrollable in any
  useful way.** It sat as the popup's last, leftover-space element behind a title, mode buttons, a
  sort/match-by-text block and — for any column with more distinct values than the lookup collects,
  which is routine for a price or timestamp column — a 3-4 line truncation notice. Whatever survived
  that queue is what the list got, and for a real column it was often nothing. The whole "pick from
  values" group (search box, select/deselect, the list) now scrolls as one unit instead of the list
  alone competing for scraps, the popup itself is taller and a little wider, and the truncation
  notice is one line with the rest in its tooltip instead of a paragraph.
- **The app icon was soft and asymmetric at the sizes Windows actually shows it** (title bar,
  taskbar, Alt-Tab) — a single large image resized down rather than rendered per size, and its two
  bars were different widths to begin with. Redrawn at each of the eight sizes Windows uses
  (16–256px) rather than scaled from one, in the app's own accent green instead of black.

### Changed

- **Copy can take the whole record, not just what's on screen.** A file can declare far more columns
  than the default 20 shown at once, and Copy used to silently drop every hidden one with no way to
  get them back short of unhiding each first — while Export, all along, already wrote every column
  regardless of visibility. The Copy panel now offers both: visible columns (unchanged default) or
  every column the row declares, the same full record Export writes.
- **Copy gained JSON**, alongside tab-separated and CSV — a JSON array of objects, column name to
  value, written with the same escaping `JsonExporter` writes files with, so a copy and an export of
  the same rows agree byte for byte on the values.
- **The Copy dropdown is a form now, not a list of preset sentences.** Four named presets ("Selected
  rows as CSV", "Selected rows, no headers", ...) only ever covered part of what became six
  independent choices once columns and JSON joined rows and format — the rest would have meant an
  ever-longer list of increasingly specific descriptions. Three small toggles (rows / columns /
  format) plus a headers checkbox replace it; the plain Copy button is unchanged.
- Every small all-caps section label ("FILTERED BY", "MATCH BY TEXT", "RECENT FILES", ...)
  previously hand-set its own font size and colour at each call site; consolidated into one style,
  nudged very slightly larger for legibility.

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
