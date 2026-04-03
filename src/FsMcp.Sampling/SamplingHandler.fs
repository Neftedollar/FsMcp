namespace FsMcp.Sampling

open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation

/// A function that sends a sampling request to the client's LLM.
type SampleFunc = SamplingRequest -> Task<Result<SamplingResult, SamplingError>>

/// Context for tool handlers that can invoke sampling.
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
    let define<'TArgs>
        (name: string)
        (description: string)
        (handler: SamplingContext -> 'TArgs -> Task<Result<Content list, McpError>>)
        : Result<ToolDefinition, ValidationError> =

        let schema = FsMcp.Server.SchemaGen.generateSchema<'TArgs> ()
        let deserializerOptions =
            System.Text.Json.JsonSerializerOptions(PropertyNameCaseInsensitive = true)

        // Default handler uses no-op sampling (wired to real client at runtime)
        let rawHandler (args: Map<string, System.Text.Json.JsonElement>) =
            task {
                try
                    let jsonObj = System.Text.Json.Nodes.JsonObject()
                    for kv in args do
                        jsonObj.[kv.Key] <- System.Text.Json.Nodes.JsonNode.Parse(kv.Value.GetRawText())
                    let json = jsonObj.ToJsonString()
                    let typedArgs =
                        System.Text.Json.JsonSerializer.Deserialize<'TArgs>(json, deserializerOptions)
                    let ctx = noOpContext ()
                    return! handler ctx typedArgs
                with ex ->
                    return Error (HandlerException ex)
            }

        match ToolName.create name with
        | Ok tn ->
            Ok {
                Name = tn
                Description = description
                InputSchema = Some schema
                Handler = rawHandler
            }
        | Error e -> Error e
