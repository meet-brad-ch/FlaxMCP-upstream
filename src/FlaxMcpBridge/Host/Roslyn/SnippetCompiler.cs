using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FlaxMcpBridge.Host.Roslyn;

/// <summary>
/// Compiles editor_execute_csharp snippets in the bridge host, so the compiler stays loaded and warm across script
/// reloads. References are kept per file (path, write time, size) and compiled images per source and references; the
/// plugin loads each image into its own collectible load context, as before.
/// </summary>
/// <remarks>
/// Only BCL types cross to the plugin. Roslyn is loaded into the Default load context from the engine's tools folder
/// (<see cref="Configure"/> first): the plugin's own copy is in its collectible context and cannot be shared.
/// </remarks>
public static class SnippetCompiler
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CachedReference> References = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SnippetCache Images = new(64);
    private static string? _roslynFolder;

    /// <summary>Tells the host where the engine's Roslyn assemblies are; call before the first compile.</summary>
    /// <param name="roslynFolder">The folder of Microsoft.CodeAnalysis.dll and Microsoft.CodeAnalysis.CSharp.dll.</param>
    public static void Configure(string roslynFolder)
    {
        lock (Gate)
        {
            if (_roslynFolder is not null)
            {
                return;
            }

            _roslynFolder = roslynFolder;
            AssemblyLoadContext.Default.Resolving += ResolveRoslyn;
        }
    }

    /// <summary>Compiles a snippet's source, or takes its image from the cache.</summary>
    /// <param name="source">The whole compilation unit.</param>
    /// <param name="referencePaths">The assemblies it may use, as files.</param>
    /// <param name="cacheHit">Whether the image came from the cache.</param>
    /// <param name="milliseconds">The time spent here.</param>
    /// <param name="referenceCount">How many references the compile used.</param>
    /// <returns>The compiled assembly.</returns>
    /// <exception cref="InvalidOperationException">The source does not compile; the message lists the errors.</exception>
    public static byte[] Compile(string source, string[] referencePaths, out bool cacheHit, out double milliseconds, out int referenceCount)
    {
        var watch = Stopwatch.StartNew();
        var references = ReferencesFor(referencePaths);
        referenceCount = references.Count;
        var key = Key(source, references);
        var image = Images.Find(key);
        cacheHit = image is not null;
        if (image is null)
        {
            image = Emit(key, source, references);
            Images.Store(key, image);
        }

        milliseconds = watch.Elapsed.TotalMilliseconds;
        return image;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] Emit(string key, string source, IReadOnlyList<CachedReference> references)
    {
        var compilation = CSharpCompilation.Create(
            "FlaxMcpDynamic_" + key[..16],
            new[] { CSharpSyntaxTree.ParseText(source) },
            references.Select(reference => (MetadataReference)reference.Reference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success)
        {
            throw new InvalidOperationException(
                "C# compilation failed: " + string.Join(Environment.NewLine,
                    emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        }

        return image.ToArray();
    }

    private static IReadOnlyList<CachedReference> ReferencesFor(string[] paths)
    {
        lock (Gate)
        {
            var result = new List<CachedReference>(paths.Length);
            foreach (var path in paths)
            {
                var file = new FileInfo(path);
                if (!References.TryGetValue(path, out var reference) ||
                    reference.WriteTicks != file.LastWriteTimeUtc.Ticks || reference.Length != file.Length)
                {
                    reference = new CachedReference(path, file.LastWriteTimeUtc.Ticks, file.Length, MetadataReference.CreateFromFile(path));
                    References[path] = reference;
                }

                result.Add(reference);
            }

            return result;
        }
    }

    private static string Key(string source, IReadOnlyList<CachedReference> references)
    {
        var text = new StringBuilder(source);
        foreach (var reference in references)
        {
            text.Append('\n').Append(reference.Path).Append('|').Append(reference.WriteTicks).Append('|').Append(reference.Length);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static Assembly? ResolveRoslyn(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is null || !name.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.Combine(_roslynFolder!, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    private sealed record CachedReference(string Path, long WriteTicks, long Length, PortableExecutableReference Reference);
}
