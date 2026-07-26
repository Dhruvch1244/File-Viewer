# Bloomberg File Viewer

[![CI](https://github.com/Dhruvch1244/File-Viewer/actions/workflows/ci.yml/badge.svg)](https://github.com/Dhruvch1244/File-Viewer/actions/workflows/ci.yml)

A native Windows (.NET 8 / WPF) desktop app for opening, browsing, editing, and exporting
Bloomberg DIF/GETDATA files — built to stay responsive on files up to 2 GB, with instant
virtualized scrolling instead of loading the whole file into memory.

![Welcome screen](docs/screenshots/welcome-screen.png)

## Features

- **Handles large files without stalling.** Indexing runs on a background thread with progress
  reporting; the grid only ever decodes the rows currently on screen, backed by an unmanaged
  (non-GC-heap) row index and an LRU decoded-row cache.
- **Inline editing with full undo.** Edit any cell, add/duplicate/delete rows, bulk-delete a
  selection, and undo any of it — edits are tracked as an overlay on top of the original file, so
  the source file is never mutated until you export.
- **Excel-style column filtering.** Click a column header to see the distinct values present in
  that column and filter by them directly, without needing to already know what values exist.
  Double-click a header to sort by it instead.
- **Search and select at scale.** Free-text search across every column, plus a "select all" that
  spans every row matching the current filter — not just the page currently on screen — so bulk
  actions act on the full result set.
- **Multi-format export.** Export to DIF, CSV, TSV, or JSON with a live preview of the first rows,
  reflecting your current edits, sort, and filters.
- **Light and dark themes**, toggled from the button in the toolbar.
- **No content validation gate.** The app shows records as they are in the file; it doesn't reject
  or flag rows for looking "malformed" — only structural file-format issues (e.g. a missing
  section marker) are surfaced as diagnostics.

## Project layout

```
src/FileViewer.Core/    Format parsing, indexing, editing, sorting/filtering, export — no UI code
src/FileViewer.App/     WPF UI: the virtualized grid, view models, dialogs, theming
tests/FileViewer.Core.Tests/   xUnit tests for FileViewer.Core
benchmarks/FileViewer.Benchmarks/   BenchmarkDotNet suite (see its own README)
```

`FileViewer.Core` has no dependency on WPF or any UI framework — it's a plain library, so the
indexing/editing/export logic is independently testable and could back a different front end later.

## Building and running

Requires the .NET 8 SDK (or newer, as long as `net8.0` / `net8.0-windows` targets are available).

```
dotnet build FileViewer.slnx
dotnet run --project src/FileViewer.App
```

## Running tests

```
dotnet test tests/FileViewer.Core.Tests
```

## Publishing a release build

```
pwsh ./build-release.ps1
```

This produces a self-contained, single-file, ReadyToRun `win-x64` build of `FileViewer.App` — no
.NET runtime install required on the machine that runs it — and zips it up at
`release/FileViewer-win-x64.zip`. To do it by hand instead:

```
dotnet publish src/FileViewer.App -c Release -r win-x64 -o publish/FileViewer-win-x64
```

## CI/CD

- **CI** (`.github/workflows/ci.yml`) runs on every push and pull request: builds `FileViewer.App`
  and the benchmarks project, and runs the `FileViewer.Core` test suite on a Windows runner.
- **Release** (`.github/workflows/release.yml`) builds and attaches a downloadable
  `FileViewer-win-x64.zip` to a GitHub Release. It runs automatically when you push a tag matching
  `v*.*.*`:

  ```
  git tag v0.1.0
  git push origin v0.1.0
  ```

  or manually from the Actions tab (`Release` → `Run workflow`) against any branch, if you want a
  build without cutting a version tag.

### Code signing (avoiding the "Unknown Publisher" warning)

Release builds are signed via [SignPath](https://signpath.io)'s free code-signing program for
open-source projects, so the downloaded exe isn't flagged by Windows SmartScreen. To enable it on
a fork or after re-creating the repo:

1. Sign up at [signpath.io](https://signpath.io), create an organization, and apply for the free
   open-source plan for this project (this step needs manual approval from SignPath — it isn't
   something a script can do for you).
2. In the SignPath dashboard, create a **project** and a **signing policy** for release builds,
   and generate an **API token** with permission to submit signing requests.
3. In this repo's Settings → Secrets and variables → Actions, add:
   - Secrets: `SIGNPATH_API_TOKEN`, `SIGNPATH_ORG_ID`
   - Variables: `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`

Once those are set, the next tag push signs the exe automatically. Until then, `release.yml` skips
signing and ships an unsigned build — the workflow doesn't fail either way. Local builds from
`build-release.ps1` are always unsigned; only CI-produced releases get signed.

## Benchmarks

See [`benchmarks/FileViewer.Benchmarks/README.md`](benchmarks/FileViewer.Benchmarks/README.md) for
the BenchmarkDotNet suite covering indexing, scrolling, sort/filter, and export throughput.

---

Made by Dhruv Choudhary.
