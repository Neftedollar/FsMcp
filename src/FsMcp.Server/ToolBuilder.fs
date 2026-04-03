namespace FsMcp.Server

open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation

/// A pre-built typed handler with its associated JSON schema.
type TypedHandlerInfo = {
    RawHandler: Map<string, JsonElement> -> Task<Result<Content list, McpError>>
    Schema: JsonElement
}

/// Helper to create a TypedHandlerInfo from a typed handler function.
module TypedHandler =
    let private deserializerOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    /// Create a TypedHandlerInfo that auto-generates the JSON schema from the record type.
    let create<'TArgs> (handler: 'TArgs -> Task<Result<Content list, McpError>>) : TypedHandlerInfo =
        let schema = SchemaGen.generateSchema<'TArgs>()
        let rawHandler (args: Map<string, JsonElement>) = task {
            try
                let jsonObj = JsonObject()
                for kv in args do
                    jsonObj.[kv.Key] <- JsonNode.Parse(kv.Value.GetRawText())
                let typedArgs = JsonSerializer.Deserialize<'TArgs>(jsonObj.ToJsonString(), deserializerOptions)
                return! handler typedArgs
            with ex ->
                return Error (HandlerException ex)
        }
        { RawHandler = rawHandler; Schema = schema }

/// State accumulated during mcpTool CE execution.
type ToolBuilderState = {
    Name: string option
    Description: string option
    Handler: (Map<string, JsonElement> -> Task<Result<Content list, McpError>>) option
    InputSchema: JsonElement option
}

/// Computation expression builder for defining MCP tools.
type ToolCEBuilder() =
    member _.Yield(_) : ToolBuilderState =
        { Name = None; Description = None; Handler = None; InputSchema = None }

    /// Set the tool name.
    [<CustomOperation("toolName")>]
    member _.ToolName(state: ToolBuilderState, n: string) = { state with Name = Some n }

    /// Set the tool description.
    [<CustomOperation("description")>]
    member _.Description(state: ToolBuilderState, d: string) = { state with Description = Some d }

    /// Set a raw handler (Map<string, JsonElement> -> Task<Result<Content list, McpError>>).
    [<CustomOperation("handler")>]
    member _.Handler(state: ToolBuilderState, h) = { state with Handler = Some h }

    /// Set a typed handler with auto-generated schema.
    /// Use TypedHandler.create<'T> to build the info, or pass a lambda with
    /// a type annotation to let the compiler infer the type.
    [<CustomOperation("typedHandler")>]
    member _.TypedHandler(state: ToolBuilderState, info: TypedHandlerInfo) =
        { state with Handler = Some info.RawHandler; InputSchema = Some info.Schema }

    member _.Run(state: ToolBuilderState) : ToolDefinition =
        let name = state.Name |> Option.defaultWith (fun () -> failwith "Tool name is required")
        let desc = state.Description |> Option.defaultValue ""
        let handler = state.Handler |> Option.defaultWith (fun () -> failwith "Tool handler is required")
        let tn = ToolName.create name |> Result.defaultWith (fun e -> failwith $"%A{e}")
        { Name = tn; Description = desc; InputSchema = state.InputSchema; Handler = handler }

/// AutoOpen module to expose the mcpTool CE instance.
[<AutoOpen>]
module ToolCE =
    let mcpTool = ToolCEBuilder()
