using Xunit;
using WindowsCareKit.Core;

namespace WindowsCareKit.Tests;

public class SmokeTests
{
    [Fact]
    public void Toolchain_and_project_graph_compile()
    {
        Assert.Equal("Windows Care Kit", CoreInfo.ProductName);
    }
}
