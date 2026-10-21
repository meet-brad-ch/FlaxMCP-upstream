using System.ComponentModel;
using FlaxMcp.Flax;
using ModelContextProtocol.Server;

namespace FlaxMcp.Tools;

/// <summary>Commands the running game registered with the bridge (FlaxMcpBridge.GameCommands).</summary>
[McpServerToolType]
public sealed class GameTools
{
    private readonly IFlaxGameCommandClient _client;

    public GameTools(IFlaxGameCommandClient client)
    {
        _client = client;
    }

    [McpServerTool(Name = "game_commands", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Lists the commands that the game registered with the bridge (FlaxMcpBridge.GameCommands.Register). " +
        "The list contains the name, the description and the type that registered each command. " +
        "Game scripts usually register commands while they are enabled. " +
        "Thus the list in play mode can be different from the list in edit mode."
    )]
    public Task<FlaxGameCommandList> ListGameCommandsAsync(CancellationToken cancellationToken)
    {
        return _client.ListGameCommandsAsync(cancellationToken);
    }

    [McpServerTool(Name = "game_command", UseStructuredContent = true)]
    [Description(
        "Runs a registered game command on the main thread of the editor and returns its text result. " +
        "The errors are unknown_command, which lists the registered names, and command_failed, which contains the exception type and message of the handler."
    )]
    public Task<FlaxGameCommandResult> RunGameCommandAsync(
        [Description("The command name, as game_commands lists it.")] string name,
        [Description("The argument string that the description of the command asks for.")] string args = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _client.RunGameCommandAsync(name, args, cancellationToken);
    }
}
