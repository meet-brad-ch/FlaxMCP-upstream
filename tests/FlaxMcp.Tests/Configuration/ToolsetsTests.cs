using FlaxMcp.Configuration;
using FlaxMcp.Tools;
using Xunit;

namespace FlaxMcp.Tests.Configuration;

public sealed class ToolsetsTests
{
    [Fact]
    public void Resolve_WithoutSelection_ReturnsEveryToolset()
    {
        var types = Toolsets.Resolve(null);

        Assert.Equal(types.Distinct().Count(), types.Count);
        Assert.Contains(typeof(ServerInfoTool), types);
        Assert.Contains(typeof(ProjectTools), types);
        Assert.Contains(typeof(EditorTools), types);
        Assert.Contains(typeof(EditorObjectTools), types);
        Assert.Contains(typeof(JobTools), types);
    }

    [Fact]
    public void Resolve_Editor_ReturnsBothEditorToolClasses()
    {
        var types = Toolsets.Resolve("editor");

        Assert.Equal([typeof(ServerInfoTool), typeof(EditorTools), typeof(EditorObjectTools)], types);
    }

    [Fact]
    public void Resolve_WithSelection_KeepsServerToolsetAndSelectedOnes()
    {
        var types = Toolsets.Resolve("project");

        Assert.Equal(2, types.Count);
        Assert.Contains(typeof(ServerInfoTool), types);
        Assert.Contains(typeof(ProjectTools), types);
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveAndIgnoresDuplicates()
    {
        var types = Toolsets.Resolve("PROJECT,project,server");

        Assert.Equal(2, types.Count);
        Assert.Contains(typeof(ProjectTools), types);
    }

    [Fact]
    public void Resolve_WithUnknownToolset_ThrowsListingValidNames()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Toolsets.Resolve("project,bogus"));

        Assert.Contains("bogus", exception.Message);
        Assert.Contains("project", exception.Message);
        Assert.Contains(FlaxMcpOptions.ToolsetsVariable, exception.Message);
    }
}
