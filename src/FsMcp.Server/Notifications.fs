namespace FsMcp.Server

open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation

/// Notification types and helpers for MCP server-to-client notifications.
module Notifications =

    /// Progress notification data.
    type ProgressUpdate = {
        Progress: float      // 0.0 to 1.0
        Message: string option
    }

    /// Log level for log notifications.
    type McpLogLevel =
        | Debug
        | Info
        | Warning
        | Error

    /// Log notification data.
    type LogEntry = {
        Level: McpLogLevel
        Message: string
        Logger: string option
    }

    /// A handler context that includes notification capabilities.
    [<NoComparison; NoEquality>]
    type HandlerContext = {
        /// Send a progress notification to the client.
        ReportProgress: ProgressUpdate -> Task<unit>
        /// Send a log notification to the client.
        Log: LogEntry -> Task<unit>
        /// Cancellation token for the request.
        CancellationToken: CancellationToken
    }

    /// Helpers for HandlerContext.
    module HandlerContext =
        /// A no-op context where ReportProgress and Log do nothing.
        let noOp : HandlerContext = {
            ReportProgress = fun _ -> Task.FromResult(())
            Log = fun _ -> Task.FromResult(())
            CancellationToken = CancellationToken.None
        }

    /// Per-tool handler storage. Each ContextualTool.define call returns a
    /// ToolDefinition whose Handler closes over its own context-aware handler,
    /// avoiding a process-global registry that causes test isolation issues.

    /// A contextual tool definition that captures its context-aware handler
    /// in a closure (no global registry — test-safe).
    [<NoComparison; NoEquality>]
    type ContextualToolHandle = {
        /// The ToolDefinition for registration with the server.
        Definition: ToolDefinition
        /// Invoke with a specific HandlerContext (for testing or runtime wiring).
        InvokeWithContext: HandlerContext -> Map<string, JsonElement> -> Task<Result<Content list, McpError>>
    }

    /// Contextual tool definitions that receive a HandlerContext for sending notifications.
    module ContextualTool =

        let private deserializerOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

        /// Define a tool whose handler receives a HandlerContext for sending notifications.
        /// Returns a ContextualToolHandle with both the ToolDefinition and a way to invoke with context.
        let define<'TArgs>
            (name: string)
            (description: string)
            (handler: HandlerContext -> 'TArgs -> Task<Result<Content list, McpError>>)
            : Result<ContextualToolHandle, ValidationError> =
            let schema = SchemaGen.generateSchema<'TArgs>()

            let contextAwareHandler (ctx: HandlerContext) (args: Map<string, JsonElement>) = task {
                try
                    let jsonObj = JsonObject()
                    for kv in args do
                        jsonObj.[kv.Key] <- JsonNode.Parse(kv.Value.GetRawText())
                    let json = jsonObj.ToJsonString()
                    let typedArgs = JsonSerializer.Deserialize<'TArgs>(json, deserializerOptions)
                    return! handler ctx typedArgs
                with ex ->
                    return Result.Error (HandlerException ex)
            }

            let rawHandler (args: Map<string, JsonElement>) =
                contextAwareHandler HandlerContext.noOp args

            match ToolName.create name with
            | Ok tn ->
                let td = { Name = tn; Description = description; InputSchema = Some schema; Handler = rawHandler }
                Ok { Definition = td; InvokeWithContext = contextAwareHandler }
            | Result.Error e -> Result.Error e

        /// Invoke a contextual tool with a specific HandlerContext.
        let invokeWithContext
            (ctx: HandlerContext)
            (handle: ContextualToolHandle)
            (args: Map<string, JsonElement>)
            : Task<Result<Content list, McpError>> =
            handle.InvokeWithContext ctx args
