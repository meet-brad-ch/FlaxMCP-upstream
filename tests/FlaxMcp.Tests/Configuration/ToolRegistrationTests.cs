using FlaxMcp.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace FlaxMcp.Tests.Configuration;

public sealed class ToolRegistrationTests
{
    private static IReadOnlyList<McpServerTool> Register(FlaxMcpOptions options)
    {
        var services = new ServiceCollection();
        ToolRegistration.AddTools(services, options);
        using var provider = services.BuildServiceProvider();
        return [.. provider.GetServices<McpServerTool>()];
    }

    [Fact]
    public void AddTools_WithDefaults_RegistersEveryTool()
    {
        var tools = Register(new FlaxMcpOptions());

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "server_info");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "flax_status");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "project_info");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_scene_graph");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_get_selection");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_set_selection");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_actor_details");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_modify_actor");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_create_actor");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_duplicate_actor");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_rename_actor");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_reparent_actor");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_delete_actor");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_static_model_details");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_set_static_model");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_set_static_model_material");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "content_create_material");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "content_material_details");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "content_create_material_instance");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "content_set_material_instance_parameter");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_box_collider_details");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_create_box_collider");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_set_box_collider");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_save");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_play_mode");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_screenshot");
        Assert.DoesNotContain(tools, tool => tool.ProtocolTool.Name == "editor_execute_csharp");
        Assert.DoesNotContain(tools, tool => tool.ProtocolTool.Name == "editor_invoke_method");
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "editor_set_member");
    }

    [Fact]
    public void AddTools_WithReadOnly_OnlyRegistersReadOnlyTools()
    {
        var tools = Register(new FlaxMcpOptions { ReadOnly = true });

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint));
    }

    [Fact]
    public void AddTools_WithReadOnly_ExcludesWriteToolsButKeepsTheirReadOnlySiblings()
    {
        var names = Register(new FlaxMcpOptions { ReadOnly = true }).Select(tool => tool.ProtocolTool.Name).ToList();

        Assert.DoesNotContain("build_generate_projects", names);
        Assert.DoesNotContain("build_compile_scripts", names);
        Assert.DoesNotContain("build_clear_cache", names);
        Assert.DoesNotContain("build_game", names);
        Assert.Contains("job_status", names);
        Assert.Contains("job_wait", names);
        Assert.DoesNotContain("editor_job_start", names);
        Assert.Contains("editor_events", names);
        Assert.Contains("editor_wait_for", names);
        Assert.Contains("game_commands", names);
        Assert.Contains("editor_find_actors", names);
        Assert.Contains("editor_get_member", names);
        Assert.DoesNotContain("editor_set_member", names);
        Assert.DoesNotContain("editor_add_script", names);
        Assert.DoesNotContain("editor_invoke_method", names);
        Assert.DoesNotContain("game_command", names);
        Assert.Contains("logs_tail", names);
        Assert.Contains("logs_errors", names);
        Assert.Contains("editor_get_selection", names);
        Assert.Contains("editor_actor_details", names);
        Assert.DoesNotContain("editor_set_selection", names);
        Assert.DoesNotContain("editor_modify_actor", names);
        Assert.DoesNotContain("editor_create_actor", names);
        Assert.DoesNotContain("editor_duplicate_actor", names);
        Assert.DoesNotContain("editor_rename_actor", names);
        Assert.DoesNotContain("editor_reparent_actor", names);
        Assert.DoesNotContain("editor_delete_actor", names);
        Assert.Contains("editor_static_model_details", names);
        Assert.DoesNotContain("editor_set_static_model", names);
        Assert.DoesNotContain("editor_set_static_model_material", names);
        Assert.DoesNotContain("content_create_material", names);
        Assert.Contains("content_material_details", names);
        Assert.DoesNotContain("content_create_material_instance", names);
        Assert.DoesNotContain("content_set_material_instance_parameter", names);
        Assert.Contains("editor_box_collider_details", names);
        Assert.DoesNotContain("editor_create_box_collider", names);
        Assert.DoesNotContain("editor_set_box_collider", names);
        Assert.DoesNotContain("editor_save", names);
        Assert.DoesNotContain("editor_play_mode", names);
        Assert.DoesNotContain("editor_screenshot", names);
        Assert.DoesNotContain("editor_execute_csharp", names);
    }

    [Fact]
    public void AddTools_WithCodeExecutionEnabled_RegistersExecuteCSharp()
    {
        var names = Register(new FlaxMcpOptions { AllowCodeExecution = true })
            .Select(tool => tool.ProtocolTool.Name);

        Assert.Contains("editor_execute_csharp", names);
        Assert.Contains("editor_invoke_method", names);
    }

    [Fact]
    public void AddTools_WithCodeExecutionAndReadOnly_ExcludesExecuteCSharp()
    {
        var names = Register(new FlaxMcpOptions { AllowCodeExecution = true, ReadOnly = true })
            .Select(tool => tool.ProtocolTool.Name);

        Assert.DoesNotContain("editor_execute_csharp", names);
    }

    [Fact]
    public void AddTools_WithToolsetSelection_AlwaysKeepsServerToolset()
    {
        var tools = Register(new FlaxMcpOptions { Toolsets = "project" });

        var names = tools.Select(tool => tool.ProtocolTool.Name).ToList();
        Assert.Contains("server_info", names);
        Assert.Contains("project_info", names);
    }

    [Fact]
    public void AddTools_PublishesOutputSchemasForStructuredResults()
    {
        var tools = Register(new FlaxMcpOptions());

        var projectInfo = Assert.Single(tools, tool => tool.ProtocolTool.Name == "project_info");
        Assert.NotNull(projectInfo.ProtocolTool.OutputSchema);
    }
}
