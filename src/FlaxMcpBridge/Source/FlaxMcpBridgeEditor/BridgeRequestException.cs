using System;

namespace FlaxMcpBridge.Editor;

/// <summary>A request failure with its own error code for the client (other failures are <c>request_failed</c>).</summary>
internal sealed class BridgeRequestException : Exception
{
    /// <summary>Creates the failure.</summary>
    /// <param name="code">The error code the client sees.</param>
    /// <param name="message">What went wrong.</param>
    public BridgeRequestException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The error code the client sees.</summary>
    public string Code { get; }
}
