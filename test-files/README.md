# Sample files

One sample for every shape of file the viewer handles, so you can exercise a feature without
hunting for a real Bloomberg export. Open one with **Ctrl+O**, drag it onto the window, or pass it
on the command line:

```
FileViewer.exe test-files/30-bulk-named-sections.dif
```

They're small enough to read in a text editor, which is the point — when something looks wrong in
the grid you can see what the file actually says.

`generate.py` rewrites the whole set. You don't need to run it to use them; run it to change one,
add another, or make a bigger performance sample:

```
python3 test-files/generate.py
python3 test-files/generate.py --perf-rows 2000000
```

All of these are marked binary in `.gitattributes`. A sample that exists to prove CRLF handling is
worthless if git converts it on checkout.

## Header dialects

| File | What it covers |
| --- | --- |
| `01-classic-inahdr.dif` | `INAHDR`/`INATRL`, with `_ID` and `_ERR` declared in `START-OF-FIELDS`. Because the list already starts with `_ID`, no implicit prefix is added. Includes an error row (`_ERR=10`, values blank). |
| `02-getdata-imahdr.dif` | `IMAHDR`/`IMATRL` plus `START-OF-FILE`/`END-OF-FILE`, and the implicit `_ID`/`_ERR`/`_SIZE` prefix that getdata rows carry whether or not they're declared. Also puts `TIMESTARTED=` between `END-OF-FIELDS` and `START-OF-DATA`, which the parser skips rather than demanding the two markers be adjacent. |
| `03-fields-inline-data-attribute.dif` | The section name carried on the `START-OF-FIELDS` line itself (`START-OF-FIELDS DATA=PX_LAST_HIST`) instead of on its own `DATA=` line. |

Both marker spellings round-trip: export to DIF and the file comes back with the marker it started
with, not a normalized one.

## Delimiters

The delimiter is declared per file and never guessed.

| File | Declared |
| --- | --- |
| `10-delimiter-comma.dif` | `DELIMITER=,` |
| `11-delimiter-tab.dif` | `DELIMITER=` + a literal tab |
| `12-delimiter-semicolon.dif` | `DELIMITER=;` |
| `13-delimiter-missing.dif` | No `DELIMITER` line at all — falls back to `\|` and records a warning. Open **File info** to read it. |

## Line endings

| File | What it covers |
| --- | --- |
| `20-lineendings-lf.dif` | LF throughout. |
| `21-lineendings-crlf.dif` | CRLF throughout. |
| `22-lineendings-mixed.dif` | CRLF and LF alternating. The file's line ending is taken from the first one, so this opens as CRLF with LF lines inside it. |

Worth doing with these: open one, edit a cell, export as DIF, and compare bytes with the original.
The export writes rows with the source's line ending, so a CRLF file stays CRLF.

## Bulk (multi-section) files

There are two different shapes a "bulk" DIF file takes, and both are handled — the difference is
where the envelope boundary falls.

**One outer envelope, several inner field blocks** — a bulk export repeats the whole
`START-OF-FIELDS` … `END-OF-DATA` block once per requested field, all inside a single
`INAHDR`/`INATRL` (or `IMAHDR`/`IMATRL`) pair.

| File | What it covers |
| --- | --- |
| `30-bulk-named-sections.dif` | Three `DATA=`-named sections (`DVD_HIST`, `CALL_SCHEDULE`, `INDX_MEMBERS`) with different field lists and row counts. |
| `31-bulk-many-sections.dif` | Eight sections, 3 to 10 rows each. Enough to see the section bar fill up and to feel per-section indexing. |
| `32-bulk-unnamed-sections.dif` | Sections with no `DATA=` attribute — names are synthesized (`Section 1`, `Section 2`) so they're still tellable apart. |
| `33-multi-section-plain-name.dif` | Multi-section, but nothing in the file name says "bulk". Detection has to come from the content, and does. |

**Several whole file envelopes concatenated** — no single outer header/trailer at all. Each section
is its own complete `START-OF-FILE` … `DATA=` … `START-OF-FIELDS` … `END-OF-DATA` … `END-OF-FILE`
envelope, one after another in the same file, the way some Bloomberg DL deliveries actually arrive.

| File | What it covers |
| --- | --- |
| `34-bulk-concatenated-envelopes.dif` | Three whole-file envelopes (`CALL_SCHEDULE`, `DVD_HIST`, `INDX_MEMBERS`), wrapped in one file-level `IMAHDR`/`IMATRL` pair. Each envelope's own `DATA=` name and `DATARECORDS=` count are kept — not swallowed into a later envelope's or the file trailer's. |
| `35-bulk-envelopes-no-header-marker.dif` | The same three envelopes with no file-level `IMAHDR` at all — the file's first line is `START-OF-FILE` itself. Legal, and opens the same way. |

Both shapes open identically in the app: one tab, a section bar naming every section, each section
indexed the first time it's clicked. **Open all in tabs**, next to the section bar, splits a bulk
tab into one tab per section — any edits already made carry over with the section rather than being
discarded, since it's the same underlying session that moves, not a fresh read of the file.

Try **Export → all sections** on `30`, `31` or `34`: one output file per section, each named after it.

## Column shapes

| File | What it covers |
| --- | --- |
| `40-datatypes-mixed.dif` | One column of each kind the column menu has to cope with: integers, decimals, negatives, an integer too wide to survive being treated as a float, `yyyymmdd` dates, free text containing near-miss delimiters and quotes, a column of repeated values for the distinct-value picker, and blanks. Point **Column stats** at each one. |
| `41-unicode-and-blanks.dif` | Japanese, Korean, Cyrillic and accented Latin company names, plus an `_ERR=10` row with every value blank. Search these with case sensitivity on and off — the raw-byte prefilter only folds ASCII case, so these rows prove the decoded path still matches. |

## Scale

| File | What it covers |
| --- | --- |
| `50-perf-25k-rows.dif` | 25,000 rows, ~2 MB. Big enough that paging, search, sort and column stats are doing real work; small enough to keep in git. Eight low-cardinality columns, so the distinct-value picker and match-any searches have something to chew on. |

For a real workout, generate something in the millions — the app is built for 2 GB files and 25,000
rows won't show you that:

```
python3 test-files/generate.py --perf-rows 2000000     # ~170 MB
```

## Malformed and edge cases

None of these may crash the app. Each either opens with a diagnostic in **File info**, or is
refused with a clear reason.

| File | What happens |
| --- | --- |
| `60-edge-empty-data.dif` | A section that returned nothing. Valid, not an error — the grid is simply empty. |
| `61-edge-datarecords-mismatch.dif` | Trailer says `DATARECORDS=99`; there are 3 rows. The actual rows win. **No warning is raised** — `DATARECORDS` is informational, and a mismatch is never flagged. |
| `62-edge-ragged-rows.dif` | Rows with too few and too many values for the declared field list. Both are indexed and shown as they decode. **No warning here either**: a row's field count is deliberately never validated against the header, so nothing is dropped or judged "malformed". |
| `63-edge-missing-end-of-data.dif` | No `END-OF-DATA` and no trailer. The data section is treated as running to end of file; two warnings say so. |
| `64-edge-truncated-mid-row.dif` | Cut off mid-row, as a dead transfer would leave it. The partial last row is kept and shown as far as it goes. |
| `65-edge-marker-trailing-content.dif` | `END-OF-DATA    (3 records returned)` — a marker with trailing content is still a marker, not a data row. |
| `66-edge-not-a-dif-file.bin` | 4 KB of random bytes. Refused with "File does not start with 'INAHDR', 'IMAHDR' or 'START-OF-FILE'" rather than a crash or a garbage grid. |

The two "no warning" rows above are deliberate product behaviour, not gaps: the viewer displays
records rather than validating them. `tests/FileViewer.Core.Tests` pins both.
