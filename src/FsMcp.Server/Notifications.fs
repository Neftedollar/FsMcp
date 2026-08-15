namespace FsMcp.Server

open System.Text.Json
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

    /// Low-level storage for manually constructed contextual handlers in tests.
    /// This type is not connected to the MCP transport or SDK request context.
    [<NoComparison; NoEquality>]
    type ContextualToolHandle = {
        /// A manually supplied definition; this field does not add transport wiring.
        Definition: ToolDefinition
        /// Invoke with an explicit test context; no context is obtained from the transport.
        InvokeWithContext: HandlerContext -> Map<string, JsonElement> -> Task<Result<Content list, McpError>>
    }

    /// Contextual tool definitions that receive a HandlerContext for sending notifications.
    module ContextualTool =

        let private unavailableMessage =
            "ContextualTool transport wiring was never implemented in FsMcp 1.x and no longer falls back to no-op notifications. Use invokeWithContext only in an explicit/manual test context; production support requires a future SDK RequestContext-based API."

        /// Retained as a fail-closed migration entry point. It never constructs a
        /// transport-backed handle; production notification support requires a
        /// future SDK request-context integration.
        [<System.Obsolete("ContextualTool transport wiring was never implemented and now fails closed. Use explicit SDK RequestContext APIs for production notifications.")>]
        let define<'TArgs>
            (name: string)
            (description: string)
            (handler: HandlerContext -> 'TArgs -> Task<Result<Content list, McpError>>)
            : Result<ContextualToolHandle, ValidationError> =
            ignore name
            ignore description
            ignore handler
            raise (FsMcpConfigException unavailableMessage)

        /// Invoke a manually constructed handle with an explicit test context.
        /// This helper does not provide MCP transport or SDK request-context wiring.
        let invokeWithContext
            (ctx: HandlerContext)
            (handle: ContextualToolHandle)
            (args: Map<string, JsonElement>)
            : Task<Result<Content list, McpError>> =
            handle.InvokeWithContext ctx args
