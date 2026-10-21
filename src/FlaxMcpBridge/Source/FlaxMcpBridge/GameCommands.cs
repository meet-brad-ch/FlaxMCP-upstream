#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FlaxMcpBridge;

/// <summary>
/// Named commands a game registers for the FlaxMcp bridge: an MCP client lists them (<c>game_commands</c>) and runs
/// them (<c>game_command</c>) in edit mode and in play mode. A handler runs on the main thread, takes the client's
/// argument string and returns a text result; it must return quickly (the bridge request times out after 5 seconds).
/// </summary>
/// <remarks>
/// The registry is static because game scripts register from their own lifetimes (OnEnable/OnDisable) and the
/// bridge has no scene object to hold it; a script reload starts it empty and the scripts register again. Reads take
/// a snapshot, so listing never blocks and the game makes no garbage while nothing registers.
/// </remarks>
public static class GameCommands
{
    private static readonly object Gate = new();
    private static GameCommand[] _commands = Array.Empty<GameCommand>();

    /// <summary>Registers a command until the returned registration is disposed.</summary>
    /// <param name="name">Lowercase letters, digits and dashes, starting with a letter.</param>
    /// <param name="description">What the command does and what its argument means.</param>
    /// <param name="handler">Runs the command on the main thread: argument string in, text result out.</param>
    /// <returns>Disposing it removes the command.</returns>
    /// <exception cref="ArgumentException">The name breaks the naming rule or the description is empty.</exception>
    /// <exception cref="InvalidOperationException">A command with this name is already registered.</exception>
    public static GameCommandRegistration Register(string name, string description, Func<string, string> handler)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"Game command name '{name}' must be lowercase letters, digits and dashes, starting with a letter.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException($"Game command '{name}' needs a description.", nameof(description));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        var command = new GameCommand(new GameCommandInfo(name, description, OwnerOf(handler)), handler);
        lock (Gate)
        {
            if (_commands.Any(existing => existing.Info.Name == name))
            {
                throw new InvalidOperationException($"Game command '{name}' is already registered by {Find(name)!.Info.Owner}.");
            }

            Volatile.Write(ref _commands, _commands.Append(command).ToArray());
        }

        return new GameCommandRegistration(command);
    }

    /// <summary>The registered commands, sorted by name.</summary>
    /// <returns>A snapshot.</returns>
    public static IReadOnlyList<GameCommandInfo> List() =>
        Volatile.Read(ref _commands).Select(command => command.Info).OrderBy(info => info.Name, StringComparer.Ordinal).ToArray();

    /// <summary>Finds a command by name.</summary>
    /// <param name="name">The command name.</param>
    /// <returns>The command, or null when none is registered under the name.</returns>
    public static GameCommand? Find(string name) =>
        Volatile.Read(ref _commands).FirstOrDefault(command => command.Info.Name == name);

    /// <summary>The type that wrote the handler: a lambda's compiler-made closure class names its enclosing type.</summary>
    private static string OwnerOf(Delegate handler)
    {
        var type = handler.Method.DeclaringType;
        while (type is not null && type.Name.Contains('<') && type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type?.FullName ?? "?";
    }

    private static bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] < 'a' || name[0] > 'z')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    internal static void Unregister(GameCommand command)
    {
        lock (Gate)
        {
            Volatile.Write(ref _commands, _commands.Where(existing => !ReferenceEquals(existing, command)).ToArray());
        }
    }
}

/// <summary>What a client sees of a game command.</summary>
public sealed class GameCommandInfo
{
    /// <summary>Creates the description of a command.</summary>
    /// <param name="name">The command name.</param>
    /// <param name="description">What it does and what its argument means.</param>
    /// <param name="owner">The type that declared the handler.</param>
    public GameCommandInfo(string name, string description, string owner)
    {
        Name = name;
        Description = description;
        Owner = owner;
    }

    /// <summary>The command name.</summary>
    public string Name { get; }

    /// <summary>What it does and what its argument means.</summary>
    public string Description { get; }

    /// <summary>The type that declared the handler.</summary>
    public string Owner { get; }
}

/// <summary>A registered command: its description and its handler.</summary>
public sealed class GameCommand
{
    internal GameCommand(GameCommandInfo info, Func<string, string> handler)
    {
        Info = info;
        Handler = handler;
    }

    /// <summary>What a client sees.</summary>
    public GameCommandInfo Info { get; }

    /// <summary>Runs the command: argument string in, text result out. Main thread only.</summary>
    public Func<string, string> Handler { get; }
}

/// <summary>Keeps a command registered until disposed.</summary>
public sealed class GameCommandRegistration : IDisposable
{
    private GameCommand? _command;

    internal GameCommandRegistration(GameCommand command)
    {
        _command = command;
    }

    /// <summary>Removes the command; disposing twice does nothing more.</summary>
    public void Dispose()
    {
        var command = Interlocked.Exchange(ref _command, null);
        if (command is not null)
        {
            GameCommands.Unregister(command);
        }
    }
}
