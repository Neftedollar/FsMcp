namespace FsMcp.Server

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation

/// A pre-built typed handler with its associated JSON schema.
[<NoComparison; NoEquality>]
type TypedHandlerInfo = {
    RawHandler: Map<string, JsonElement> -> CancellationToken -> Task<Result<Content list, McpError>>
    Schema: JsonElement
}

/// Helper to create a TypedHandlerInfo from a typed handler function.
module TypedHandler =
    let private deserializerOptions = TypedDeserializer.strictToolOptions ()

    /// Create a TypedHandlerInfo that auto-generates the JSON schema from the record type.
    let create<'TArgs>
        (handler: 'TArgs -> CancellationToken -> Task<Result<Content list, McpError>>)
        : TypedHandlerInfo =
        let schema = SchemaGen.generateSchema<'TArgs>()
        let rawHandler (args: Map<string, JsonElement>) (cancellationToken: CancellationToken) = task {
            let deserialized =
                if not (TypedDeserializer.hasRequiredArguments schema args) then
                    Error(TypedDeserializer.invalidArguments "tool")
                else
                    try
                        let jsonObject = JsonObject()
                        for keyValue in args do
                            jsonObject.[keyValue.Key] <- JsonNode.Parse(keyValue.Value.GetRawText())
                        Ok(JsonSerializer.Deserialize<'TArgs>(jsonObject.ToJsonString(), deserializerOptions))
                    with :? JsonException ->
                        Error(TypedDeserializer.invalidArguments "tool")

            match deserialized with
            | Error error -> return raise error
            | Ok typedArgs ->
                try
                    return! handler typedArgs cancellationToken
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return raise (OperationCanceledException(cancellationToken))
                | error -> return Error(HandlerException error)
        }
        { RawHandler = rawHandler; Schema = schema }

/// State accumulated during mcpTool CE execution.
[<NoComparison; NoEquality>]
type ToolBuilderState = {
    Name: string option
    Description: string option
    Handler: (Map<string, JsonElement> -> CancellationToken -> Task<Result<Content list, McpError>>) option
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

    /// Set a cancellation-aware raw handler.
    [<CustomOperation("handler")>]
    member _.Handler(state: ToolBuilderState, h) = { state with Handler = Some h }

    /// Set a typed handler with auto-generated schema.
    /// Use TypedHandler.create<'T> to build the info, or pass a lambda with
    /// a type annotation to let the compiler infer the type.
    [<CustomOperation("typedHandler")>]
    member _.TypedHandler(state: ToolBuilderState, info: TypedHandlerInfo) =
        { state with Handler = Some info.RawHandler; InputSchema = Some info.Schema }

    member _.Run(state: ToolBuilderState) : ToolDefinition =
        let name =
            state.Name
            |> Option.defaultWith (fun () ->
                raise (FsMcpConfigException "Tool name is required. Add 'toolName \"myTool\"' to your mcpTool { } block."))
        let desc = state.Description |> Option.defaultValue ""
        let handler =
            state.Handler
            |> Option.defaultWith (fun () ->
                raise (FsMcpConfigException "Tool handler is required. Add 'handler (fun args cancellationToken -> ...)' or 'typedHandler (TypedHandler.create<Args> ...)' to your mcpTool { } block."))
        let tn =
            ToolName.create name
            |> Result.defaultWith (fun e ->
                raise (FsMcpConfigException $"Invalid tool name '{name}': %A{e}. Tool name must be non-empty."))
        { Name = tn; Description = desc; InputSchema = state.InputSchema; Handler = handler }

/// AutoOpen module to expose the mcpTool CE instance.
[<AutoOpen>]
module ToolCE =
    let mcpTool = ToolCEBuilder()
