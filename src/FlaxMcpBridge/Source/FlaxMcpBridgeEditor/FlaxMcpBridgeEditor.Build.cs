using Flax.Build;
using Flax.Build.NativeCpp;
using System.IO;

public class FlaxMcpBridgeEditor : GameEditorModule
{
    /// <inheritdoc />
    public override void Setup(BuildOptions options)
    {
        base.Setup(options);

        options.PublicDependencies.Add("FlaxMcpBridge");
        options.ScriptingAPI.SystemReferences.Add("System.Runtime.Loader");
        options.ScriptingAPI.SystemReferences.Add("System.Text.Json");
        options.ScriptingAPI.SystemReferences.Add("System.Text.Encoding.Extensions");
        options.ScriptingAPI.FileReferences.Add(Path.Combine(Globals.EngineRoot, "Binaries", "Tools", "Microsoft.CodeAnalysis.dll"));
        options.ScriptingAPI.FileReferences.Add(Path.Combine(Globals.EngineRoot, "Binaries", "Tools", "Microsoft.CodeAnalysis.CSharp.dll"));

        BuildNativeCode = false;
    }
}
