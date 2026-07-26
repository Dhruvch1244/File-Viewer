namespace FileViewer.Core.Overlay;

/// <summary>
/// A single entry in the undo stack (<see cref="EditOverlay.RowOps"/>). <see cref="PreviousState"/>
/// is an addition beyond the PRS §9 data-model sketch: without it, undoing a row that was, say,
/// Added and then Deleted would have no way to tell whether it should revert to Added or to
/// Normal — a flat <c>Dictionary&lt;long, RowState&gt;</c> loses that history the moment a new
/// state overwrites the old one. Capturing the pre-operation state at push time makes undo exact.
/// </summary>
public sealed record RowOp(RowOpType Type, long RowIndex, RowState PreviousState);
