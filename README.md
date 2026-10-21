# FlaxMCP

[![CI](https://github.com/eXoz00rd/FlaxMCP/actions/workflows/ci.yml/badge.svg)](https://github.com/eXoz00rd/FlaxMCP/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

MCP (Model Context Protocol) server for **[Flax Engine](https://flaxengine.com)** projects, built with **.NET 10 / C#**
on the official [ModelContextProtocol C# SDK](https://www.nuget.org/packages/ModelContextProtocol).

> **Status:** source-only alpha. The server and bridge work from a checkout, but no public package or MCP registry entry
> is available yet.

## Why

Flax projects combine JSON project and scene files, binary content assets, a native build pipeline, and state that exists
only inside the running editor. FlaxMCP gives an MCP client one interface to all of them:

- offline project, content, and scene inspection
- script checks, headless script compilation, game builds, and structured log diagnostics
- a local editor bridge for live scenes, live objects, selection, transforms, saving, play mode, and viewport
  screenshots
- editor events, long operations as jobs with progress notifications, and commands that a game registers

C# navigation and refactoring remain the job of an IDE-integrated MCP such as Rider. FlaxMCP focuses on engine operations
and editor state that a conventional code model cannot see.

## Tools

The default configuration has 55 tools in ten toolsets. `server` is always enabled. You can select the other toolsets
with `FLAX_TOOLSETS`.

| Toolset | Tools |
|---|---|
| `server` | `server_info`, `flax_status` |
| `project` | `project_info`, `project_targets`, `project_settings` |
| `content` | `content_search`, `content_asset_info`, `content_resolve_guid`, `content_material_details`, `content_create_material`, `content_create_material_instance`, `content_set_material_instance_parameter` |
| `scene` | `scene_list`, `scene_outline`, `scene_find_actor` |
| `build` | `build_check_scripts`, `build_generate_projects`, `build_compile_scripts`, `build_clear_cache`, `build_game` |
| `logs` | `logs_tail`, `logs_errors` |
| `editor` | `editor_scene_graph`, `editor_get_selection`, `editor_set_selection`, `editor_actor_details`, `editor_modify_actor`, `editor_create_actor`, `editor_duplicate_actor`, `editor_rename_actor`, `editor_reparent_actor`, `editor_delete_actor`, `editor_static_model_details`, `editor_set_static_model`, `editor_set_static_model_material`, `editor_box_collider_details`, `editor_create_box_collider`, `editor_set_box_collider`, `editor_save`, `editor_play_mode`, `editor_screenshot`, `editor_compile`, `editor_open_scene`, `editor_host_status`, `editor_find_actors`, `editor_get_member`, `editor_set_member`, `editor_add_script` |
| `events` | `editor_events`, `editor_wait_for` |
| `jobs` | `editor_job_start`, `job_status`, `job_wait` |
| `game` | `game_commands`, `game_command` |

Two more editor tools, `editor_execute_csharp` and `editor_invoke_method`, are not available by default. They are
available only when `FLAX_ALLOW_CODE_EXECUTION=true` and read-only mode is off.

### Offline inspection

`project_info` reads the `.flaxproj`. `project_targets` parses the declarations in `*.Build.cs`. `project_settings`
returns structured settings assets. The content tools keep an index of `Content/` in memory, with GUID and type metadata
from the headers of Flax 1.12 binary assets. A binary asset that the tools do not recognize stays searchable by its
path, without metadata.

The scene tools parse `.scene` and `.prefab` files and do not start the editor. Actor trees follow the `ParentID`
relations and attach scripts to their owners. The result tells when a response limit truncates it.

### Script checks and builds

`build_check_scripts` compiles the script modules of the project outside the editor. It uses the compiler arguments that
Flax.Build wrote last (`Cache/Intermediate/<EditorTarget>/<Platform>/<Arch>/<Config>/<Module>.CSharp.response`) and the
current files of the modules. It compiles through the compiler server of the .NET SDK into a scratch folder. The errors
come back as SARIF, structured (file, line, column, code), in less than a second. The check works while the editor is
open, also in play mode, and it does not change the project.

`build_generate_projects`, `build_compile_scripts`, `build_clear_cache` and `build_game` run the editor headlessly.
Script compilation returns structured diagnostics, not the raw log. These four tools do not start while the same project
has a live editor session. Close the GUI editor before you use them. `logs_tail` and `logs_errors` read the newest
project log, also while the editor runs.

`build_game` can take minutes, so it starts a job and returns its handle immediately:

1. Call `build_game` with a preset and a target, for example `Development` and `Windows`.
2. Call `job_wait` with the job ID, or read `job_status`. `job_wait` sends progress notifications and returns the state
   when the job ends or when the wait times out.
3. The `result` of the ended job is the build outcome. A failed job contains its `error`.

The `diagnose_build_failure` prompt combines `build_compile_scripts` with `logs_errors`. It tells compiler failures apart
from engine or project configuration errors.

### Live editor bridge

The `editor` tools communicate with the companion plugin through a local named pipe. `flax_status` reports whether the
configured project has a reachable, protocol-compatible session. A live call fails with a clear error when the editor or
the plugin is not running, or when the editor process that opened the session has exited.

The pipe belongs to the bridge host (`src/FlaxMcpBridge/Host`). The plugin compiles the host once for each editor process
into the Default load context, so script reloads do not stop it. Each reloaded plugin attaches to the host again, and
requests that arrive in the meantime wait for it. The host answers a request that a reload interrupts with
`interrupted_by_reload`, and the server sends read-only requests again. The host serves clients at the same time. A
changed host takes the pipe from the old one. `editor_host_status` reports the host, the plugin generation, the memory of
the editor and the number of old plugin load contexts that are still alive.

Live scene reads include unsaved state. The write tools change the selection and transforms, create, rename, reparent and
delete actors, and save the changed scenes and assets. `editor_play_mode` starts, pauses, resumes or stops play mode and
returns when the editor is in the requested state. `editor_open_scene` opens a scene and returns when the scene is
loaded. `editor_screenshot` writes a PNG of the visible scene viewport. It needs a GUI editor with a rendered viewport and
does not work headlessly. Its destination directory must exist.

`editor_compile` runs `build_check_scripts` first. After a clean check, the editor compiles the scripts, and the tool
returns when the editor reloads them and is idle. The outcome is `reloaded`, `unchanged` or `failed`. The tool stops
play mode first.

### Live objects

`editor_find_actors` finds actors in the loaded scenes by name, actor type and script type. `editor_get_member` reads a
field or property of an actor, or of one of its scripts, through a dotted path (for example, `Stats.Score`).
`editor_set_member` writes a value through such a path. In edit mode, the change goes through the undo of the editor and
marks the scene as edited, so that `editor_save` keeps it. `editor_add_script` adds a script to an actor.
`editor_invoke_method` calls an instance method or a static method on the main thread. It is available only when code
execution is allowed.

`editor_execute_csharp` compiles and runs a C# snippet on the main thread of the editor. The host loads the compiler
once and keeps a cache of the references and the compiled snippets, so the cache stays through script reloads.

### Editor events

The host records the events of the editor in a ring of 4096 events: managed log lines, exceptions, compiles, script
reloads, play mode changes, and scene loads, saves and unloads. `editor_events` returns the events after a sequence
number. `editor_wait_for` waits for the first event of a kind, optionally with a text. Both tools work also during a
script reload. Native engine lines, for example compiler output, do not reach the ring. `logs_tail` and `logs_errors`
read them from the log file.

### Jobs

Long operations run as jobs. `editor_job_start` starts a compile, a play mode change or a scene open and returns
immediately. `editor_compile` and `editor_play_mode` also run as jobs. Only one editor job runs at a time, and a second
one fails with `job_conflict`. `job_status` returns the state of a job, and `job_wait` waits for its end and sends its
progress as MCP progress notifications. A timeout of `job_wait` is a result, not an error.

### Game commands

A game can give named commands to an MCP client. Add the runtime module of the bridge to the game module, and register
the commands from a script:

```csharp
// Game.Build.cs, in Setup: options.PrivateDependencies.Add("FlaxMcpBridge");
using FlaxEngine;
using FlaxMcpBridge;

public class HealthCommands : Script
{
    private GameCommandRegistration _heal;

    public override void OnEnable()
    {
        _heal = GameCommands.Register("heal", "Adds health points. Argument: the number of points.",
            argument => $"healed {argument}");
    }

    public override void OnDisable()
    {
        _heal.Dispose();
    }
}
```

`game_commands` lists the registered commands. `game_command` runs one on the main thread and returns its text result.
A handler must return quickly, because a bridge request stops after 5 seconds. A script reload clears the list, and the
scripts register their commands again.

## Requirements

- .NET 10 SDK
- Flax Engine 1.12.6912 or newer installed locally (a standard engine or an engine built with large worlds)
- a Flax project (`.flaxproj`)
- the companion plugin installed in that project for the live editor tools

## Setup from source

### 1. Get and validate the server

```bash
git clone https://github.com/eXoz00rd/FlaxMCP.git
cd FlaxMCP
dotnet build FlaxMcp.slnx
dotnet test FlaxMcp.slnx
dotnet format --verify-no-changes
```

### 2. Install the editor bridge

Add the bridge project to the `References` array of the consumer project. Use an absolute path or a path relative to the
consumer `.flaxproj`:

```json
{
  "References": [
    { "Name": "$(EnginePath)/Flax.flaxproj" },
    { "Name": "../FlaxMCP/src/FlaxMcpBridge/FlaxMcpBridge.flaxproj" }
  ]
}
```

Keep the existing references and change only the bridge path. Flax finds the `EditorPlugin` through this reference. The
consumer needs no `PrivateDependencies` or `Modules.Add` entry for the editor tools. Start or reload the GUI editor and
call `flax_status` to make sure that it returns `connected: true`.

### 3. Configure an MCP client

Use the checkout as the process target. This JSON shape works for clients that accept an `mcpServers` map, including
Claude Code configuration files:

```json
{
  "mcpServers": {
    "flax": {
      "command": "dotnet",
      "args": ["run", "--project", "D:/Tools/FlaxMCP/src/FlaxMcp"],
      "env": {
        "FLAX_PROJECT_PATH": "D:/Projects/YourFlaxProject"
      }
    }
  }
}
```

In the MCP server settings of Rider, create a stdio server with the same command, arguments, checkout, and environment
variables. Use absolute paths, because GUI clients do not always inherit the working directory of the shell.

Call `server_info`, `project_info`, and `flax_status` as a basic check.

## Configuration

| Variable | Required | Description |
|---|---|---|
| `FLAX_PROJECT_PATH` | yes | Path to a `.flaxproj`, or a directory that contains exactly one top-level `.flaxproj` |
| `FLAX_ENGINE_PATH` | no | Flax Engine root. Without it, the server uses the registrations of the Flax Launcher in `%APPDATA%/Flax/Versions.txt` |
| `FLAX_EDITOR_CONFIG` | no | Editor binary configuration: `Development` (default), `Debug`, or `Release`. Without it, `build_check_scripts` reads the compiler arguments of the newest editor build |
| `FLAX_TOOLSETS` | no | Comma-separated selection from `project`, `content`, `scene`, `build`, `logs`, `editor`, `events`, `jobs`, and `game`. `server` is always included. Omit it for all toolsets |
| `FLAX_READ_ONLY` | no | `true`, `1`, or `yes` removes all tools that can change files or editor state |
| `FLAX_BRIDGE` | no | Bridge mode placeholder, `auto` by default. The server parses it, but it does not change the connection |
| `FLAX_ALLOW_CODE_EXECUTION` | no | Enables `editor_execute_csharp` and `editor_invoke_method` only when it is true and `FLAX_READ_ONLY` is false |
| `FLAX_LOG_LEVEL` | no | Minimum level of the server log on stderr, `Warning` by default |

An unknown toolset name stops the server at startup, and the error lists the valid names. With `FLAX_READ_ONLY=true`,
these tools remain: `server_info`, `flax_status`, the project and scene tools, `build_check_scripts`, `content_search`,
`content_asset_info`, `content_resolve_guid`, `content_material_details`, both log tools, `editor_scene_graph`,
`editor_get_selection`, `editor_actor_details`, `editor_static_model_details`, `editor_box_collider_details`,
`editor_host_status`, `editor_find_actors`, `editor_get_member`, `editor_events`, `editor_wait_for`, `job_status`,
`job_wait`, and `game_commands`.

## Security

- The bridge accepts local named-pipe connections and writes discovery data to `%APPDATA%/FlaxMcp/sessions`. It does not
  need network credentials.
- The build and editor write tools run with the file and process permissions of the current user. Use
  `FLAX_READ_ONLY=true` for clients that only inspect.
- `editor_execute_csharp` compiles and runs any C# on the main thread of the editor, with the full permissions of the Flax
  Editor process on the machine. This includes access to files, processes, credentials, and the network.
  `editor_invoke_method` can call any method, so it can also run any code with these permissions. Enable code execution
  only for trusted clients and prompts.

## Troubleshooting

### `flax_status` reports `connected: false`

Make sure that the configured project is the project open in Flax, that its `.flaxproj` has the bridge reference, and
that the editor has compiled the scripts. Reload the scripts or restart the editor after you add the plugin. Look in the
latest project log for `[FlaxMcpBridge] Attached to the bridge host`, or for `[FlaxMcpBridge] Failed to attach to the
bridge host` and a plugin compilation error.

### Protocol mismatch

The server and the editor plugin come from incompatible revisions. Update both from the same checkout, let Flax compile
the plugin again, and reload the scripts. The server rejects a mismatch with an error. It does not use an incompatible
protocol.

### A build says the editor is already running

The headless build tools do not run at the same time as a GUI editor that uses the same project and `Cache/` directory.
Close that editor session normally and try again. The log tools, `build_check_scripts` and the live editor tools do not
need this step.

### Screenshot capture fails

Open a scene in the GUI editor and make sure that its scene viewport is visible. The capture needs a rendered viewport,
cannot run headlessly, accepts only a `.png` path, and does not create the destination directory.

## Roadmap

The source alpha covers offline inspection, the build and log tools, and the live editor bridge. The remaining release
work includes clean installation tests, NuGet publication, MCP registry metadata, and a final check of the
documentation. See the [project board](https://github.com/users/eXoz00rd/projects/3) for the current status.

## Contributing

- **[Contributing Guide](CONTRIBUTING.md)** — branch flow, commits, code style, CI
- **[Task Conventions](CONVENTIONS.md)** — writing issues and definitions of done
- **[Agent Instructions](AGENTS.md)** — single source of truth for AI tools working on this repository

## License

[MIT](LICENSE)
