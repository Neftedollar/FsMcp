// RunSessionHandler and ConfigureSessionOptions are experimental SDK lifecycle
// seams. They are centralized here so cleanup and stateless honesty are enforced.
#nowarn "57"

namespace FsMcp.Server.Http

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open FsMcp.Server
open ModelContextProtocol.AspNetCore
open ModelContextProtocol.Server

/// Streamable HTTP composition and convenience runners for FsMcp servers.
module HttpServer =

    let private cleanupSession
        (registry: ResourceSubscriptionRegistry option)
        (server: McpServer) =

        match registry with
        | Some registry when not (isNull server) && not (String.IsNullOrWhiteSpace server.SessionId) ->
            ResourceSubscriptions.unsubscribeAllForSession server.SessionId registry
        | _ -> ()

    let private configureTransport
        (configure: HttpServerTransportOptions -> unit)
        (registration: ServerRegistration) =

        registration.Builder.WithHttpTransport(fun options ->
            configure options

            match registration.Subscriptions with
            | Some _ ->
                let configuredSessionOptions = options.ConfigureSessionOptions
                options.ConfigureSessionOptions <-
                    Func<HttpContext, McpServerOptions, CancellationToken, Task>(fun context serverOptions cancellationToken ->
                        task {
                            if not (isNull configuredSessionOptions) then
                                do! configuredSessionOptions.Invoke(context, serverOptions, cancellationToken)

                            if options.Stateless then
                                // SDK 1.4.1 treats a missing handler for this standard
                                // method as an empty success. Keep the fail-closed
                                // handlers, but omit the advertised stateful capability.
                                if not (isNull serverOptions.Capabilities)
                                   && not (isNull serverOptions.Capabilities.Resources) then
                                    serverOptions.Capabilities.Resources.Subscribe <- Nullable()
                        }
                        :> Task)
            | None -> ()

            let configuredRunner = options.RunSessionHandler
            options.RunSessionHandler <-
                Func<HttpContext, McpServer, CancellationToken, Task>(fun context server cancellationToken ->
                    task {
                        try
                            if isNull configuredRunner then
                                do! server.RunAsync(cancellationToken)
                            else
                                do! configuredRunner.Invoke(context, server, cancellationToken)
                        finally
                            cleanupSession registration.Subscriptions server
                    }
                    :> Task))
        |> ignore

        registration

    /// Compose FsMcp into an existing official SDK builder and add Streamable HTTP.
    let addToBuilder (config: ServerConfig) (builder: IMcpServerBuilder) =
        Server.addToBuilderWithSubscriptionsInternal config builder
        |> configureTransport ignore

    /// Compose FsMcp into an existing SDK builder with explicit transport options.
    /// Any custom RunSessionHandler is preserved and wrapped in guaranteed cleanup.
    let addToBuilderWithOptions
        (config: ServerConfig)
        (configure: HttpServerTransportOptions -> unit)
        (builder: IMcpServerBuilder) =

        ArgumentNullException.ThrowIfNull configure
        Server.addToBuilderWithSubscriptionsInternal config builder
        |> configureTransport configure

    let addToServices (config: ServerConfig) (services: IServiceCollection) =
        ArgumentNullException.ThrowIfNull services
        addToBuilder config (services.AddMcpServer())

    let addToServicesWithOptions
        (config: ServerConfig)
        (configure: HttpServerTransportOptions -> unit)
        (services: IServiceCollection) =

        ArgumentNullException.ThrowIfNull services
        addToBuilderWithOptions config configure (services.AddMcpServer())

    /// Internal wire-test seam for exercising bounded subscription handlers.
    let internal addToServicesWithSubscriptionLimitInternal
        (maxSubscriptionsPerSession: int)
        (config: ServerConfig)
        (services: IServiceCollection) =

        ArgumentNullException.ThrowIfNull services
        Server.addToBuilderWithSubscriptionLimitInternal
            maxSubscriptionsPerSession
            config
            (services.AddMcpServer())
        |> configureTransport ignore

    let private runCore
        (config: ServerConfig)
        (endpoint: string option)
        (url: string)
        (onRegistry: ResourceSubscriptionRegistry option -> Task<unit>)
        (cancellationToken: CancellationToken) =

        task {
            let builder = WebApplication.CreateBuilder()
            builder.Logging.SetMinimumLevel(LogLevel.Information) |> ignore
            let registration = addToServices config builder.Services
            do! onRegistry registration.Subscriptions

            use application = builder.Build()
            application.MapMcp(endpoint |> Option.defaultValue "/") |> ignore
            application.Urls.Add url
            do! application.StartAsync cancellationToken
            do! application.WaitForShutdownAsync cancellationToken
        }

    /// Run the server over Streamable HTTP until cancellation or shutdown.
    let runWithCancellation config endpoint url cancellationToken =
        runCore config endpoint url (fun _ -> Task.FromResult()) cancellationToken

    let run config endpoint url =
        runWithCancellation config endpoint url CancellationToken.None

    /// Run over Streamable HTTP and expose the opaque subscription handle before
    /// the application starts. Disconnect cleanup is guaranteed for both runners.
    let runWithSubscriptionsAndCancellation
        config endpoint url onRegistry cancellationToken =
        runCore config endpoint url onRegistry cancellationToken

    let runWithSubscriptions config endpoint url onRegistry =
        runWithSubscriptionsAndCancellation
            config endpoint url onRegistry CancellationToken.None

    /// Run over Streamable HTTP as Async with cooperative cancellation.
    let runAsync config endpoint url =
        async {
            let! cancellationToken = Async.CancellationToken
            do! runWithCancellation config endpoint url cancellationToken |> Async.AwaitTask
        }
