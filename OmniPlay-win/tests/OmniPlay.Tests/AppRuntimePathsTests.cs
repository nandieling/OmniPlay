using OmniPlay.Core.Runtime;

namespace OmniPlay.Tests;

public sealed class AppRuntimePathsTests
{
    [Fact]
    public void ResolveAppRoot_UsesEnvironmentOverrideWhenPresent()
    {
        const string variableName = "OMNIPLAY_APP_ROOT";
        var originalValue = Environment.GetEnvironmentVariable(variableName);

        try
        {
            Environment.SetEnvironmentVariable(variableName, @".\windows\tmp\override-root");

            var resolved = AppRuntimePaths.ResolveAppRoot();

            Assert.Equal(
                Path.GetFullPath(@".\windows\tmp\override-root"),
                resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
        }
    }
}
