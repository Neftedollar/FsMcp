namespace FsMcp.Server

open FsMcp.Core
open FsMcp.Core.Validation

/// Transport configuration for the MCP server.
type Transport =
    | Stdio
    | Http of endpoint: string option

/// Middleware type: receives context and next handler, returns response.
type McpMiddleware =
    McpContext -> (McpContext -> System.Threading.Tasks.Task<McpResponse>) -> System.Threading.Tasks.Task<McpResponse>

/// Context available to middleware.
and McpContext = {
    Method: string
    Params: System.Text.Json.JsonElement option
    CancellationToken: System.Threading.CancellationToken
}

/// Response from middleware pipeline.
and McpResponse =
    | Success of System.Text.Json.JsonElement
    | McpResponseError of McpError

/// Complete server configuration built by the CE.
type ServerConfig = {
    Name: ServerName
    Version: ServerVersion
    Tools: ToolDefinition list
    Resources: ResourceDefinition list
    Prompts: PromptDefinition list
    Middleware: McpMiddleware list
    Transport: Transport
}

/// Validation of ServerConfig (uniqueness checks).
module ServerConfig =
    /// Validate that a ServerConfig has no duplicate names.
    let validate (config: ServerConfig) : Result<ServerConfig, ValidationError> =
        let toolNames =
            config.Tools |> List.map (fun t -> ToolName.value t.Name)
        let resourceUris =
            config.Resources |> List.map (fun r -> ResourceUri.value r.Uri)
        let promptNames =
            config.Prompts |> List.map (fun p -> PromptName.value p.Name)

        let findDuplicate (kind: string) (items: string list) =
            items
            |> List.groupBy id
            |> List.tryFind (fun (_, group) -> List.length group > 1)
            |> Option.map (fun (name, _) -> DuplicateEntry(kind, name))

        match findDuplicate "Tool" toolNames with
        | Some err -> Error err
        | None ->
            match findDuplicate "Resource" resourceUris with
            | Some err -> Error err
            | None ->
                match findDuplicate "Prompt" promptNames with
                | Some err -> Error err
                | None -> Ok config

/// Builder state accumulated during CE execution.
type ServerBuilderState = {
    Name: ServerName option
    Version: ServerVersion option
    Tools: ToolDefinition list
    Resources: ResourceDefinition list
    Prompts: PromptDefinition list
    Middleware: McpMiddleware list
    Transport: Transport
}

module ServerBuilderState =
    let empty = {
        Name = None
        Version = None
        Tools = []
        Resources = []
        Prompts = []
        Middleware = []
        Transport = Stdio
    }

/// Computation expression builder for MCP servers.
type McpServerBuilder() =
    member _.Yield(_) = ServerBuilderState.empty

    /// Set server name.
    [<CustomOperation("name")>]
    member _.Name(state: ServerBuilderState, n: string) =
        match ServerName.create n with
        | Ok sn -> { state with Name = Some sn }
        | Error e -> failwith $"Invalid server name: %A{e}"

    /// Set server version.
    [<CustomOperation("version")>]
    member _.Version(state: ServerBuilderState, v: string) =
        match ServerVersion.create v with
        | Ok sv -> { state with Version = Some sv }
        | Error e -> failwith $"Invalid server version: %A{e}"

    /// Add a tool definition.
    [<CustomOperation("tool")>]
    member _.Tool(state: ServerBuilderState, td: ToolDefinition) =
        { state with Tools = state.Tools @ [td] }

    /// Add a resource definition.
    [<CustomOperation("resource")>]
    member _.Resource(state: ServerBuilderState, rd: ResourceDefinition) =
        { state with Resources = state.Resources @ [rd] }

    /// Add a prompt definition.
    [<CustomOperation("prompt")>]
    member _.Prompt(state: ServerBuilderState, pd: PromptDefinition) =
        { state with Prompts = state.Prompts @ [pd] }

    /// Add middleware.
    [<CustomOperation("middleware")>]
    member _.Middleware(state: ServerBuilderState, mw: McpMiddleware) =
        { state with Middleware = state.Middleware @ [mw] }

    /// Use stdio transport.
    [<CustomOperation("useStdio")>]
    member _.UseStdio(state: ServerBuilderState) =
        { state with Transport = Stdio }

    /// Use HTTP transport with optional endpoint.
    [<CustomOperation("useHttp")>]
    member _.UseHttp(state: ServerBuilderState, endpoint: string option) =
        { state with Transport = Http endpoint }

    member _.Run(state: ServerBuilderState) : ServerConfig =
        let name =
            state.Name
            |> Option.defaultWith (fun () -> failwith "Server name is required")
        let version =
            state.Version
            |> Option.defaultWith (fun () -> failwith "Server version is required")
        let config : ServerConfig = {
            Name = name
            Version = version
            Tools = state.Tools
            Resources = state.Resources
            Prompts = state.Prompts
            Middleware = state.Middleware
            Transport = state.Transport
        }
        match ServerConfig.validate config with
        | Ok c -> c
        | Error e -> failwith $"Server configuration validation failed: %A{e}"

/// CE instance for building MCP servers.
[<AutoOpen>]
module ServerBuilderCE =
    let mcpServer = McpServerBuilder()
