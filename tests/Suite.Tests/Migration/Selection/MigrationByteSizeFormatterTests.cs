using System.Globalization;
using WindowsCareKit.Core.Modules.Migration.Selection;
using Xunit;

namespace WindowsCareKit.Tests.Migration.Selection;

public sealed class MigrationByteSizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    public void Format_preserves_compact_byte_units(long bytes, string expected)
        => Assert.Equal(expected, MigrationByteSizeFormatter.Format(bytes, CultureInfo.InvariantCulture));

    [Fact]
    public void Format_uses_the_requested_culture_for_fractional_sizes()
        => Assert.Equal("1,5 KB", MigrationByteSizeFormatter.Format(1536, CultureInfo.GetCultureInfo("tr-TR")));
}
