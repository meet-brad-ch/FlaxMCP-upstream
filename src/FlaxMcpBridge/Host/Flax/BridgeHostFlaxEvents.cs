using System;
using FlaxEditor;
using FlaxEngine;

namespace FlaxMcpBridge.Host.Flax;

/// <summary>
/// Feeds the bridge host's event ring from the engine and the editor: managed log lines and exceptions, script
/// compiles and reloads, play mode, scene loads, saves and unloads. It lives with the host in the Default load
/// context, so its subscriptions survive script reloads; it subscribes to nothing that runs every frame.
/// </summary>
/// <remarks>
/// The one Flax-dependent part of the host: the host's other sources stay pure .NET for the tests. It must never log
/// (a log line would come back into it).
/// </remarks>
public static class BridgeHostFlaxEvents
{
    private static readonly object Gate = new();
    private static BridgeEventRing? _ring;

    /// <summary>Subscribes once per editor process; later calls do nothing.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_ring is not null)
            {
                return;
            }

            _ring = BridgeHostEntry.Events;
        }

        Debug.Logger.LogHandler.SendLog += OnLog;
        Debug.Logger.LogHandler.SendExceptionLog += OnException;
        ScriptsBuilder.CompilationBegin += () => Record(BridgeEventKind.CompileBegin);
        ScriptsBuilder.CompilationSuccess += () => Record(BridgeEventKind.CompileSucceeded);
        ScriptsBuilder.CompilationFailed += () => Record(BridgeEventKind.CompileFailed);
        ScriptsBuilder.ScriptsReloadBegin += () => Record(BridgeEventKind.ReloadBegin);
        ScriptsBuilder.ScriptsReloadEnd += () => Record(BridgeEventKind.ReloadEnd);
        Editor.Instance.PlayModeBegin += () => Record(BridgeEventKind.PlayBegin);
        Editor.Instance.PlayModeEnd += () => Record(BridgeEventKind.PlayEnd);
        Level.SceneLoaded += (scene, id) => Record(BridgeEventKind.SceneLoaded, scene?.Name);
        Level.SceneLoadError += (scene, id) => Record(BridgeEventKind.SceneLoadError, id.ToString());
        Level.SceneSaved += (scene, id) => Record(BridgeEventKind.SceneSaved, scene?.Name);
        Level.SceneSaveError += (scene, id) => Record(BridgeEventKind.SceneSaveError, scene?.Name);
        Level.SceneUnloaded += (scene, id) => Record(BridgeEventKind.SceneUnloaded, scene?.Name);
    }

    private static void OnLog(LogType level, string message, FlaxEngine.Object context, string stackTrace)
    {
        _ring?.Append(BridgeEventKind.Log, (byte)level, message);
    }

    private static void OnException(Exception exception, FlaxEngine.Object context)
    {
        _ring?.Append(BridgeEventKind.Exception, (byte)LogType.Error, exception.GetType().Name + ": " + exception.Message);
    }

    private static void Record(BridgeEventKind kind, string? text = null)
    {
        _ring?.Append(kind, 0, text);
    }
}
