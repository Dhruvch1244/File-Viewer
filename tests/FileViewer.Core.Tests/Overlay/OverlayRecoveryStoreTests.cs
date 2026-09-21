using FileViewer.Core.Overlay;

namespace FileViewer.Core.Tests.Overlay;

public class OverlayRecoveryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"fileviewer-recovery-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static OverlayRecoveryRecord SampleRecord(string sourcePath = "C:\\data\\file.dif", int sectionIndex = 0)
    {
        var overlay = new EditOverlay();
        overlay.EditCell(3, "PRICE", "100.00");
        overlay.AddRow(["_ID", "PRICE"]);
        return new OverlayRecoveryRecord(sourcePath, sectionIndex, DateTimeOffset.UtcNow, overlay.CapturePersistedState());
    }

    [Fact]
    public void LoadAll_OnAMissingDirectory_ReturnsEmptyRatherThanThrowing()
    {
        Assert.Empty(OverlayRecoveryStore.LoadAll(Path.Combine(_directory, "does-not-exist")));
    }

    [Fact]
    public void Save_ThenLoadAll_RoundTripsTheRecord()
    {
        OverlayRecoveryRecord record = SampleRecord();
        OverlayRecoveryStore.Save(_directory, record);

        IReadOnlyList<OverlayRecoveryRecord> loaded = OverlayRecoveryStore.LoadAll(_directory);

        Assert.Single(loaded);
        Assert.Equal(record.SourceFilePath, loaded[0].SourceFilePath);
        Assert.Equal(record.SectionIndex, loaded[0].SectionIndex);
        Assert.False(loaded[0].Overlay.IsEmpty);
        Assert.True(loaded[0].Overlay.CellEdits.ContainsKey(3));
    }

    [Fact]
    public void Save_CalledTwiceForTheSameDocument_ReplacesRatherThanDuplicates()
    {
        OverlayRecoveryStore.Save(_directory, SampleRecord());
        OverlayRecoveryStore.Save(_directory, SampleRecord());

        Assert.Single(OverlayRecoveryStore.LoadAll(_directory));
    }

    [Fact]
    public void Save_ForDifferentSectionsOfTheSameFile_KeepsThemSeparate()
    {
        OverlayRecoveryStore.Save(_directory, SampleRecord(sectionIndex: 0));
        OverlayRecoveryStore.Save(_directory, SampleRecord(sectionIndex: 1));

        Assert.Equal(2, OverlayRecoveryStore.LoadAll(_directory).Count);
    }

    [Fact]
    public void Delete_RemovesOnlyTheNamedDocumentsRecoveryFile()
    {
        OverlayRecoveryStore.Save(_directory, SampleRecord(sourcePath: "C:\\data\\a.dif"));
        OverlayRecoveryStore.Save(_directory, SampleRecord(sourcePath: "C:\\data\\b.dif"));

        OverlayRecoveryStore.Delete(_directory, "C:\\data\\a.dif", sectionIndex: 0);

        IReadOnlyList<OverlayRecoveryRecord> remaining = OverlayRecoveryStore.LoadAll(_directory);
        Assert.Single(remaining);
        Assert.Equal("C:\\data\\b.dif", remaining[0].SourceFilePath);
    }

    [Fact]
    public void Delete_ForADocumentWithNoRecoveryFile_DoesNotThrow()
    {
        var exception = Record.Exception(() => OverlayRecoveryStore.Delete(_directory, "C:\\nothing.dif", sectionIndex: 0));
        Assert.Null(exception);
    }

    [Fact]
    public void LoadAll_SkipsACorruptFileRatherThanFailingTheWholeScan()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "corrupt.recovery.json"), "{ not valid json");
        OverlayRecoveryStore.Save(_directory, SampleRecord());

        IReadOnlyList<OverlayRecoveryRecord> loaded = OverlayRecoveryStore.LoadAll(_directory);

        Assert.Single(loaded);
    }

    [Fact]
    public void FileNameFor_IsCaseInsensitiveOnThePath_MatchingWindowsPathSemantics()
    {
        Assert.Equal(
            OverlayRecoveryStore.FileNameFor("C:\\Data\\File.dif", 0),
            OverlayRecoveryStore.FileNameFor("c:\\data\\file.dif", 0));
    }

    [Fact]
    public void FileNameFor_DiffersBySectionIndex()
    {
        Assert.NotEqual(
            OverlayRecoveryStore.FileNameFor("C:\\Data\\File.dif", 0),
            OverlayRecoveryStore.FileNameFor("C:\\Data\\File.dif", 1));
    }
}
