using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FlaxMcp.Configuration;

public static class ToolRegistration
{
    /// <summary>
    /// The SDK's options, except that null properties are written: the output schema lists every record parameter
    /// without a default as required, and a client that checks the schema rejects a result whose
    /// null property was left out.
    /// </summary>
    internal static JsonSerializerOptions SerializerOptions { get; } =
        new(McpJsonUtilities.DefaultOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static int AddTools(IServiceCollection services, FlaxMcpOptions options)
    {
        var registered = 0;

        foreach (var toolType in Toolsets.Resolve(options.Toolsets))
        {
            foreach (var method in SelectMethods(toolType, options))
            {
                var capturedType = toolType;
                var capturedMethod = method;
                services.AddSingleton<McpServerTool>(serviceProvider => McpServerTool.Create(
                        capturedMethod,
                        _ => ActivatorUtilities.CreateInstance(serviceProvider, capturedType),
                        new McpServerToolCreateOptions { Services = serviceProvider, SerializerOptions = SerializerOptions }
                    )
                );
                registered++;
            }
        }

        return registered;
    }

    private static IEnumerable<MethodInfo> SelectMethods(Type toolType, FlaxMcpOptions options)
    {
        return toolType
               .GetMethods(BindingFlags.Public | BindingFlags.Instance)
               .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is { } attribute &&
                   (!options.ReadOnly || attribute.ReadOnly) &&
                   (method.GetCustomAttribute<RequiresCodeExecutionAttribute>() is null ||
                       options.AllowCodeExecution && !options.ReadOnly)
               );
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class RequiresCodeExecutionAttribute : Attribute;
