using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FlaxEngine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FlaxMcpBridge.Editor;

/// <summary>
/// Finds or starts the bridge host (Host/*.cs) and attaches this plugin generation to it. The host is compiled once
/// per editor process and loaded into the Default AssemblyLoadContext, so script reloads do not stop it. When its
/// sources change, the new version is compiled under its own name and takes the pipe over from the old one.
/// </summary>
internal static class HostBootstrap
{
    private const string EntryTypeName = "FlaxMcpBridge.Host.BridgeHostEntry";
    private const string AssemblyPrefix = "FlaxMcpBridgeHost-";
    private const string MarkerFile = "BridgeHostEntry.cs";
    private const double AttachWaitSeconds = 10;

    /// <summary>Starts the host when needed and attaches the given dispatcher as the current generation.</summary>
    /// <param name="generation">This plugin generation.</param>
    /// <param name="protocolVersion">The bridge protocol the plugin speaks.</param>
    /// <param name="dispatch">Answers a request line with a response line.</param>
    /// <exception cref="InvalidOperationException">The host sources were not found or did not compile.</exception>
    public static void Attach(long generation, int protocolVersion, Func<string, Task<string>> dispatch)
    {
        var folder = SourceFolder();
        var files = Directory.GetFiles(folder, "*.cs").OrderBy(file => file, StringComparer.Ordinal).ToArray();
        var sources = files.Select(File.ReadAllText).ToArray();
        var version = Version(sources);
        var entry = LoadedEntry(version) ?? Compile(version, files, sources);
        ShutDownOtherVersions(version);
        if (!(bool)entry.GetProperty("IsStarted")!.GetValue(null)!)
        {
            Invoke(entry, "Start", JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["projectFolder"] = Globals.ProjectFolder,
                ["sessionsDirectory"] = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlaxMcp", "sessions"),
                ["hostVersion"] = version,
                ["attachWaitSeconds"] = AttachWaitSeconds,
            }));
        }

        Invoke(entry, "Attach", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["generation"] = generation,
            ["protocolVersion"] = protocolVersion,
            ["pluginVersion"] = typeof(HostBootstrap).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ["engineBuild"] = Globals.EngineBuildNumber,
        }), dispatch);
    }

    /// <summary>Detaches a plugin generation from every running host (there is one).</summary>
    /// <param name="generation">The generation going away.</param>
    public static void Detach(long generation)
    {
        foreach (var entry in RunningEntries())
        {
            Invoke(entry, "Detach", generation);
        }
    }

    /// <summary>Stops every running host and removes its handshake file (the editor is exiting).</summary>
    public static void Shutdown()
    {
        foreach (var entry in RunningEntries())
        {
            Invoke(entry, "Shutdown");
        }
    }

    private static string SourceFolder()
    {
        var folders = FlaxEditor.Editor.Instance.GameProject.GetAllProjects()
            .Select(project => Path.Combine(project.ProjectFolderPath, "Host"))
            .Where(folder => File.Exists(Path.Combine(folder, MarkerFile)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return folders.Length == 1 ?
            folders[0] :
            throw new InvalidOperationException(
                $"Expected one referenced project with Host/{MarkerFile} (the FlaxMcp bridge), found {folders.Length}.");
    }

    private static string Version(IEnumerable<string> sources)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n// next file\n", sources)));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static IEnumerable<Assembly> HostAssemblies() =>
        AssemblyLoadContext.Default.Assemblies.Where(assembly =>
            assembly.GetName().Name?.StartsWith(AssemblyPrefix, StringComparison.Ordinal) == true);

    private static Type? LoadedEntry(string version) =>
        HostAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == AssemblyPrefix + version)?
            .GetType(EntryTypeName, throwOnError: true);

    private static IEnumerable<Type> RunningEntries() =>
        HostAssemblies()
            .Select(assembly => assembly.GetType(EntryTypeName, throwOnError: true)!)
            .Where(entry => (bool)entry.GetProperty("IsStarted")!.GetValue(null)!)
            .ToArray();

    private static void ShutDownOtherVersions(string version)
    {
        foreach (var entry in RunningEntries().Where(entry => entry.Assembly.GetName().Name != AssemblyPrefix + version))
        {
            Invoke(entry, "Shutdown");
            Debug.LogWarning(
                $"[FlaxMcpBridge] The bridge host changed: {entry.Assembly.GetName().Name} stopped, {AssemblyPrefix}{version} " +
                "takes over. The old host stays loaded until the editor restarts.");
        }
    }

    private static Type Compile(string version, IReadOnlyList<string> files, IReadOnlyList<string> sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp11);
        var trees = files.Select((file, index) => CSharpSyntaxTree.ParseText(sources[index], parseOptions, file, Encoding.UTF8));
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = CSharpCompilation.Create(
            AssemblyPrefix + version,
            trees,
            platform.Where(IsManaged).Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success)
        {
            throw new InvalidOperationException(
                "The bridge host did not compile: " +
                string.Join("; ", emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        }

        image.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(image).GetType(EntryTypeName, throwOnError: true)!;
    }

    private static bool IsManaged(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            // The trusted platform list also names the native libraries next to the runtime.
            return false;
        }
    }

    private static void Invoke(Type entry, string method, params object[] arguments) =>
        entry.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, BindingFlags.DoNotWrapExceptions, null, arguments, null);
}
