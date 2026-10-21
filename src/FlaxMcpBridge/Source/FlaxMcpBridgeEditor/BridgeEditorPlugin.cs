using System;
using FlaxEditor;
using FlaxEngine;

namespace FlaxMcpBridge.Editor;

/// <summary>
/// Editor plugin for FlaxMCP: attaches this plugin generation's request handler to the bridge host, the named-pipe
/// server that outlives script reloads, so an external MCP server process can query and drive the editor.
/// </summary>
/// <seealso cref="FlaxEditor.EditorPlugin" />
public class BridgeEditorPlugin : EditorPlugin
{
    private PipeBridgeServer? _server;

    /// <inheritdoc />
    public override Type GamePluginType => typeof(BridgeGamePlugin);

    /// <inheritdoc />
    public override void InitializeEditor()
    {
        base.InitializeEditor();

        try
        {
            _server = new PipeBridgeServer();
            HostBootstrap.Attach(PipeBridgeServer.Generation, PipeBridgeServer.Protocol, _server.HandleRequestAsync);
            Scripting.Exit += OnExit;
            Debug.Log($"[FlaxMcpBridge] Attached to the bridge host (generation {PipeBridgeServer.Generation}).");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[FlaxMcpBridge] Failed to attach to the bridge host: {ex}");
        }
    }

    /// <inheritdoc />
    public override void Deinitialize()
    {
        Scripting.Exit -= OnExit;
        if (_server is not null)
        {
            HostBootstrap.Detach(PipeBridgeServer.Generation);
            _server.Dispose();
            _server = null;
        }

        base.Deinitialize();
    }

    private static void OnExit()
    {
        HostBootstrap.Shutdown();
    }
}
