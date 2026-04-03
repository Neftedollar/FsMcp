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

    /// Registry for contextual tool handlers, keyed by tool name.
    /// Stores the pre-wrapped handler that accepts (HandlerContext, Map<string, JsonElement>).
    let private contextHandlerRegistry =
        System.Collections.Concurrent.ConcurrentDictionary<string, HandlerContext -> Map<string, JsonElement> -> Task<Result<Content list, McpError>>>()

    /// Contextual tool definitions that receive a HandlerContext for sending notifications.
    module ContextualTool =

        let private deserializerOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

        /// Define a tool whose handler receives a HandlerContext for sending notifications.
        /// When invoked through the standard Handler, a no-op context is used.
        /// Use invokeWithContext to supply a real context at runtime.
        let define<'TArgs>
            (name: string)
            (description: string)
            (handler: HandlerContext -> 'TArgs -> Task<Result<Content list, McpError>>)
            : Result<ToolDefinition, ValidationError> =
            let schema = SchemaGen.generateSchema<'TArgs>()

            // Pre-wrap: deserialize args once, then call the typed handler with any context.
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

            // The standard handler uses a no-op context.
            let rawHandler (args: Map<string, JsonElement>) =
                contextAwareHandler HandlerContext.noOp args

            match ToolName.create name with
            | Ok tn ->
                contextHandlerRegistry.[ToolName.value tn] <- contextAwareHandler
                Ok { Name = tn; Description = description; InputSchema = Some schema; Handler = rawHandler }
            | Result.Error e -> Result.Error e

        /// Invoke a contextual tool with a specific HandlerContext.
        /// This allows wiring up real progress/log notification senders at runtime.
        let invokeWithContext
            (ctx: HandlerContext)
            (tool: ToolDefinition)
            (args: Map<string, JsonElement>)
            : Task<Result<Content list, McpError>> =
            let toolName = ToolName.value tool.Name
            match contextHandlerRegistry.TryGetValue(toolName) with
            | true, contextHandler -> contextHandler ctx args
            | false, _ ->
                // Fallback to standard handler (no context available).
                tool.Handler args
