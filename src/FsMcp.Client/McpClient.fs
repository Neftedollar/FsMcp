namespace FsMcp.Client

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Reflection
open System.Runtime.ExceptionServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open ModelContextProtocol.Client

/// Simplified tool information returned from listing tools.
type ToolInfo = { Name: string; Description: string }

/// Simplified resource information returned from listing resources.
type ResourceInfo = { Uri: string; Name: string; MimeType: string option }

/// Simplified prompt information returned from listing prompts.
type PromptInfo = { Name: string; Description: string option }

/// Prompt metadata including the server-advertised argument contract.
type PromptDetails = {
    Name: string
    Description: string option
    Arguments: FsMcp.Core.PromptArgument list
}

/// Client configuration.
[<NoComparison>]
type ClientConfig = {
    Transport: ClientTransport
    Name: string
    /// Optional shutdown bound. For stdio this configures the SDK-owned child
    /// process cutoff; for HTTP it bounds client disposal in FsMcp.
    ShutdownTimeout: TimeSpan option
}

/// Wrapper around the C# SDK's McpClient.
[<NoComparison>]
type McpClient = internal {
    Client: ModelContextProtocol.Client.McpClient
    Lifetime: McpClientLifetime
    RedactTransportFailures: bool
}

and internal McpClientLifetime
    (client: ModelContextProtocol.Client.McpClient,
     transportLifetime: IAsyncDisposable option,
     clientDisposalTimeout: TimeSpan option,
     redactFailures: bool) =
    let observe (operation: Task) =
        let observation = task { try do! operation with _ -> () }
        ignore observation

    let disconnect =
        lazy (
            task {
                try
                    do! Task.Yield()
                    let! clientError =
                        task {
                            try
                                let clientDisposal = client.DisposeAsync().AsTask()
                                match clientDisposalTimeout with
                                | None ->
                                    do! clientDisposal
                                    return None
                                | Some timeout ->
                                    let! completed = Task.WhenAny(clientDisposal, Task.Delay timeout)
                                    if obj.ReferenceEquals(completed, clientDisposal) then
                                        do! clientDisposal
                                        return None
                                    else
                                        observe clientDisposal
                                        return Some(TimeoutException("MCP client shutdown timed out.") :> exn)
                            with caught -> return Some caught
                        }

                    let! transportError =
                        task {
                            try
                                match transportLifetime with
                                | Some lifetime -> do! lifetime.DisposeAsync().AsTask()
                                | None -> ()
                                return None
                            with caught -> return Some caught
                        }

                    match clientError, transportError with
                    | None, None -> ()
                    | Some caught, None
                    | None, Some caught -> ExceptionDispatchInfo.Capture(caught).Throw()
                    | Some clientException, Some transportException ->
                        raise (AggregateException(clientException, transportException))
                with
                | :? EnterpriseManagedAuthorizationException as caught ->
                    return ExceptionDispatchInfo.Capture(caught).Throw()
                | _ when redactFailures ->
                    return
                        raise (
                            EnterpriseManagedAuthorizationException
                                EnterpriseManagedAuthorizationFailure.ResourceConnectionFailed)
                | caught -> return ExceptionDispatchInfo.Capture(caught).Throw()
            })

    member _.DisconnectAsync() = disconnect.Value

/// Functions for creating and interacting with MCP clients.
module McpClient =

    [<NoEquality; NoComparison>]
    type private BuiltTransport = {
        Transport: IClientTransport
        Lifetime: IAsyncDisposable option
        ClientDisposalTimeout: TimeSpan option
    }

    let private maximumShutdownTimeout = TimeSpan.FromHours 24.0

    let private validateShutdownTimeout = function
        | Some value when value <= TimeSpan.Zero || value > maximumShutdownTimeout ->
            raise (ArgumentOutOfRangeException(
                "ShutdownTimeout",
                "ShutdownTimeout must be greater than zero and no greater than 24 hours."))
        | value -> value

    let private clientVersion =
        let assembly = typeof<ClientConfig>.Assembly
        match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null ->
            match assembly.GetName().Version with
            | null -> "0.0.0"
            | version -> version.ToString()
        | attribute -> attribute.InformationalVersion.Split('+', 2).[0]

    let private createClientOptions name =
        let options = McpClientOptions()
        options.ClientInfo <- ModelContextProtocol.Protocol.Implementation(Name = name, Version = clientVersion)
        options

    let private copyHeaders headers (options: HttpClientTransportOptions) =
        if not (Map.isEmpty headers) then
            let dictionary = Dictionary<string, string>()
            headers |> Map.iter (fun key value -> dictionary.[key] <- value)
            options.AdditionalHeaders <- dictionary

    /// Build an SDK transport from our ClientTransport DU.
    let private buildTransport (shutdownTimeout: TimeSpan option) transport =
        match transport with
        | StdioProcess (command, args) ->
            let options = StdioClientTransportOptions(Command = command)
            options.Arguments <- List.toArray args
            shutdownTimeout |> Option.iter (fun timeout -> options.ShutdownTimeout <- timeout)
            { Transport = StdioClientTransport(options, null) :> IClientTransport
              Lifetime = None
              ClientDisposalTimeout = None }
        | HttpEndpoint (endpoint, headers) ->
            let options = HttpClientTransportOptions(Endpoint = endpoint)
            copyHeaders headers options
            let transport = HttpClientTransport(options, null)
            { Transport = transport :> IClientTransport
              Lifetime = Some(transport :> IAsyncDisposable)
              ClientDisposalTimeout = shutdownTimeout }

    let private hasHeader name headers =
        headers |> Map.exists (fun key _ -> String.Equals(key, name, StringComparison.OrdinalIgnoreCase))

    let private buildEnterpriseTransport
        (shutdownTimeout: TimeSpan option)
        transport
        (authorization: EnterpriseManagedAuthorization) =
        if obj.ReferenceEquals(authorization, null) then nullArg (nameof authorization)
        match transport with
        | StdioProcess _ ->
            raise (EnterpriseManagedAuthorizationException EnterpriseManagedAuthorizationFailure.UnsupportedTransport)
        | HttpEndpoint (uri, headers) ->
            if hasHeader "Authorization" headers then
                raise (EnterpriseManagedAuthorizationException EnterpriseManagedAuthorizationFailure.StaticAuthorizationHeader)
            if hasHeader "Host" headers then
                raise (EnterpriseManagedAuthorizationException EnterpriseManagedAuthorizationFailure.HostHeaderOverride)
            if not (EnterpriseAuthorizationEndpoint.isSafeAtRuntime authorization.ResourcePlaintextAllowance uri) then
                raise (EnterpriseManagedAuthorizationException EnterpriseManagedAuthorizationFailure.UnsafeRequestTarget)
            if not (EnterpriseAuthorizationEndpoint.sameOrigin authorization.Settings.Resource uri) then
                raise (EnterpriseManagedAuthorizationException EnterpriseManagedAuthorizationFailure.ResourceOriginMismatch)

            let sockets = new SocketsHttpHandler()
            sockets.AllowAutoRedirect <- false
            sockets.UseCookies <- false
            sockets.AutomaticDecompression <- DecompressionMethods.None
            let handler = new EnterpriseManagedAuthorizationHandler(authorization, sockets)
            let httpClient = new HttpClient(handler, true)
            httpClient.Timeout <- Timeout.InfiniteTimeSpan
            try
                let options = HttpClientTransportOptions(Endpoint = uri)
                copyHeaders headers options
                let transport = HttpClientTransport(options, httpClient, null, true)
                { Transport = transport :> IClientTransport
                  Lifetime = Some(transport :> IAsyncDisposable)
                  ClientDisposalTimeout = shutdownTimeout }
            with _ ->
                httpClient.Dispose()
                reraise ()

    let private preserveException<'value> (caught: exn) : 'value =
        ExceptionDispatchInfo.Capture(caught).Throw()
        Unchecked.defaultof<'value>

    let private independentCancellation (caught: exn) =
        InvalidOperationException(
            "The MCP operation was canceled independently of the caller.",
            caught)

    let internal projectConnectionFailure
        redactFailure
        (cancellationToken: CancellationToken)
        (caught: exn)
        (disposalError: exn option)
        =
        let expose failure =
            match disposalError with
            | None -> preserveException failure
            | Some disposal -> raise (AggregateException(failure, disposal))

        match caught with
        | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
            preserveException caught
        | :? EnterpriseManagedAuthorizationException -> preserveException caught
        | _ when redactFailure ->
            raise (
                EnterpriseManagedAuthorizationException
                    EnterpriseManagedAuthorizationFailure.ResourceConnectionFailed)
        | :? OperationCanceledException -> expose (independentCancellation caught)
        | _ -> expose caught

    let private connectCore config builtTransport redactFailure cancellationToken =
        task {
            try
                let! client = ModelContextProtocol.Client.McpClient.CreateAsync(
                    builtTransport.Transport,
                    createClientOptions config.Name,
                    null,
                    cancellationToken)
                return {
                    Client = client
                    Lifetime = McpClientLifetime(
                        client,
                        builtTransport.Lifetime,
                        builtTransport.ClientDisposalTimeout,
                        redactFailure)
                    RedactTransportFailures = redactFailure
                }
            with caught ->
                let! disposalError =
                    task {
                        try
                            match builtTransport.Lifetime with
                            | Some lifetime -> do! lifetime.DisposeAsync().AsTask()
                            | None -> ()
                            return None
                        with disposal -> return Some disposal
                    }
                return
                    projectConnectionFailure
                        redactFailure
                        cancellationToken
                        caught
                        disposalError
        }

    let private unsupportedContent blockType =
        Error(TransportError $"The server returned unsupported MCP content type '{blockType}'.")

    /// Convert an SDK ContentBlock without silently replacing unsupported wire
    /// values with text that changes their meaning.
    let private convertContentBlock (block: ModelContextProtocol.Protocol.ContentBlock) =
        match block with
        | :? ModelContextProtocol.Protocol.TextContentBlock as text ->
            Ok(Content.Text(if isNull text.Text then "" else text.Text))
        | :? ModelContextProtocol.Protocol.ImageContentBlock as image ->
            let mimeType = if isNull image.MimeType then "image/png" else image.MimeType
            match MimeType.create mimeType with
            | Ok mime -> Ok(Content.Image(image.DecodedData.ToArray(), mime))
            | Error _ -> Error(TransportError "The server returned an image with an invalid MIME type.")
        | :? ModelContextProtocol.Protocol.EmbeddedResourceBlock as embedded ->
            match embedded.Resource with
            | :? ModelContextProtocol.Protocol.TextResourceContents as text ->
                match ResourceUri.create (if isNull text.Uri then "" else text.Uri),
                      MimeType.create (if isNull text.MimeType then "" else text.MimeType) with
                | Ok uri, Ok mime -> Ok(Content.EmbeddedResource(TextResource(uri, mime, text.Text)))
                | _ -> Error(TransportError "The server returned invalid embedded text-resource metadata.")
            | :? ModelContextProtocol.Protocol.BlobResourceContents as blob ->
                match ResourceUri.create (if isNull blob.Uri then "" else blob.Uri),
                      MimeType.create (if isNull blob.MimeType then "" else blob.MimeType) with
                | Ok uri, Ok mime ->
                    Ok(Content.EmbeddedResource(BlobResource(uri, mime, blob.DecodedData.ToArray())))
                | _ -> Error(TransportError "The server returned invalid embedded binary-resource metadata.")
            | _ -> unsupportedContent "embedded_resource"
        | _ -> unsupportedContent (if isNull block.Type then "unknown" else block.Type)

    let private convertContentBlocks blocks =
        ((Ok []), blocks)
        ||> Seq.fold (fun state block ->
            match state with
            | Error error -> Error error
            | Ok converted -> convertContentBlock block |> Result.map (fun content -> content :: converted))
        |> Result.map List.rev

    let private protocolError (caught: ModelContextProtocol.McpException) =
        match caught with
        | :? ModelContextProtocol.McpProtocolException as protocol ->
            ProtocolError(int protocol.ErrorCode, protocol.Message)
        | _ -> ProtocolError(int ModelContextProtocol.McpErrorCode.InternalError, caught.Message)

    let private raiseOperationFailure
        (client: McpClient)
        (cancellationToken: CancellationToken)
        (caught: exn)
        =
        match caught with
        | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> preserveException caught
        | :? OperationCanceledException when not client.RedactTransportFailures ->
            raise (InvalidOperationException("The MCP operation was canceled independently of the caller.", caught))
        | :? EnterpriseManagedAuthorizationException
        | :? ModelContextProtocol.McpProtocolException -> preserveException caught
        | _ when not client.RedactTransportFailures -> preserveException caught
        | _ -> raise (EnterpriseManagedAuthorizationException EnterpriseManagedAuthorizationFailure.ResourceConnectionFailed)

    let private resultOperationFailure
        (client: McpClient)
        (cancellationToken: CancellationToken)
        (caught: exn)
        =
        match caught with
        | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> preserveException caught
        | :? OperationCanceledException when not client.RedactTransportFailures ->
            Error(TransportError "The MCP operation was canceled independently of the caller.")
        | :? EnterpriseManagedAuthorizationException -> preserveException caught
        | :? ModelContextProtocol.McpProtocolException as protocol -> Error(protocolError protocol)
        | :? ModelContextProtocol.McpException as mcp when not client.RedactTransportFailures -> Error(protocolError mcp)
        | _ when not client.RedactTransportFailures -> Error(HandlerException caught)
        | _ -> Error(TransportError "The enterprise-authorized MCP operation failed.")

    let private convertRole role =
        match role with
        | ModelContextProtocol.Protocol.Role.User -> Ok McpRole.User
        | ModelContextProtocol.Protocol.Role.Assistant -> Ok McpRole.Assistant
        | _ -> Error(TransportError "The server returned an unsupported MCP prompt role.")

    /// Connect to an MCP server with cancellation propagated into transport and initialization.
    let connectWithCancellation config cancellationToken =
        let shutdownTimeout = validateShutdownTimeout config.ShutdownTimeout
        connectCore config (buildTransport shutdownTimeout config.Transport) false cancellationToken

    /// Connect to an MCP server.
    let connect config = connectWithCancellation config CancellationToken.None

    /// Connect an HTTP MCP client with enterprise-managed authorization.
    let connectEnterpriseManagedWithCancellation config authorization cancellationToken =
        let shutdownTimeout = validateShutdownTimeout config.ShutdownTimeout
        connectCore config (buildEnterpriseTransport shutdownTimeout config.Transport authorization) true cancellationToken

    /// Connect an HTTP MCP client with enterprise-managed authorization.
    let connectEnterpriseManaged config authorization =
        connectEnterpriseManagedWithCancellation config authorization CancellationToken.None

    /// List available tools with cancellation.
    let listToolsWithCancellation client cancellationToken =
        task {
            try
                let! tools = client.Client.ListToolsAsync(cancellationToken = cancellationToken)
                return tools |> Seq.map (fun tool -> {
                    ToolInfo.Name = tool.Name
                    Description = if isNull tool.Description then "" else tool.Description }) |> Seq.toList
            with caught -> return raiseOperationFailure client cancellationToken caught
        }

    /// List available tools.
    let listTools client = listToolsWithCancellation client CancellationToken.None

    /// Call a tool by name with arguments.
    let callToolWithCancellation
        client
        toolName
        (args: Map<string, JsonElement>)
        cancellationToken
        =
        task {
            try
                let dictionary = Dictionary<string, obj>()
                args |> Map.iter (fun key value -> dictionary.[key] <- value :> obj)
                let! result = client.Client.CallToolAsync(
                    ToolName.value toolName,
                    dictionary,
                    cancellationToken = cancellationToken)
                if result.IsError.GetValueOrDefault false then
                    let errorText =
                        result.Content
                        |> Seq.tryPick (function
                            | :? ModelContextProtocol.Protocol.TextContentBlock as text -> Some text.Text
                            | _ -> None)
                        |> Option.defaultValue "Tool call failed"
                    return Error(TransportError errorText)
                else return convertContentBlocks result.Content
            with caught -> return resultOperationFailure client cancellationToken caught
        }

    /// Call a tool by name with arguments.
    let callTool client toolName args = callToolWithCancellation client toolName args CancellationToken.None

    /// List available resources.
    let listResourcesWithCancellation client cancellationToken =
        task {
            try
                let! resources = client.Client.ListResourcesAsync(cancellationToken = cancellationToken)
                return resources |> Seq.map (fun resource -> {
                    ResourceInfo.Uri = if isNull resource.Uri then "" else resource.Uri
                    Name = if isNull resource.Name then "" else resource.Name
                    MimeType = if isNull resource.MimeType then None else Some resource.MimeType }) |> Seq.toList
            with caught -> return raiseOperationFailure client cancellationToken caught
        }

    /// List available resources.
    let listResources client = listResourcesWithCancellation client CancellationToken.None

    let private convertResourceContent (content: ModelContextProtocol.Protocol.ResourceContents) =
        match content with
        | :? ModelContextProtocol.Protocol.TextResourceContents as text ->
            match ResourceUri.create (if isNull text.Uri then "" else text.Uri),
                  MimeType.create (if isNull text.MimeType then "" else text.MimeType) with
            | Ok uri, Ok mime -> Ok(TextResource(uri, mime, text.Text))
            | _ -> Error(TransportError "Invalid text-resource URI or MIME type in response")
        | :? ModelContextProtocol.Protocol.BlobResourceContents as blob ->
            match ResourceUri.create (if isNull blob.Uri then "" else blob.Uri),
                  MimeType.create (if isNull blob.MimeType then "" else blob.MimeType) with
            | Ok uri, Ok mime -> Ok(BlobResource(uri, mime, blob.DecodedData.ToArray()))
            | _ -> Error(TransportError "Invalid binary-resource URI or MIME type in response")
        | _ -> Error(TransportError "Unsupported resource content type")

    /// Read a resource by URI.
    let readResourceWithCancellation client uri cancellationToken =
        task {
            try
                let! result = client.Client.ReadResourceAsync(ResourceUri.value uri, cancellationToken = cancellationToken)
                return ((Ok []), result.Contents)
                       ||> Seq.fold (fun state content ->
                           match state with
                           | Error error -> Error error
                           | Ok converted ->
                               convertResourceContent content |> Result.map (fun item -> item :: converted))
                       |> Result.map List.rev
            with caught -> return resultOperationFailure client cancellationToken caught
        }

    /// Read a resource by URI.
    let readResource client uri = readResourceWithCancellation client uri CancellationToken.None

    /// List available prompts including their declared argument contract.
    let listPromptDetailsWithCancellation client cancellationToken =
        task {
            try
                let! prompts = client.Client.ListPromptsAsync(cancellationToken = cancellationToken)
                return prompts |> Seq.map (fun prompt -> {
                    PromptDetails.Name = if isNull prompt.Name then "" else prompt.Name
                    Description = if isNull prompt.Description then None else Some prompt.Description
                    Arguments =
                        match prompt.ProtocolPrompt.Arguments with
                        | null -> []
                        | arguments ->
                            arguments |> Seq.map (fun argument -> {
                                FsMcp.Core.PromptArgument.Name = if isNull argument.Name then "" else argument.Name
                                Description = if isNull argument.Description then None else Some argument.Description
                                Required = argument.Required.GetValueOrDefault false }) |> Seq.toList }) |> Seq.toList
            with caught -> return raiseOperationFailure client cancellationToken caught
        }

    /// List available prompts including their declared argument contract.
    let listPromptDetails client = listPromptDetailsWithCancellation client CancellationToken.None

    /// List available prompts using the compact compatibility projection.
    let listPromptsWithCancellation client cancellationToken =
        task {
            let! prompts = listPromptDetailsWithCancellation client cancellationToken
            return prompts |> List.map (fun prompt -> {
                PromptInfo.Name = prompt.Name
                Description = prompt.Description })
        }

    /// List available prompts using the compact compatibility projection.
    let listPrompts client = listPromptsWithCancellation client CancellationToken.None

    /// Get a prompt with arguments.
    let getPromptWithCancellation
        client
        promptName
        (args: Map<string, string>)
        cancellationToken
        =
        task {
            try
                let dictionary = Dictionary<string, obj>()
                args |> Map.iter (fun key value -> dictionary.[key] <- value :> obj)
                let! result = client.Client.GetPromptAsync(
                    PromptName.value promptName,
                    dictionary,
                    cancellationToken = cancellationToken)
                return ((Ok []), result.Messages)
                       ||> Seq.fold (fun state message ->
                           match state with
                           | Error error -> Error error
                           | Ok converted ->
                               match convertRole message.Role, convertContentBlock message.Content with
                               | Ok role, Ok content -> Ok({ Role = role; Content = content } :: converted)
                               | Error error, _
                               | _, Error error -> Error error)
                       |> Result.map List.rev
            with caught -> return resultOperationFailure client cancellationToken caught
        }

    /// Get a prompt with arguments.
    let getPrompt client promptName args = getPromptWithCancellation client promptName args CancellationToken.None

    /// Disconnect and dispose.
    let disconnect (client: McpClient) = client.Lifetime.DisconnectAsync()

/// Async wrappers for all McpClient functions.
module McpClientAsync =
    let private withCancellation operation =
        async {
            let! cancellationToken = Async.CancellationToken
            return! operation cancellationToken |> Async.AwaitTask
        }

    /// Connect to an MCP server.
    let connect config = withCancellation (McpClient.connectWithCancellation config)

    /// Connect with enterprise-managed authorization and ambient F# async cancellation.
    let connectEnterpriseManaged config authorization =
        withCancellation (McpClient.connectEnterpriseManagedWithCancellation config authorization)

    /// List available tools.
    let listTools client = withCancellation (McpClient.listToolsWithCancellation client)

    /// Call a tool by name with arguments.
    let callTool client toolName args = withCancellation (McpClient.callToolWithCancellation client toolName args)

    /// List available resources.
    let listResources client = withCancellation (McpClient.listResourcesWithCancellation client)

    /// Read a resource by URI.
    let readResource client uri = withCancellation (McpClient.readResourceWithCancellation client uri)

    /// List available prompts.
    let listPrompts client = withCancellation (McpClient.listPromptsWithCancellation client)

    /// List prompt metadata including declared arguments.
    let listPromptDetails client = withCancellation (McpClient.listPromptDetailsWithCancellation client)

    /// Get a prompt with arguments.
    let getPrompt client promptName args = withCancellation (McpClient.getPromptWithCancellation client promptName args)

    /// Disconnect and dispose.
    let disconnect client = McpClient.disconnect client |> Async.AwaitTask
