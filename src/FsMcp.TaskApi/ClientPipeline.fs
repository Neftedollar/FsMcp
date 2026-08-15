namespace FsMcp.TaskApi

open System
open System.Runtime.CompilerServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Client

[<assembly: InternalsVisibleTo("FsMcp.TaskApi.Tests")>]
do ()

/// Pipe-friendly MCP client operations for use with taskResult { } CE.
///
/// Example:
///   open FsMcp.TaskApi
///   open FsToolkit.ErrorHandling
///
///   taskResult {
///       let! client = ClientPipeline.connect config
///       let! text = client |> ClientPipeline.callToolText "greet" (Map.ofList ["name", jsonEl])
///       printfn "%s" text
///       do! client |> ClientPipeline.disconnect
///   }
module ClientPipeline =

    let internal captureResult
        (cancellationToken: CancellationToken)
        (operation: unit -> Task<Result<'value, McpError>>)
        : Task<Result<'value, McpError>> =
        task {
            try
                return! operation ()
            with
            | :? OperationCanceledException as canceled when cancellationToken.IsCancellationRequested ->
                return raise canceled
            | error -> return Error(HandlerException error)
        }

    let internal capture
        (cancellationToken: CancellationToken)
        (operation: unit -> Task<'value>)
        : Task<Result<'value, McpError>> =
        captureResult cancellationToken (fun () ->
            task {
                let! value = operation ()
                return Ok value
            })

    /// Connect to an MCP server and propagate caller cancellation.
    let connectWithCancellation config cancellationToken =
        capture cancellationToken (fun () -> McpClient.connectWithCancellation config cancellationToken)

    /// Connect to an MCP server. Wraps result in Ok for taskResult chains.
    let connect config = connectWithCancellation config CancellationToken.None

    /// Connect with Enterprise-Managed Authorization and propagate caller cancellation.
    let connectEnterpriseManagedWithCancellation config authorization cancellationToken =
        capture cancellationToken (fun () ->
            McpClient.connectEnterpriseManagedWithCancellation config authorization cancellationToken)

    /// Connect with Enterprise-Managed Authorization.
    let connectEnterpriseManaged config authorization =
        connectEnterpriseManagedWithCancellation config authorization CancellationToken.None

    /// List tools from a connected client and propagate caller cancellation.
    let listToolsWithCancellation cancellationToken client =
        capture cancellationToken (fun () -> McpClient.listToolsWithCancellation client cancellationToken)

    /// List tools from a connected client.
    let listTools client = listToolsWithCancellation CancellationToken.None client

    /// Call a tool by string name and propagate caller cancellation.
    let callToolWithCancellation name args cancellationToken client =
        taskResult {
            let! toolName =
                ToolName.create name
                |> Result.mapError (fun error -> ValidationFailed [ error ])
            return!
                captureResult cancellationToken (fun () ->
                    McpClient.callToolWithCancellation client toolName args cancellationToken)
        }

    /// Call a tool by string name (validates the name internally).
    let callTool name args client =
        callToolWithCancellation name args CancellationToken.None client

    /// Call a tool, propagate cancellation, and extract the first text content.
    let callToolTextWithCancellation name args cancellationToken client =
        taskResult {
            let! contents = callToolWithCancellation name args cancellationToken client
            return
                contents
                |> List.tryPick (function Text text -> Some text | _ -> None)
                |> Option.defaultValue ""
        }

    /// Call a tool and extract the first text content as a string.
    let callToolText name args client =
        callToolTextWithCancellation name args CancellationToken.None client

    /// Read every content item returned for a resource and propagate cancellation.
    let readResourceWithCancellation uri cancellationToken client =
        taskResult {
            let! resourceUri =
                ResourceUri.create uri
                |> Result.mapError (fun error -> ValidationFailed [ error ])
            return!
                captureResult cancellationToken (fun () ->
                    McpClient.readResourceWithCancellation client resourceUri cancellationToken)
        }

    /// Read a resource by string URI (validates internally).
    let readResource uri client =
        readResourceWithCancellation uri CancellationToken.None client

    /// Get a prompt by string name and propagate caller cancellation.
    let getPromptWithCancellation name args cancellationToken client =
        taskResult {
            let! promptName =
                PromptName.create name
                |> Result.mapError (fun error -> ValidationFailed [ error ])
            return!
                captureResult cancellationToken (fun () ->
                    McpClient.getPromptWithCancellation client promptName args cancellationToken)
        }

    /// Get a prompt by string name (validates internally).
    let getPrompt name args client =
        getPromptWithCancellation name args CancellationToken.None client

    /// List resources and propagate caller cancellation.
    let listResourcesWithCancellation cancellationToken client =
        capture cancellationToken (fun () -> McpClient.listResourcesWithCancellation client cancellationToken)

    /// List resources from a connected client.
    let listResources client = listResourcesWithCancellation CancellationToken.None client

    /// List compact prompt metadata and propagate caller cancellation.
    let listPromptsWithCancellation cancellationToken client =
        capture cancellationToken (fun () -> McpClient.listPromptsWithCancellation client cancellationToken)

    /// List prompts from a connected client.
    let listPrompts client = listPromptsWithCancellation CancellationToken.None client

    /// List prompt metadata, including declared arguments, and propagate cancellation.
    let listPromptDetailsWithCancellation cancellationToken client =
        capture cancellationToken (fun () ->
            McpClient.listPromptDetailsWithCancellation client cancellationToken)

    /// List prompt metadata, including declared arguments.
    let listPromptDetails client =
        listPromptDetailsWithCancellation CancellationToken.None client

    /// Disconnect and dispose the client.
    let disconnect client =
        capture CancellationToken.None (fun () -> McpClient.disconnect client)
