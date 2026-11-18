using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class LocalDirectoryPathResolverTests
{
    private static readonly string ContainerRoot = Path.Combine(Path.GetTempPath(), "odw-path-mapping");

    [Theory]
    [InlineData(@"D:\Repos\SampleRepo", "SampleRepo")]
    [InlineData("D:/Repos/SampleRepo", "SampleRepo")]
    [InlineData(@"  d:\repos\SampleRepo\  ", "SampleRepo")]
    [InlineData(@"D:\Repos\团队 项目\src", "团队 项目/src")]
    [InlineData(@"D:\Repos\other\..\SampleRepo\.", "SampleRepo")]
    [InlineData(@"D:\Repos", "")]
    public void Resolve_MapsWindowsHostPathsAcrossPlatforms(string input, string relative)
    {
        var actual = LocalDirectoryPathResolver.Resolve(input,
            [new() { HostPath = @"D:\Repos\", ContainerPath = ContainerRoot }]);

        Assert.Equal(Path.GetFullPath(Path.Combine(ContainerRoot, relative.Replace('/', Path.DirectorySeparatorChar))), actual);
    }

    [Fact]
    public void Resolve_UsesMostSpecificMount()
    {
        var nestedRoot = Path.Combine(ContainerRoot, "special-mount");
        var actual = LocalDirectoryPathResolver.Resolve(@"D:\Repos\SampleRepo\src",
        [
            new() { HostPath = @"D:\Repos", ContainerPath = ContainerRoot },
            new() { HostPath = @"D:\Repos\SampleRepo", ContainerPath = nestedRoot }
        ]);

        Assert.Equal(Path.Combine(nestedRoot, "src"), actual);
    }

    [Fact]
    public void Resolve_SupportsUncShares()
    {
        var actual = LocalDirectoryPathResolver.Resolve(@"\\SERVER\repos\Team\repo",
            [new() { HostPath = @"\\server\repos", ContainerPath = ContainerRoot }]);

        Assert.Equal(Path.Combine(ContainerRoot, "Team", "repo"), actual);
    }

    [Fact]
    public void Resolve_KeepsNativeBackendPathsWorking()
    {
        Assert.Equal(Path.GetFullPath(ContainerRoot), LocalDirectoryPathResolver.Resolve(ContainerRoot, []));
    }

    [Theory]
    [InlineData(@"D:\Repos-other\SampleRepo")]
    [InlineData(@"D:\Repos\..\private\SampleRepo")]
    [InlineData(@"E:\Repos\SampleRepo")]
    public void Resolve_NeverMapsSiblingsOtherDrivesOrTraversalOutsideMount(string input)
    {
        LocalPathMapping[] mappings = [new() { HostPath = @"D:\Repos", ContainerPath = ContainerRoot }];
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.GetFullPath(input), LocalDirectoryPathResolver.Resolve(input, mappings));
        }
        else
        {
            var error = Assert.Throws<ArgumentException>(() => LocalDirectoryPathResolver.Resolve(input, mappings));
            Assert.Contains("尚未配置", error.Message);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("SampleRepo")]
    [InlineData(@"D:Repos\SampleRepo")]
    [InlineData(@"D:\..\SampleRepo")]
    [InlineData(@"D:\Repos\file:stream")]
    [InlineData(@"\\?\D:\Repos\SampleRepo")]
    [InlineData(@"\\server")]
    public void Resolve_RejectsInvalidPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => LocalDirectoryPathResolver.Resolve(path, []));
    }

    [Fact]
    public void Resolve_RejectsRelativeMountDestination()
    {
        Assert.Throws<InvalidOperationException>(() => LocalDirectoryPathResolver.Resolve(@"D:\Repos\SampleRepo",
            [new() { HostPath = @"D:\Repos", ContainerPath = "relative-mount" }]));
    }
}
