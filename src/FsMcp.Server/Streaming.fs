namespace FsMcp.Server

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Nodes
open FsMcp.Core
open FsMcp.Core.Validation

/// A streaming tool handler that yields content items one at a time.
type StreamingToolHandler = Map<string, JsonElement> -> IAsyncEnumerable<Content>

/// Streaming tool definitions.
module StreamingTool =

    /// Collect all items from an IAsyncEnumerable into a list.
    let private collectAsync (ct: CancellationToken) (enumerable: IAsyncEnumerable<'T>) = task {
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
        (handler: Map<string, JsonElement> -> IAsyncEnumerable<Content>)
        : Result<ToolDefinition, ValidationError> =
        let wrappedHandler (args: Map<string, JsonElement>) = task {
            try
                let! items = collectAsync CancellationToken.None (handler args)
                return Ok items
            with ex ->
                return Error (HandlerException ex)
        }
        match ToolName.create name with
        | Ok tn -> Ok { Name = tn; Description = description; InputSchema = None; Handler = wrappedHandler }
        | Error e -> Error e

    /// Define a typed streaming tool with auto-generated schema.
    let defineTyped<'TArgs>
        (name: string)
        (description: string)
        (handler: 'TArgs -> IAsyncEnumerable<Content>)
        : Result<ToolDefinition, ValidationError> =
        let schema = SchemaGen.generateSchema<'TArgs>()
        let deserializerOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        let wrappedHandler (args: Map<string, JsonElement>) = task {
            try
                let jsonObj = JsonObject()
                for kv in args do
                    jsonObj.[kv.Key] <- JsonNode.Parse(kv.Value.GetRawText())
                let json = jsonObj.ToJsonString()
                let typedArgs = JsonSerializer.Deserialize<'TArgs>(json, deserializerOptions)
                let! items = collectAsync CancellationToken.None (handler typedArgs)
                return Ok items
            with ex ->
                return Error (HandlerException ex)
        }
        match ToolName.create name with
        | Ok tn -> Ok { Name = tn; Description = description; InputSchema = Some schema; Handler = wrappedHandler }
        | Error e -> Error e
