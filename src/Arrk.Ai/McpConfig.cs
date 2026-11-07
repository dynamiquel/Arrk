using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Arrk.Ai;

/// <summary>MCP serves Arrk tools without calling a model or requiring ai.enabled.</summary>
public sealed class McpConfig
{
    [Description("Expose Arrk tools at /mcp over Streamable HTTP. Off by default. Use HTTPS and configure a client credential before enabling.")]
    public bool Enabled { get; set; }

    [Description("Dedicated MCP clients. Each bearer token acts as one existing Arrk user; account permissions are checked live on every request.")]
    public List<McpClientConfig> Clients { get; set; } = [];
}

public sealed class McpClientConfig
{
    public string Name { get; set; } = "";

    [Description("Existing Arrk user id. Disabled, deleted and pending accounts cannot use MCP.")]
    public int UserId { get; set; }

    [Description("A unique random bearer token of at least 32 characters. Store securely; never reuse an AI provider API key. Rotate by replacing this value.")]
    [DataType(DataType.Password)]
    public string Token { get; set; } = "";

    [Description("Allow service-backed write tools for this client, subject to the user's permissions. Off means only known read tools are exposed. Delete/quality confirmation still happens in the Arrk UI.")]
    public bool AllowWrites { get; set; }
}
