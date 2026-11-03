using DshSuperagent.Core.Repositories;

namespace DshSuperagent.Tests;

public sealed class RepositoryWorkspaceTests
{
    [Fact]
    public async Task Skips_Default_Build_Directories_And_Env_Files_And_Blocks_Traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-superagent-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Page.cs"), "public sealed class Page { }");
        await File.WriteAllTextAsync(Path.Combine(root, "bin", "Ignored.cs"), "public sealed class Ignored { }");
        await File.WriteAllTextAsync(Path.Combine(root, ".env"), "PASSWORD=secret");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(root)!, "outside.txt"), "outside");

        try
        {
            var workspace = new RepositoryWorkspace(root);
            var files = await workspace.ListFilesAsync(null, 50, CancellationToken.None);
            Assert.Contains(files, file => file.Path == "src/Page.cs");
            Assert.DoesNotContain(files, file => file.Path.StartsWith("bin/", StringComparison.Ordinal));
            Assert.DoesNotContain(files, file => file.Path == ".env");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                workspace.ReadFileAsync("../outside.txt", 1, 10, CancellationToken.None));

            var matches = await workspace.SearchAsync("Page", useRegex: false, maxResults: 10, CancellationToken.None);
            Assert.Single(matches);
            Assert.Equal("src/Page.cs", matches[0].Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
