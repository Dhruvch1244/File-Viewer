using FileViewer.App.Settings;

namespace FileViewer.App.Model.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void RememberRecentFile_PutsTheNewestFirstWithoutDuplicating()
    {
        var settings = new AppSettings();

        settings.RememberRecentFile(@"C:\a\one.dif");
        settings.RememberRecentFile(@"C:\a\two.dif");
        settings.RememberRecentFile(@"C:\A\ONE.DIF"); // same file, different casing

        Assert.Equal([@"C:\A\ONE.DIF", @"C:\a\two.dif"], settings.RecentFiles);
    }

    [Fact]
    public void RememberRecentFile_KeepsTheListBounded()
    {
        var settings = new AppSettings();

        for (int i = 0; i < AppSettings.MaxRecentFiles + 5; i++)
        {
            settings.RememberRecentFile($@"C:\exports\file{i}.dif");
        }

        Assert.Equal(AppSettings.MaxRecentFiles, settings.RecentFiles.Count);
        Assert.Equal($@"C:\exports\file{AppSettings.MaxRecentFiles + 4}.dif", settings.RecentFiles[0]);
    }

    [Fact]
    public void ForgetRecentFile_RemovesRegardlessOfCasing()
    {
        var settings = new AppSettings();
        settings.RememberRecentFile(@"C:\a\one.dif");

        settings.ForgetRecentFile(@"c:\A\ONE.dif");

        Assert.Empty(settings.RecentFiles);
    }

    [Fact]
    public void ExistingRecentFiles_DropsPathsThatAreNoLongerThere()
    {
        string realFile = Path.Combine(Path.GetTempPath(), $"fileviewer-settings-test-{Guid.NewGuid():N}.dif");
        File.WriteAllText(realFile, "INAHDR");
        try
        {
            var settings = new AppSettings();
            settings.RememberRecentFile(Path.Combine(Path.GetTempPath(), "definitely-not-here.dif"));
            settings.RememberRecentFile(realFile);

            Assert.Equal([realFile], settings.ExistingRecentFiles());
        }
        finally
        {
            File.Delete(realFile);
        }
    }

    [Fact]
    public void Load_WithAnUnreadableSettingsFile_ReturnsDefaultsAndReportsIt()
    {
        // Settings are a convenience: a corrupt file must degrade to "no remembered settings"
        // rather than stop the app from starting.
        string? warning = null;
        AppSettings.OnWarning = message => warning = message;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppSettings.SettingsFilePath)!);
            string backup = AppSettings.SettingsFilePath + ".bak";
            bool hadExisting = File.Exists(AppSettings.SettingsFilePath);
            if (hadExisting) File.Move(AppSettings.SettingsFilePath, backup, overwrite: true);

            try
            {
                File.WriteAllText(AppSettings.SettingsFilePath, "{ this is not json");

                AppSettings settings = AppSettings.Load();

                Assert.Empty(settings.RecentFiles);
                Assert.Null(settings.Theme);
                Assert.NotNull(warning);
            }
            finally
            {
                File.Delete(AppSettings.SettingsFilePath);
                if (hadExisting) File.Move(backup, AppSettings.SettingsFilePath, overwrite: true);
            }
        }
        finally
        {
            AppSettings.OnWarning = null;
        }
    }
}
