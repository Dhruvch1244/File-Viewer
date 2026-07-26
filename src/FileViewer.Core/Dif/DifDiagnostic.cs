namespace FileViewer.Core.Dif;

public enum DifDiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>
/// A non-fatal (or file-invalidating) issue found while parsing a DIF file. Malformed/truncated
/// input is always recorded here rather than thrown, so a file can still open in a degraded state
/// (PRS §8 Reliability, §13 acceptance criterion).
/// </summary>
public sealed record DifDiagnostic(DifDiagnosticSeverity Severity, string Message, long? Offset = null);
