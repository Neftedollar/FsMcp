namespace FsMcp.Sampling

open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation

/// A function that sends a sampling request to the client's LLM.
type SampleFunc = SamplingRequest -> Task<Result<SamplingResult, SamplingError>>

/// Context for tool handlers that can invoke sampling.
[<NoComparison; NoEquality>]
type SamplingContext = {
    /// Send a sampling request to the client's LLM.
    Sample: SampleFunc
    /// Cancellation token.
    CancellationToken: System.Threading.CancellationToken
}

/// Tools that can invoke sampling during execution.
module SamplingTool =

    /// A no-op sample function for testing without a real client.
    let noOpSample : SampleFunc =
        fun _ -> Task.FromResult(Error SamplingNotSupported)

    /// A mock sample function that returns a fixed response.
    let mockSample (response: string) : SampleFunc =
        fun _ -> Task.FromResult(Ok {
            Message = { Role = Assistant; Content = Content.text response }
            Model = "mock"
            StopReason = Some "endTurn"
        })

    /// Create a no-op SamplingContext for testing.
    let noOpContext () : SamplingContext =
        { Sample = noOpSample
          CancellationToken = System.Threading.CancellationToken.None }

    /// Create a mock SamplingContext that always returns the given text.
    let mockContext (response: string) : SamplingContext =
        { Sample = mockSample response
          CancellationToken = System.Threading.CancellationToken.None }

    /// Define a typed tool whose handler can invoke sampling.
    [<System.Obsolete("SamplingTool transport wiring was never implemented and now fails closed. Use the SDK request-scoped McpServer sampling API directly.")>]
    let define<'TArgs>
        (name: string)
        (description: string)
        (handler: SamplingContext -> 'TArgs -> Task<Result<Content list, McpError>>)
        : Result<ToolDefinition, ValidationError> =
        ignore name
        ignore description
        ignore handler
        raise (
            FsMcp.Server.FsMcpConfigException(
                "SamplingTool.define used a SamplingNotSupported no-op context in FsMcp 1.x and never reached the connected MCP client. It now fails closed. Production support requires a future SDK RequestContext/McpServer-based handler API."))
