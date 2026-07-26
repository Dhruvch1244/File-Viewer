# Bloomberg File Viewer — Product Requirements Specification (v2.1)

Version: 2.1
Status: Draft
Author: Engineering Team
Supersedes: v1.0 (Java Spring Boot / web), v2.0 (native C++)

---

# 0. Git Repo Location

https://github.com/Dhruvch1244/File-Viewer.git

# 1. Overview

## Project Name

**Bloomberg File Viewer** (Native .NET)

## Description

Bloomberg File Viewer is a **native Windows desktop application built on .NET 8 (C#)** for
viewing and editing Bloomberg DIF (GETDATA) files up to **2 GB**, targeting the same
performance goals as a C++ build (instant load, 60 fps scroll/sort/filter, low memory
footprint) while getting faster development, easier maintenance, and a mature UI toolkit.

Validation and Amazon S3 integration remain **deferred to Phase 2** (Section 4).

---

# 2. Goals

- Open and index a 2 GB DIF file in a few seconds.
- Scroll, sort, filter, and search at 60 fps regardless of row count.
- Keep resident memory proportional to **edits made**, not file size.
- Never fully parse the file into per-cell managed objects — parse lazily, on demand.
- Avoid GC-driven stutter at 2 GB scale (the main risk moving from C++ to .NET — addressed
  explicitly in Section 6).
- Ship as a self-contained Windows executable (no separately-installed runtime required).

---

# 3. Scope (unchanged from v2.0)

## Included

- Memory-mapped file open (up to 2 GB), background indexing.
- Lazy per-row DIF parsing (INAHDR, START-OF-FIELDS, START-OF-DATA, INATRL, metadata, trailer).
- Virtualized data grid: sort, filter, search, resize/reorder/hide columns, frozen columns.
- Inline cell edit, add/delete/duplicate row, bulk delete, undo — via edit overlay.
- Streaming export: DIF, CSV, TSV, JSON, with 2-row preview.

## Deferred to Phase 2

- Field validation (`_ID`, `_ERR`, `FUT_CONT_SIZE`/`QUOTE_UNITS`)
- Amazon S3 integration
- Auth, multi-user, audit trail (unchanged out-of-scope)

Extension points (`IValidator`, `IStorageProvider`) are reserved as interfaces so Phase 2 slots
in without a rewrite (Section 6.7).

---

# 4. Why .NET Instead of C++

| Consideration                             | C++                                            | .NET 8 (C#)                                                                           |
| ----------------------------------------- | ---------------------------------------------- | ------------------------------------------------------------------------------------- |
| Raw perf ceiling                          | Highest                                        | Very high, ~90-95% of C++ achievable with careful design                              |
| Dev speed / maintainability               | Slower, more manual memory/lifetime management | Faster, safer, larger hiring pool                                                     |
| Memory-mapped file support                | Manual Win32 API                               | Built-in `System.IO.MemoryMappedFiles`, equally capable                               |
| UI toolkit maturity for a data-heavy grid | Must build custom Direct2D grid                | WPF/WinUI 3 have mature virtualization primitives to build on                         |
| Main risk                                 | None specific                                  | **GC pauses/pressure at 2 GB scale** — must be actively designed around (Section 6.2) |

Net: .NET is the right call here as long as the design keeps the large, hot-path data
structures (the row index, the decode cache) **off the managed GC heap** or otherwise
GC-friendly. That constraint shapes Section 6 below.

---

# 5. Target Users (unchanged)

- **Fixed Income Analyst** — open, review, edit, export.
- **Data Engineer** — prepares large files for downstream ingestion; needs no hangs at 2 GB.
- **Operations Team** — Phase 2, once S3 lands.

---

# 6. Architecture

## 6.1 Stack

- **Language/runtime:** C# on .NET 8, published **self-contained / AOT or ReadyToRun** to
  avoid a separate runtime install and to cut JIT warm-up time.
- **UI:** WPF (recommended default) with UI virtualization (`VirtualizingPanel`,
  `VirtualizationMode.Recycling`) as the base, replaced with a fully custom-drawn virtualized
  grid (e.g. via SkiaSharp) only if profiling shows the built-in `DataGrid` isn't enough at
  2 GB scale. WinUI 3 is a viable alternative if a modern Fluent look matters more than the
  ecosystem maturity WPF currently has for this kind of dense data grid.
- **File I/O:** `System.IO.MemoryMappedFiles.MemoryMappedFile` mapped read-only over the whole
  file (2 GB fits comfortably in 64-bit address space, no windowing needed).
- **Low-level access:** `MemoryMappedViewAccessor.SafeMemoryMappedViewHandle` +
  `unsafe`/`Span<byte>` access to read directly from the mapped region without copying into
  managed byte arrays.

## 6.2 Keeping the GC Out of the Hot Path (the .NET-specific risk)

This is the one thing that doesn't exist in the C++ design and must be handled deliberately:

- **Row index (offsets/lengths) and sort keys:** store as arrays of `struct` (not classes) —
  ideally in a `NativeMemory`-allocated unmanaged block (via `NativeMemory.Alloc` /
  `MemoryMarshal`) rather than a managed array, so a multi-million-row index never lands on the
  GC heap or triggers Gen2/LOH collections as it grows.
- **Decoded row cache (for visible + scroll-ahead rows):** small, fixed-size LRU cache of
  decoded row objects — bounded so it never grows unbounded and never approaches LOH
  thresholds (85 KB+ single objects).
- **Parsing:** use `ReadOnlySpan<byte>`/`ReadOnlySpan<char>` throughout field extraction;
  allocate a `string` only for the handful of fields actually shown/edited for a row, never
  for the whole row up front.
- **GC mode:** run Server GC with concurrent/background collection enabled; validate with a
  profiler (dotnet-trace / PerfView) under a 2 GB synthetic file before calling this done.

## 6.3 File Loading & Indexing

1. Open a read-only `MemoryMappedFile` over the source file.
2. Background `Task`s scan the mapped view in parallel chunks to find record boundaries
   (INAHDR / fields / data rows / INATRL), writing `{offset, length}` into the unmanaged index
   array described above.
3. Extract lightweight sort/filter keys (e.g. `_ID`) per row into a parallel unmanaged array in
   the same pass, so later sort/filter never re-touches the raw file for those operations.
4. No full row parsing happens at this stage.

## 6.4 Rendering (Virtualized Grid)

- Only visible rows + a small scroll-ahead cache are ever decoded into managed row objects.
- WPF's UI virtualization handles container recycling; the app supplies decoded row data
  on demand as the virtualization panel requests items by index.
- Target: sustained 60 fps scroll independent of file/row count, validated by profiling, not
  assumption — WPF virtualization needs to be explicitly tuned (not left at defaults) to hit
  this at the row counts a 2 GB file implies.

## 6.5 Sorting & Filtering

- Operates on the unmanaged index/key arrays (Section 6.2), not on decoded row objects —
  sorting is a permutation of offsets, never a re-parse or re-copy of file content.

## 6.6 Editing Model (Overlay, Not Mutation)

- Same overlay design as v2.0: cell edits and row operations (add/delete/duplicate/restore)
  stored in a dictionary/list keyed by row index, applied on top of the base index at
  render/export time.
- The overlay is expected to be small relative to file size (bounded by how much a user
  actually edits), so it's fine as ordinary managed collections (`Dictionary`, `List`) — this
  is the one place regular GC-tracked memory is the right call.
- Base memory-mapped file is never mutated; undo pops the operation log.

## 6.7 Export

- Streaming writer iterates (index + overlay) in order, writing each resolved row as it goes
  via buffered `Stream` writes — no full-file buffer in memory.
- Preview resolves just the first two rows.

## 6.8 Extension Points Reserved for Phase 2

- `IValidator` interface hooking into the same per-row decode path used by rendering/export.
- `IStorageProvider` interface for S3 search/upload/download, so the core app carries no AWS
  SDK dependency until Phase 2.

---

# 7. Functional Requirements

Same as v2.0 (FR-1 through FR-8: open file, indexing, grid display, row operations, cell
editing, export, in-memory session state, logging) — unchanged by the language switch, since
the design and API surface described in Section 6 don't otherwise differ from the C++ version.

---

# 8. Non-Functional Requirements

## Performance (same targets as v2.0)

| Operation                          | Target                                                  |
| ---------------------------------- | ------------------------------------------------------- |
| Index a 2 GB file                  | A few seconds, bounded by disk sequential read speed    |
| Scroll (any file size)             | 60 fps / ≤16 ms per frame                               |
| Cell edit commit                   | <1 ms                                                   |
| Sort/filter re-apply at 2 GB scale | Low single-digit seconds                                |
| Export                             | Bounded by disk sequential write speed, constant memory |

## Memory

- Index + sort keys: small single-digit percentage of file size, held off the GC heap
  (Section 6.2).
- Decode cache: fixed, bounded size regardless of file size.
- Overlay: scales only with actual edits made.
- **New for .NET:** no Gen2/LOH collection should be triggered by normal scrolling/sorting —
  this must be verified with a memory profiler as an explicit acceptance check, not assumed.

## Reliability

- Malformed/truncated files fail gracefully, never crash the process.
- Source file is never mutated (read-only mapping + overlay model make this true by
  construction).

## Platform

- Windows 10/11, 64-bit.
- Published self-contained (or with ReadyToRun/NativeAOT) so no separate .NET runtime install
  is required on target machines.

---

# 9. Data Model

## Unmanaged Row Index

```csharp
[StructLayout(LayoutKind.Sequential)]
struct RowIndexEntry
{
    public long Offset;
    public int Length;
}

// Backed by NativeMemory.Alloc, not a managed array/List<T>,
// so a multi-million-row index doesn't live on the GC heap.
unsafe RowIndexEntry* _rowIndex;
int _rowCount;

unsafe SortKey* _sortKeys; // parallel array, extracted during indexing
```

## Edit Overlay (ordinary managed collections — small by design)

```csharp
record CellEdit(long RowIndex, string Column, string NewValue);

enum RowOpType { Add, Delete, Duplicate, Restore }
record RowOp(RowOpType Type, long RowIndex);

class EditOverlay
{
    public Dictionary<long, List<CellEdit>> CellEdits { get; } = new();
    public List<RowOp> RowOps { get; } = new(); // also the undo stack
}
```

## Bounded Decode Cache

```csharp
class DecodedRowCache
{
    // Fixed-capacity LRU keyed by row index; evicts oldest on overflow.
    // Prevents unbounded managed allocation as the user scrolls through a 2 GB file.
}
```

---

# 10. Deployment

```
Windows Desktop (.NET 8, self-contained/ReadyToRun .exe)
        │
        ├── Memory-mapped local DIF file (read-only)
        └── Local log file
```

No server, browser, or network dependency in this phase. Phase 2 adds an S3 client (AWS SDK
for .NET) behind `IStorageProvider`.

---

# 11. Testing

- **Unit:** indexer correctness, overlay apply/undo, streaming export round-trip.
- **Performance:** benchmark suite (10 MB / 500 MB / 2 GB synthetic files) measuring index
  time, scroll frame time, sort/filter time, export time, peak working set, **and GC pause
  frequency/duration** (the .NET-specific check that has no C++ equivalent).
- **Fuzz/robustness:** truncated/corrupted files, non-DIF binary input.

---

# 12. Risks

| Risk                                                                       | Mitigation                                                                                                                              |
| -------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| GC pauses/stutter at 2 GB scale                                            | Unmanaged index/key arrays, bounded decode cache, Server GC, profiler-verified acceptance check (Section 8)                             |
| WPF's built-in `DataGrid` virtualization isn't enough at target row counts | Fall back to a custom-drawn grid (e.g. SkiaSharp) if profiling shows it's needed — budget this as a contingency, not assume it up front |
| Overlay resolution edge cases (edit on a since-deleted row, etc.)          | Define resolution order precisely in design, cover with unit tests                                                                      |
| Deferred validation/S3 create later integration risk                       | `IValidator`/`IStorageProvider` reserved now (Section 6.8)                                                                              |

---

# 13. Acceptance Criteria

- ✅ Opens and indexes a 2 GB sample DIF file within target time without blocking the UI.
- ✅ Scrolling a 2 GB-scale file sustains 60 fps.
- ✅ Sort/filter operate on the index, not full row content, meeting timing targets.
- ✅ Cell edit, add/delete/duplicate row, and undo work via the overlay, no source mutation.
- ✅ Export streams correctly for all four formats.
- ✅ Memory usage and GC pause behavior stay within target bounds for a 2 GB file under a
  realistic editing session (profiler-verified).
- ✅ Malformed files fail gracefully, no crash.

---

# 14. Version History

| Version | Date      | Description                                                                      |
| ------- | --------- | -------------------------------------------------------------------------------- |
| 1.0     | June 2026 | Initial PRS — Java Spring Boot / web architecture                                |
| 2.0     | July 2026 | Rewritten for native C++ desktop app                                             |
| 2.1     | July 2026 | Rewritten for .NET 8 (C#) desktop app — same architecture, GC-aware design added |
