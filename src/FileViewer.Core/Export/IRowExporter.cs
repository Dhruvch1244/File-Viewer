using FileViewer.Core.Overlay;

namespace FileViewer.Core.Export;

/// <summary>
/// Streams one export format. Implementations are stateful for the duration of a single
/// Begin/WriteRow*/End sequence (not reusable across concurrent exports) so the underlying writer
/// can be created once and reused across rows rather than per call. Never closes the caller's
/// stream — <see cref="Dispose"/> only releases the writer's own internal state.
/// </summary>
public interface IRowExporter : IDisposable
{
    void Begin(Stream stream, IReadOnlyList<string> columnNames);
    void WriteRow(ResolvedRow row);
    void End();
}
