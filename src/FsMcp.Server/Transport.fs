namespace FsMcp.Server

open System
open System.Collections.Generic
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open FsMcp.Core
open FsMcp.Core.Validation
open ModelContextProtocol
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server

/// Result of composing an FsMcp configuration into the official SDK builder.
/// The SDK builder remains available for additional filters and hosting-specific
/// configuration; subscription state is exposed only through its opaque handle.
[<Sealed>]
type ServerRegistration internal (
    builder: IMcpServerBuilder,
    subscriptions: ResourceSubscriptionRegistry option) =
    member _.Builder = builder
    member _.Subscriptions = subscriptions

/// Functions for composing and running an MCP server from a ServerConfig.
module Server =

    let private emptyObjectSchema =
        JsonSerializer.SerializeToElement(JsonObject([ KeyValuePair("type", JsonValue.Create("object") :> JsonNode) ]))

    let private protocolError (message: string) (code: McpErrorCode) =
        McpProtocolException(message, code)

    let private formatValidationErrors errors =
        errors |> List.map ValidationError.format |> String.concat "; "

    let private toolErrorText = function
        | ValidationFailed errors -> $"Validation failed: {formatValidationErrors errors}"
        | ToolNotFound name -> $"Tool '{ToolName.value name}' was not found."
        | ResourceNotFound uri -> $"Resource '{ResourceUri.value uri}' was not found."
        | PromptNotFound name -> $"Prompt '{PromptName.value name}' was not found."
        | HandlerException _ -> "The tool handler failed."
        | TransportError message -> message
        | ProtocolError(_, message) -> message

    let private raisePrimitiveError primitive = function
        | ProtocolError(code, message) ->
            raise (McpProtocolException(message, enum<McpErrorCode> code))
        | ValidationFailed errors ->
            raise (protocolError $"Invalid {primitive} arguments: {formatValidationErrors errors}" McpErrorCode.InvalidParams)
        | ToolNotFound name ->
            raise (protocolError $"Tool '{ToolName.value name}' was not found." McpErrorCode.InvalidParams)
        | ResourceNotFound uri ->
            raise (protocolError $"Resource '{ResourceUri.value uri}' was not found." McpErrorCode.InvalidParams)
        | PromptNotFound name ->
            raise (protocolError $"Prompt '{PromptName.value name}' was not found." McpErrorCode.InvalidParams)
        | HandlerException _ ->
            raise (protocolError $"The {primitive} handler failed." McpErrorCode.InternalError)
        | TransportError _ ->
            raise (protocolError $"The {primitive} handler could not complete the request." McpErrorCode.InternalError)

    let private jsonArguments (arguments: AIFunctionArguments) =
        if isNull arguments then Map.empty
        else
            arguments
            |> Seq.choose (fun keyValue ->
                match keyValue.Value with
                | :? JsonElement as value -> Some(keyValue.Key, value.Clone())
                | null -> None
                | value -> Some(keyValue.Key, JsonSerializer.SerializeToElement value))
            |> Map.ofSeq

    let private stringArguments (arguments: AIFunctionArguments) =
        let toStringValue (value: obj) =
            match value with
            | :? JsonElement as element when element.ValueKind = JsonValueKind.String -> element.GetString()
            | :? JsonElement as element -> element.GetRawText()
            | null -> null
            | :? IFormattable as formattable -> formattable.ToString(null, CultureInfo.InvariantCulture)
            | value -> defaultArg (Option.ofObj (value.ToString())) String.Empty

        if isNull arguments then Map.empty
        else
            arguments
            |> Seq.choose (fun keyValue ->
                let value = toStringValue keyValue.Value
                if isNull value then None else Some(keyValue.Key, value))
            |> Map.ofSeq

    type internal ToolAIFunction(definition: ToolDefinition) =
        inherit AIFunction()

        override _.Name = ToolName.value definition.Name
        override _.Description = definition.Description
        override _.JsonSchema = definition.InputSchema |> Option.defaultValue emptyObjectSchema

        override _.InvokeCoreAsync(arguments, cancellationToken) =
            ValueTask<obj>(task {
                try
                    let! result = definition.Handler (jsonArguments arguments) cancellationToken
                    match result with
                    | Ok contents ->
                        return
                            CallToolResult(
                                Content = (contents |> List.map Interop.toSdkContentBlock |> List.toArray))
                            :> obj
                    | Error error ->
                        return
                            CallToolResult(
                                Content = [| TextContentBlock(Text = toolErrorText error) |],
                                IsError = true)
                            :> obj
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return raise (OperationCanceledException(cancellationToken))
                | :? McpProtocolException as error -> return raise error
                | _ ->
                    return
                        CallToolResult(
                            Content = [| TextContentBlock(Text = "The tool handler failed.") |],
                            IsError = true)
                        :> obj
            })

    let internal createSdkTool (definition: ToolDefinition) =
        McpServerTool.Create(
            ToolAIFunction(definition),
            McpServerToolCreateOptions(
                Name = ToolName.value definition.Name,
                Description = definition.Description))

    let private templateArgumentNames (template: string) =
        Regex.Matches(template, @"\{(?:[+#./;?&])?([^}]+)\}")
        |> Seq.cast<Match>
        |> Seq.collect (fun matched -> matched.Groups.[1].Value.Split(','))
        |> Seq.map (fun expression -> expression.Trim().TrimEnd('*').Split(':').[0])
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> Seq.distinct
        |> Seq.toList

    let private stringSchema (arguments: (string * string option * bool) list) =
        let properties = JsonObject()
        let required = JsonArray()
        for name, description, isRequired in arguments do
            let property = JsonObject()
            property.["type"] <- JsonValue.Create("string")
            description |> Option.iter (fun value -> property.["description"] <- JsonValue.Create(value))
            properties.[name] <- property
            if isRequired then required.Add(JsonValue.Create(name))
        let schema = JsonObject()
        schema.["type"] <- JsonValue.Create("object")
        schema.["properties"] <- properties
        if required.Count > 0 then schema.["required"] <- required
        JsonSerializer.SerializeToElement schema

    type internal ResourceAIFunction(definition: ResourceDefinition) =
        inherit AIFunction()

        let schema =
            ResourceUri.value definition.Uri
            |> templateArgumentNames
            |> List.map (fun name -> name, None, true)
            |> stringSchema

        override _.Name = definition.Name
        override _.Description = definition.Description |> Option.defaultValue String.Empty
        override _.JsonSchema = schema

        override _.InvokeCoreAsync(arguments, cancellationToken) =
            ValueTask<obj>(task {
                try
                    let! result = definition.Handler (stringArguments arguments) cancellationToken
                    match result with
                    | Ok resource ->
                        let sdkResource: ModelContextProtocol.Protocol.ResourceContents =
                            match resource with
                            | TextResource(uri, mimeType, text) ->
                                TextResourceContents(
                                    Uri = ResourceUri.value uri,
                                    MimeType = MimeType.value mimeType,
                                    Text = text)
                            | BlobResource(uri, mimeType, data) ->
                                BlobResourceContents(
                                    Uri = ResourceUri.value uri,
                                    MimeType = MimeType.value mimeType,
                                    Blob = ReadOnlyMemory data)
                        return ReadResourceResult(Contents = [| sdkResource |]) :> obj
                    | Error error -> return raisePrimitiveError "resource" error
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return raise (OperationCanceledException(cancellationToken))
                | :? McpProtocolException as error -> return raise error
                | error -> return raisePrimitiveError "resource" (HandlerException error)
            })

    let internal createSdkResource (definition: ResourceDefinition) =
        let options =
            McpServerResourceCreateOptions(
                Name = definition.Name,
                Description = (definition.Description |> Option.defaultValue String.Empty),
                UriTemplate = ResourceUri.value definition.Uri)
        definition.MimeType |> Option.iter (MimeType.value >> fun value -> options.MimeType <- value)
        McpServerResource.Create(ResourceAIFunction(definition), options)

    type internal PromptAIFunction(definition: PromptDefinition) =
        inherit AIFunction()

        let schema =
            definition.Arguments
            |> List.map (fun argument -> argument.Name, argument.Description, argument.Required)
            |> stringSchema

        override _.Name = PromptName.value definition.Name
        override _.Description = definition.Description |> Option.defaultValue String.Empty
        override _.JsonSchema = schema

        override _.InvokeCoreAsync(arguments, cancellationToken) =
            ValueTask<obj>(task {
                try
                    let! result = definition.Handler (stringArguments arguments) cancellationToken
                    match result with
                    | Ok messages ->
                        let sdkMessages =
                            messages
                            |> List.map (fun message ->
                                PromptMessage(
                                    Role = Interop.toSdkRole message.Role,
                                    Content = Interop.toSdkContentBlock message.Content))
                            |> List.toArray
                        return
                            GetPromptResult(
                                Messages = sdkMessages,
                                Description = (definition.Description |> Option.defaultValue String.Empty))
                            :> obj
                    | Error error -> return raisePrimitiveError "prompt" error
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return raise (OperationCanceledException(cancellationToken))
                | :? McpProtocolException as error -> return raise error
                | error -> return raisePrimitiveError "prompt" (HandlerException error)
            })

    let internal createSdkPrompt (definition: PromptDefinition) =
        McpServerPrompt.Create(
            PromptAIFunction(definition),
            McpServerPromptCreateOptions(
                Name = PromptName.value definition.Name,
                Description = (definition.Description |> Option.defaultValue String.Empty)))

    let private ensureSupportedConfig (config: ServerConfig) =
#nowarn "44"
        if not (List.isEmpty config.Middleware) then
            raise (
                FsMcpConfigException(
                    "ServerConfig.Middleware was accepted but never executed in FsMcp 1.x. FsMcp 2.0 fails closed instead of silently bypassing it. Migrate to ModelContextProtocol request filters or ASP.NET Core middleware before registering this server."))
#warnon "44"

    let private configureServerInfo (builder: IMcpServerBuilder) (config: ServerConfig) =
        builder.Services.Configure<McpServerOptions>(fun (options: McpServerOptions) ->
            options.ServerInfo <-
                Implementation(
                    Name = ServerName.value config.Name,
                    Version = ServerVersion.value config.Version))
        |> ignore

    let private requireSession (server: McpServer) =
        if isNull server || String.IsNullOrWhiteSpace server.SessionId then
            raise (
                protocolError
                    "Resource subscriptions require a stateful transport session. They are unavailable for stateless or sessionless requests."
                    McpErrorCode.InvalidRequest)
        server.SessionId

    let private requireConfiguredResource (resources: McpServerResource list) (uriText: string) =
        if String.IsNullOrWhiteSpace uriText then
            raise (protocolError "A resource URI is required." McpErrorCode.InvalidParams)
        if not (resources |> List.exists (fun resource -> resource.IsMatch uriText)) then
            raise (protocolError $"Resource '{uriText}' is not configured by this server." McpErrorCode.InvalidParams)
        match ResourceUri.create uriText with
        | Ok uri -> uri
        | Error _ -> raise (protocolError "The resource URI is invalid." McpErrorCode.InvalidParams)

    let private registerSubscriptions
        maxSubscriptionsPerSession
        (builder: IMcpServerBuilder)
        (resources: McpServerResource list) =

        if List.isEmpty resources then None
        else
            let registry = ResourceSubscriptions.createWithLimit maxSubscriptionsPerSession
            let subscribeHandler =
                McpRequestHandler<SubscribeRequestParams, EmptyResult>(fun request _ ->
                    ValueTask<EmptyResult>(task {
                        if isNull request.Params then
                            raise (protocolError "Subscribe parameters are required." McpErrorCode.InvalidParams)
                        let sessionId = requireSession request.Server
                        let uri = requireConfiguredResource resources request.Params.Uri
                        match ResourceSubscriptions.subscribe sessionId request.Server uri registry with
                        | Ok _ -> ()
                        | Error MissingSessionId ->
                            raise (protocolError "Resource subscriptions require a stateful transport session." McpErrorCode.InvalidRequest)
                        | Error(SubscriptionLimitExceeded maximum) ->
                            raise (protocolError $"The session reached the resource subscription limit of {maximum}." McpErrorCode.InvalidParams)
                        return EmptyResult()
                    }))
            let unsubscribeHandler =
                McpRequestHandler<UnsubscribeRequestParams, EmptyResult>(fun request _ ->
                    ValueTask<EmptyResult>(task {
                        if isNull request.Params then
                            raise (protocolError "Unsubscribe parameters are required." McpErrorCode.InvalidParams)
                        let sessionId = requireSession request.Server
                        let uri = requireConfiguredResource resources request.Params.Uri
                        ResourceSubscriptions.unsubscribeResource sessionId uri registry
                        return EmptyResult()
                    }))
            builder.WithSubscribeToResourcesHandler(subscribeHandler).WithUnsubscribeFromResourcesHandler(unsubscribeHandler)
            |> ignore
            Some registry

    let private addToBuilderCore
        (subscriptionLimit: int option)
        (config: ServerConfig)
        (builder: IMcpServerBuilder) =

        ensureSupportedConfig config
        configureServerInfo builder config
        let resources = config.Resources |> List.map createSdkResource
        if not (List.isEmpty config.Tools) then builder.WithTools(config.Tools |> List.map createSdkTool) |> ignore
        if not (List.isEmpty resources) then builder.WithResources(resources) |> ignore
        if not (List.isEmpty config.Prompts) then builder.WithPrompts(config.Prompts |> List.map createSdkPrompt) |> ignore
        let subscriptions = subscriptionLimit |> Option.bind (fun limit -> registerSubscriptions limit builder resources)
        ServerRegistration(builder, subscriptions)

    /// Compose transport-agnostic primitives into an existing official SDK builder.
    /// This entry point deliberately does not advertise resource subscriptions.
    let addToBuilder (config: ServerConfig) (builder: IMcpServerBuilder) =
        ArgumentNullException.ThrowIfNull builder
        addToBuilderCore None config builder

    let addToServices (config: ServerConfig) (services: IServiceCollection) =
        ArgumentNullException.ThrowIfNull services
        addToBuilderCore None config (services.AddMcpServer())

    let internal addToBuilderWithSubscriptionsInternal config (builder: IMcpServerBuilder) =
        ArgumentNullException.ThrowIfNull builder
        addToBuilderCore (Some ResourceSubscriptions.DefaultMaxSubscriptionsPerSession) config builder

    let internal addToBuilderWithSubscriptionLimitInternal limit config (builder: IMcpServerBuilder) =
        ArgumentNullException.ThrowIfNull builder
        addToBuilderCore (Some limit) config builder

    let internal registerAllInternal (builder: IMcpServerBuilder) config =
        (addToBuilderWithSubscriptionsInternal config builder).Subscriptions

    let private configureLogging (hostBuilder: HostApplicationBuilder) (config: ServerConfig) =
        hostBuilder.Logging.ClearProviders() |> ignore
        if config.ConsoleLogging then
            hostBuilder.Logging.AddProvider(new NonBlockingStderrLoggerProvider(LogLevel.Information)) |> ignore
            hostBuilder.Logging.SetMinimumLevel(LogLevel.Information) |> ignore

    let runWithCancellation (config: ServerConfig) (cancellationToken: CancellationToken) =
        task {
            let hostBuilder = Host.CreateApplicationBuilder()
            configureLogging hostBuilder config
            let registration = addToServices config hostBuilder.Services
            registration.Builder.WithStdioServerTransport() |> ignore
            use host = hostBuilder.Build()
            do! host.RunAsync cancellationToken
        }

    let run config = runWithCancellation config CancellationToken.None

    let runAsync config =
        async {
            let! cancellationToken = Async.CancellationToken
            do! runWithCancellation config cancellationToken |> Async.AwaitTask
        }
