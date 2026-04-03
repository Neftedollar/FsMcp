namespace FsMcp.Sampling

open FsMcp.Core

/// A request to sample (generate) text from the client's LLM.
type SamplingRequest = {
    /// Messages to send to the LLM for completion.
    Messages: McpMessage list
    /// Optional system prompt.
    SystemPrompt: string option
    /// Maximum tokens to generate.
    MaxTokens: int
    /// Temperature (0.0 = deterministic, 1.0 = creative).
    Temperature: float option
    /// Optional stop sequences.
    StopSequences: string list
    /// Optional model preference hint.
    ModelHint: string option
}

/// The result of a sampling request.
type SamplingResult = {
    /// The generated message.
    Message: McpMessage
    /// The model that was used.
    Model: string
    /// Stop reason (e.g., "endTurn", "maxTokens", "stopSequence").
    StopReason: string option
}

/// Errors specific to sampling operations.
type SamplingError =
    | SamplingNotSupported
    | SamplingRejected of reason: string
    | SamplingFailed of message: string
    | SamplingTimeout

module SamplingRequest =
    /// Create a simple sampling request with one user message.
    let simple (prompt: string) (maxTokens: int) : SamplingRequest =
        { Messages = [ { Role = User; Content = Content.text prompt } ]
          SystemPrompt = None
          MaxTokens = maxTokens
          Temperature = None
          StopSequences = []
          ModelHint = None }

    /// Create a sampling request with a system prompt.
    let withSystem (systemPrompt: string) (prompt: string) (maxTokens: int) : SamplingRequest =
        { Messages = [ { Role = User; Content = Content.text prompt } ]
          SystemPrompt = Some systemPrompt
          MaxTokens = maxTokens
          Temperature = None
          StopSequences = []
          ModelHint = None }

    /// Set temperature on a request.
    let withTemperature (temp: float) (req: SamplingRequest) : SamplingRequest =
        { req with Temperature = Some temp }

    /// Set model hint on a request.
    let withModel (model: string) (req: SamplingRequest) : SamplingRequest =
        { req with ModelHint = Some model }

    /// Add stop sequences to a request.
    let withStopSequences (seqs: string list) (req: SamplingRequest) : SamplingRequest =
        { req with StopSequences = seqs }
