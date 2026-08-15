namespace FsMcp.Server

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Nodes
open FsMcp.Core
open FsMcp.Core.Validation

/// A cancellation-aware streaming tool handler that yields content items one at a time.
type StreamingToolHandler =
    Map<string, JsonElement> -> CancellationToken -> IAsyncEnumerable<Content>

/// Streaming tool definitions.
module StreamingTool =

    /// Collect all items from an IAsyncEnumerable into a list.
    let internal collectAsync (ct: CancellationToken) (enumerable: IAsyncEnumerable<'T>) = task {
        let e = enumerable.GetAsyncEnumerator(ct)
        let acc = ResizeArray<'T>()
        let mutable error : exn option = None
        try
            let mutable hasNext = true
            while hasNext do
                let! next = e.MoveNextAsync()
                if next then acc.Add(e.Current)
                else hasNext <- false
        with ex ->
            error <- Some ex
        do! e.DisposeAsync()
        match error with
        | Some ex -> return raise ex
        | None -> return List.ofSeq acc
    }

    /// Define a tool with a streaming handler.
    /// The handler yields Content items via IAsyncEnumerable.
    /// Items are collected and returned as a Content list.
    let define
        (name: string)
        (description: string)
        (handler: Map<string, JsonElement> -> CancellationToken -> IAsyncEnumerable<Content>)
        : Result<ToolDefinition, ValidationError> =
        let wrappedHandler (args: Map<string, JsonElement>) (cancellationToken: CancellationToken) = task {
            try
                let! items = collectAsync cancellationToken (handler args cancellationToken)
                return Ok items
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                return raise (OperationCanceledException(cancellationToken))
            | ex -> return Error (HandlerException ex)
        }
        match ToolName.create name with
        | Ok tn -> Ok { Name = tn; Description = description; InputSchema = None; Handler = wrappedHandler }
        | Error e -> Error e

    /// Define a typed streaming tool with auto-generated schema.
    let defineTyped<'TArgs>
        (name: string)
        (description: string)
        (handler: 'TArgs -> CancellationToken -> IAsyncEnumerable<Content>)
        : Result<ToolDefinition, ValidationError> =
        let schema = SchemaGen.generateSchema<'TArgs>()
        let deserializerOptions = TypedDeserializer.strictToolOptions ()
        let wrappedHandler (args: Map<string, JsonElement>) (cancellationToken: CancellationToken) = task {
            let deserialized =
                if not (TypedDeserializer.hasRequiredArguments schema args) then
                    Error(TypedDeserializer.invalidArguments "tool")
                else
                    try
                        let jsonObject = JsonObject()
                        for keyValue in args do
                            jsonObject.[keyValue.Key] <- JsonNode.Parse(keyValue.Value.GetRawText())
                        Ok(JsonSerializer.Deserialize<'TArgs>(jsonObject.ToJsonString(), deserializerOptions))
                    with :? JsonException ->
                        Error(TypedDeserializer.invalidArguments "tool")

            match deserialized with
            | Error error -> return raise error
            | Ok typedArgs ->
                try
                    let! items = collectAsync cancellationToken (handler typedArgs cancellationToken)
                    return Ok items
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return raise (OperationCanceledException(cancellationToken))
                | ex -> return Error (HandlerException ex)
        }
        match ToolName.create name with
        | Ok tn -> Ok { Name = tn; Description = description; InputSchema = Some schema; Handler = wrappedHandler }
        | Error e -> Error e
