namespace FsMcp.Sampling

open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Server

/// Middleware that provides sampling capabilities to downstream handlers.
module SamplingMiddleware =

    /// Create a middleware that injects a SampleFunc into the context.
    /// In production, the SampleFunc would be wired to the SDK's
    /// CreateMessageAsync on the McpServer instance.
    /// For testing, use SamplingTool.mockSample or noOpSample.
    let create (sampleFunc: SampleFunc) : McpMiddleware =
        fun ctx next -> next ctx
        // Note: actual sampling injection requires runtime wiring
        // to the SDK's McpServer.CreateMessageAsync. This middleware
        // is a placeholder for the composition pattern.
        // In practice, sampling is invoked directly within handlers
        // via the SamplingContext, not via middleware.
