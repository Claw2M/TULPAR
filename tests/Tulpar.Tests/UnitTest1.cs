using System.Reflection;

namespace Tulpar.Tests;

public class UnitTest1
{
    [Fact]
    public void SolutionAssemblies_LoadCleanly()
    {
        Assert.NotNull(typeof(Tulpar.App.App).Assembly);
        Assert.NotNull(Assembly.Load("Tulpar.Core"));
        Assert.NotNull(Assembly.Load("Tulpar.MapHost"));
    }
}
