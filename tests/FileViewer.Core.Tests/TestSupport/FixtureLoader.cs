namespace FileViewer.Core.Tests.TestSupport;

internal static class FixtureLoader
{
    public static byte[] ReadBytes(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}
