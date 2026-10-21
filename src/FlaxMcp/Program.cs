using FlaxMcp.Configuration;
using FlaxMcp.Flax;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(
    Enum.TryParse<LogLevel>(
        Environment.GetEnvironmentVariable(FlaxMcpOptions.LogLevelVariable),
        true,
        out var minimumLevel
    ) ?
        minimumLevel :
        LogLevel.Warning
);

builder.Services.AddSingleton<IValidateOptions<FlaxMcpOptions>, FlaxMcpOptionsValidator>();
builder.Services.AddSingleton<FlaxContentIndex>();
builder.Services.AddSingleton<FlaxEditorSessionGuard>();
builder.Services.AddSingleton<FlaxBridgeClient>();
builder.Services.AddSingleton<IFlaxBridgeClient>(services => services.GetRequiredService<FlaxBridgeClient>());
builder.Services.AddSingleton<IFlaxGameCommandClient>(services => services.GetRequiredService<FlaxBridgeClient>());
builder.Services.AddSingleton<FlaxHeadlessEditorRunner>();
builder.Services.AddSingleton<FlaxBuildJobManager>();
builder.Services
       .AddOptions<FlaxMcpOptions>()
       .Configure(options => options.LoadFromEnvironment())
       .ValidateOnStart();

var startupOptions = new FlaxMcpOptions();
startupOptions.LoadFromEnvironment();

var toolCount = ToolRegistration.AddTools(builder.Services, startupOptions);

var mcpServer = builder.Services
                       .AddMcpServer(options => options.ServerInstructions = ServerInstructions.Build(startupOptions, toolCount));

PromptRegistration.AddPrompts(mcpServer, startupOptions);
mcpServer.WithStdioServerTransport();

await builder.Build().RunAsync();
