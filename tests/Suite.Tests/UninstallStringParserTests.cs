using WindowsCareKit.Core.Modules.Uninstall;
using Xunit;

namespace WindowsCareKit.Tests;

public class UninstallStringParserTests
{
    [Fact]
    public void Parses_quoted_exe_without_args()
    {
        var p = UninstallStringParser.Parse("\"C:\\Program Files\\App\\unins000.exe\"");
        Assert.NotNull(p);
        Assert.Equal(@"C:\Program Files\App\unins000.exe", p!.FileName);
        Assert.Empty(p.Arguments);
        Assert.False(p.IsMsi);
    }

    [Fact]
    public void Parses_quoted_exe_with_args()
    {
        var p = UninstallStringParser.Parse("\"C:\\Program Files\\App\\unins000.exe\" /SILENT /NORESTART");
        Assert.NotNull(p);
        Assert.Equal(@"C:\Program Files\App\unins000.exe", p!.FileName);
        Assert.Equal(new[] { "/SILENT", "/NORESTART" }, p.Arguments);
    }

    [Fact]
    public void Parses_unquoted_exe_path_with_spaces_via_exe_boundary()
    {
        var p = UninstallStringParser.Parse(@"C:\Program Files\App\uninstall.exe /x");
        Assert.NotNull(p);
        Assert.Equal(@"C:\Program Files\App\uninstall.exe", p!.FileName);
        Assert.Equal(new[] { "/x" }, p.Arguments);
    }

    [Theory]
    [InlineData("MsiExec.exe /X{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}")]
    [InlineData("msiexec.exe /x {0A1B2C3D-4E5F-6789-ABCD-EF0123456789}")]
    public void Parses_msiexec(string raw)
    {
        var p = UninstallStringParser.Parse(raw);
        Assert.NotNull(p);
        Assert.True(p!.IsMsi);
        Assert.Equal("msiexec", Path.GetFileNameWithoutExtension(p.FileName), ignoreCase: true);
        Assert.Contains(p.Arguments, a => a.Contains("0A1B2C3D", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parses_rundll32_form()
    {
        var p = UninstallStringParser.Parse(@"rundll32.exe C:\Win\setupapi.dll,InstallHinfSection DefaultUninstall 132");
        Assert.NotNull(p);
        Assert.Equal("rundll32.exe", Path.GetFileName(p!.FileName), ignoreCase: true);
        Assert.NotEmpty(p.Arguments);
    }

    [Fact]
    public void Keeps_quoted_argument_with_spaces_together()
    {
        var p = UninstallStringParser.Parse("\"C:\\app\\u.exe\" \"/log=C:\\Temp\\my log.txt\"");
        Assert.NotNull(p);
        Assert.Equal(new[] { @"/log=C:\Temp\my log.txt" }, p!.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_null_for_blank(string? raw)
        => Assert.Null(UninstallStringParser.Parse(raw));

    [Fact]
    public void Returns_null_for_unterminated_quote()
        => Assert.Null(UninstallStringParser.Parse("\"C:\\app\\u.exe"));
}
