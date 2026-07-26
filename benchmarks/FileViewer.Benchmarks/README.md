# FileViewer.Benchmarks

BenchmarkDotNet suite covering PRS §11's performance matrix: indexing, scroll (resolve
throughput), sort/filter, and export.

## Running

Must run in Release (BenchmarkDotNet refuses Debug builds):

```
dotnet run --project benchmarks/FileViewer.Benchmarks -c Release
```

Or a specific class:

```
dotnet run --project benchmarks/FileViewer.Benchmarks -c Release -- --filter "*ScrollBenchmarks*"
```

`IndexingBenchmarks` generates 10 MB / 500 MB / 2 GB synthetic files in `GlobalSetup` regardless
of which `[Benchmark]` method is filtered to — the 2 GB case is the slowest run in this suite by
a wide margin. Run 10 MB/500 MB first for fast iteration; save the full 2 GB run for a deliberate
final verification pass, not routine iteration.

## What this suite does *not* replace

`[MemoryDiagnoser]` gives allocation counts and Gen0/1/2 collection counts per operation, which is
useful signal but is not the same thing as PRS §11's "GC pause frequency/duration" acceptance
check. That needs a real trace against the running WPF app under an actual scroll/edit session —
use `dotnet-trace` or PerfView against `FileViewer.App` with a 2 GB synthetic file loaded, not this
benchmark suite alone.

## Tuning inputs these benchmarks inform

- `DecodedRowCache` default capacity (`FileViewer.Core.Caching.DecodedRowCache.DefaultCapacity`)
- Indexer chunk-size threshold (`FileViewer.Core.Indexing.FileIndexer.MinChunkBytes`)
- `DataGrid` `VirtualizingPanel.ScrollUnit` (`Pixel` vs `Item`) in `MainWindow.xaml`
