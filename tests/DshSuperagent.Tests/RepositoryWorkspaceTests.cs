using DshSuperagent.Core.Configuration;
using DshSuperagent.Core.Repositories;
using DshSuperagent.Core.Review;

namespace DshSuperagent.Tests;

public sealed class RepositoryWorkspaceTests
{
    [Theory]
    [InlineData("secret.cs")]
    [InlineData("中文.cs")]
    public async Task GitIgnore_Matches_The_First_Stdin_Path_Without_A_Bom(string fileName)
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-superagent-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var init = await ProcessRunner.RunAsync("git", ["init", "-q"], root, TimeSpan.FromSeconds(20));
            Assert.Equal(0, init.ExitCode);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), $"/{fileName}\n");
            await File.WriteAllTextAsync(Path.Combine(root, fileName), "// TODO ignored\n");
            var workspace = new RepositoryWorkspace(root);
            Assert.Empty(await workspace.ListFilesAsync(null, 100, CancellationToken.None));
            Assert.Empty(await workspace.SearchAsync("TODO", false, 100, CancellationToken.None));
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public async Task GitIgnore_Applies_Nested_Negation_Anchoring_And_Tracked_File_Rules_To_List_Search_And_Scan()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-superagent-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "ignored"));
        try
        {
            var init = await ProcessRunner.RunAsync("git", ["init", "-q"], root, TimeSpan.FromSeconds(20));
            Assert.Equal(0, init.ExitCode);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"),
                "# comment\n*.generated.cs\n!keep.generated.cs\n/root-only.cs\nignored/\n!ignored/rescue.cs\n\\#hidden.cs\n\\!hidden.cs\n");
            await File.WriteAllTextAsync(Path.Combine(root, "src", ".gitignore"), "*.cs\n!keep.cs\n!root-only.cs\n");
            string[] ignored = ["drop.generated.cs", "root-only.cs", "ignored/drop.cs", "ignored/rescue.cs",
                "src/drop.cs", "#hidden.cs", "!hidden.cs", "中文.generated.cs"];
            string[] included = ["keep.generated.cs", "src/keep.cs", "src/root-only.cs", "中文.cs"];
            foreach (var file in ignored.Concat(included))
            {
                await File.WriteAllTextAsync(Path.Combine(root, file), "// TODO review marker\n");
            }

            var add = await ProcessRunner.RunAsync("git", ["add", "-f", "drop.generated.cs"], root, TimeSpan.FromSeconds(20));
            Assert.Equal(0, add.ExitCode);
            var workspace = new RepositoryWorkspace(root);
            var listed = await workspace.ListFilesAsync(null, 100, CancellationToken.None);
            Assert.Equal(included.Order(), listed.Select(file => file.Path).Order());
            var matches = await workspace.SearchAsync("review marker", false, 100, CancellationToken.None);
            Assert.Equal(included.Order(), matches.Select(match => match.Path).Order());
            var scan = await new StaticCodeReviewer().ScanAsync(new RepositoryProfile { LocalPath = root }, 100, CancellationToken.None);
            Assert.Equal(included.Length, scan.FilesScanned);
            Assert.Equal(included.Order(), scan.Findings.Select(finding => finding.FilePath).Order());
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public async Task GitIgnore_Is_Applied_Before_Result_Limit_Across_Batches_And_Reloaded_After_Changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-superagent-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var init = await ProcessRunner.RunAsync("git", ["init", "-q"], root, TimeSpan.FromSeconds(20));
            Assert.Equal(0, init.ExitCode);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "drop-*.cs\n");
            for (var index = 0; index < 205; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(root, $"drop-{index:D3}.cs"), "// TODO ignored\n");
            }

            await File.WriteAllTextAsync(Path.Combine(root, "keep.cs"), "// TODO kept\n");
            var workspace = new RepositoryWorkspace(root);
            Assert.Equal("keep.cs", Assert.Single(await workspace.ListFilesAsync(null, 1, CancellationToken.None)).Path);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "*.cs\n");
            Assert.Empty(await workspace.ListFilesAsync(null, 100, CancellationToken.None));
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public async Task Scan_Skips_Oversized_File_And_Counts_Only_Read_Files_Without_Relaxing_Read_Limit()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-superagent-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "large.xml"), new string('x', 512 * 1024 + 1));
            await File.WriteAllTextAsync(Path.Combine(root, "Page.cs"), "// TODO fix this\n");
            var scan = await new StaticCodeReviewer().ScanAsync(new RepositoryProfile { LocalPath = root }, 1, CancellationToken.None);
            Assert.Equal(1, scan.FilesScanned);
            Assert.Equal("Page.cs", Assert.Single(scan.Findings).FilePath);
            await Assert.ThrowsAsync<FileTooLargeException>(() => new RepositoryWorkspace(root)
                .ReadFileAsync("large.xml", 1, 10, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void DeleteRepository(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

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
