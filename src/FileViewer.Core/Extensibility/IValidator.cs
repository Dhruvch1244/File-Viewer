using FileViewer.Core.Overlay;

namespace FileViewer.Core.Extensibility;

/// <summary>
/// Phase 2 extension point: field validation (e.g. `_ID`/`_ERR`/`FUT_CONT_SIZE`/`QUOTE_UNITS`,
/// deferred per PRS §3) hooking into the same resolved-row shape used by rendering and export. No
/// implementation exists yet — reserved now so Phase 2 slots in without reworking the render/export
/// path (PRS §6.8).
/// </summary>
public interface IValidator
{
    /// <summary>Returns validation error messages for the row, or an empty list if it's valid.</summary>
    IReadOnlyList<string> Validate(ResolvedRow row, IReadOnlyList<string> columnNames);
}
