namespace FsMcp.Client

/// Transport configuration for connecting to MCP servers.
type ClientTransport =
    | StdioProcess of command: string * args: string list
    | HttpEndpoint of uri: System.Uri * headers: Map<string, string>

/// Convenience constructors for ClientTransport.
module ClientTransport =
    /// Create a stdio transport (launches server as child process).
    let stdio (command: string) (args: string list) : ClientTransport =
        StdioProcess (command, args)

    /// Create an HTTP transport.
    let http (uri: string) : ClientTransport =
        HttpEndpoint (System.Uri(uri), Map.empty)

    /// Create an HTTP transport with auth headers.
    let httpWithHeaders (uri: string) (headers: Map<string, string>) : ClientTransport =
        HttpEndpoint (System.Uri(uri), headers)
