using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution.Adapters;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>The file-delete adapter sends to the recycle bin, decides file vs. dir, and throws on a missing target.</summary>
public class RecycleBinFileDeleteAdapterTests
{
    [Fact]
    public void Deletes_a_real_temp_file_to_the_recycle_bin()
    {
        // TEMP only — never a system path. (spec: real delete tested only in a temp dir.)
        string file = Path.Combine(Path.GetTempPath(), "wck-del-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(file, "delete me");
        Assert.True(File.Exists(file));

        var adapter = new RecycleBinFileDeleteAdapter();
        try
        {
            adapter.Delete(new FileDeleteAction
            {
                Path = file,
                ToRecycleBin = true,
                Description = "delete temp",
                Reason = "test",
                Risk = RiskLevel.Low,
                Undo = UndoCapability.Full,
            });

            Assert.False(File.Exists(file)); // gone from its original location (now in the recycle bin)
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void Deletes_a_real_temp_directory_to_the_recycle_bin()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wck-deldir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "inner.txt"), "x");

        var adapter = new RecycleBinFileDeleteAdapter();
        try
        {
            adapter.Delete(new FileDeleteAction
            {
                Path = dir,
                ToRecycleBin = true,
                Description = "delete dir",
                Reason = "test",
                Risk = RiskLevel.Low,
                Undo = UndoCapability.Full,
            });

            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Throws_FileNotFound_when_neither_file_nor_dir_exists()
    {
        string missing = Path.Combine(Path.GetTempPath(), "wck-missing-" + Guid.NewGuid().ToString("N"));
        var adapter = new RecycleBinFileDeleteAdapter();

        Assert.Throws<FileNotFoundException>(() => adapter.Delete(new FileDeleteAction
        {
            Path = missing,
            ToRecycleBin = true,
            Description = "missing",
            Reason = "test",
        }));
    }

    [Theory]
    [InlineData(@"C:\short\path", @"C:\short\path")]                       // under MAX_PATH: unchanged
    [InlineData(@"\\?\C:\already\extended", @"\\?\C:\already\extended")]   // already extended: unchanged
    public void ToExtendedLengthPath_leaves_short_and_extended_paths_alone(string input, string expected)
        => Assert.Equal(expected, RecycleBinFileDeleteAdapter.ToExtendedLengthPath(input));

    [Fact]
    public void ToExtendedLengthPath_prefixes_a_long_local_path()
    {
        string longPath = @"C:\" + new string('a', 300);
        Assert.Equal(@"\\?\" + longPath, RecycleBinFileDeleteAdapter.ToExtendedLengthPath(longPath));
    }

    [Fact]
    public void ToExtendedLengthPath_prefixes_a_long_unc_path()
    {
        string longUnc = @"\\server\share\" + new string('b', 300);
        Assert.Equal(@"\\?\UNC\server\share\" + new string('b', 300), RecycleBinFileDeleteAdapter.ToExtendedLengthPath(longUnc));
    }
}
