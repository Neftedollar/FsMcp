namespace FsMcp.Server

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Schema
open System.Text.Json.Serialization
open System.Text.Json.Serialization.Metadata
open System.Threading
open System.Threading.Tasks
open TypeShape.Core
open FsMcp.Core
open FsMcp.Core.Validation
open ModelContextProtocol

/// JSON Schema generation with F# option-awareness via TypeShape.
/// All TypeShape reflection results are cached per-type for performance.
module internal SchemaGen =

    open System.Collections.Concurrent

    let private jsonOptions =
        let options = JsonSerializerOptions()
        options.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
        options

    let private optionFieldsCache = ConcurrentDictionary<Type, Set<string>>()
    let private schemaCache = ConcurrentDictionary<Type, JsonElement>()

    let getOptionFields<'T> () : Set<string> =
        optionFieldsCache.GetOrAdd(typeof<'T>, fun _ ->
            match shapeof<'T> with
            | Shape.FSharpRecord shape ->
                shape.Fields
                |> Array.choose (fun field ->
                    let property = field.MemberInfo :?> Reflection.PropertyInfo
                    let propertyType = property.PropertyType
                    if propertyType.IsGenericType
                       && propertyType.GetGenericTypeDefinition() = typedefof<option<_>> then
                        Some field.Label
                    else
                        None)
                |> Set.ofArray
            | _ -> Set.empty)

    let generateSchema<'T> () : JsonElement =
        schemaCache.GetOrAdd(typeof<'T>, fun typ ->
            let schemaNode = jsonOptions.GetJsonSchemaAsNode(typ)
            let optionFields = getOptionFields<'T> ()

            match schemaNode with
            | :? JsonObject as schemaObject ->
                match schemaObject.["type"] with
                | :? JsonArray as types when types.Count = 2 ->
                    schemaObject.["type"] <- JsonValue.Create("object")
                | _ -> ()

                match schemaObject.["properties"] with
                | :? JsonObject as properties ->
                    for property in properties do
                        match property.Value with
                        | :? JsonObject as propertyObject ->
                            match propertyObject.["type"] with
                            | :? JsonArray as types when types.Count = 2 && not (optionFields.Contains property.Key) ->
                                types
                                |> Seq.cast<JsonNode>
                                |> Seq.tryFind (fun node -> node.GetValue<string>() <> "null")
                                |> Option.iter (fun node ->
                                    propertyObject.["type"] <- JsonValue.Create(node.GetValue<string>()))
                            | _ -> ()
                        | _ -> ()
                | _ -> ()

                if not (Set.isEmpty optionFields) then
                    match schemaObject.["required"] with
                    | :? JsonArray as required ->
                        required
                        |> Seq.cast<JsonNode>
                        |> Seq.filter (fun node -> optionFields.Contains(node.GetValue<string>()))
                        |> Seq.toList
                        |> List.iter (required.Remove >> ignore)

                        if required.Count = 0 then
                            schemaObject.Remove("required") |> ignore
                    | _ -> ()
            | _ -> ()

            JsonSerializer.SerializeToElement(schemaNode, jsonOptions))

/// Reads resource and prompt protocol strings as their declared scalar types.
type internal FlexibleBooleanConverter() =
    inherit JsonConverter<bool>()

    override _.Read(reader: byref<Utf8JsonReader>, _, _) =
        match reader.TokenType with
        | JsonTokenType.True -> true
        | JsonTokenType.False -> false
        | JsonTokenType.String ->
            match Boolean.TryParse(reader.GetString()) with
            | true, value -> value
            | _ -> raise (JsonException("Expected 'true' or 'false'."))
        | _ -> raise (JsonException("Expected a JSON boolean or boolean string."))

    override _.Write(writer, value, _) = writer.WriteBooleanValue(value)

module internal TypedDeserializer =
    let strictToolOptions () =
        JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    let protocolStringOptions () =
        let options =
            JsonSerializerOptions(
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString)
        options.Converters.Add(FlexibleBooleanConverter())
        options

    // TypedHandler.create is a tool-only entry point.
    let options = strictToolOptions

    let invalidArguments primitive =
        McpProtocolException($"Invalid {primitive} arguments.", McpErrorCode.InvalidParams)

    let invalidResult primitive =
        ProtocolError(int McpErrorCode.InvalidParams, $"Invalid {primitive} arguments.")

    let private requiredArgumentNames (schema: JsonElement) =
        match schema.TryGetProperty("required") with
        | false, _ -> Seq.empty
        | true, required ->
            required.EnumerateArray()
            |> Seq.choose (fun item -> Option.ofObj (item.GetString()))

    let hasRequiredArguments (schema: JsonElement) (arguments: Map<string, JsonElement>) =
        requiredArgumentNames schema
        |> Seq.forall (fun name ->
            match Map.tryFind name arguments with
            | Some value ->
                value.ValueKind <> JsonValueKind.Null
                && value.ValueKind <> JsonValueKind.Undefined
            | None -> false)

    let hasRequiredStringArguments (schema: JsonElement) (arguments: Map<string, string>) =
        requiredArgumentNames schema
        |> Seq.forall (fun name ->
            arguments |> Map.tryFind name |> Option.exists (isNull >> not))

module TypedTool =

    let private deserializerOptions = TypedDeserializer.strictToolOptions ()

    /// Define a cancellation-aware tool with a strongly-typed F# record as input.
    /// JSON Schema is auto-generated from the record type via TypeShape.
    let define<'TArgs>
        (name: string)
        (description: string)
        (handler: 'TArgs -> CancellationToken -> Task<Result<Content list, McpError>>)
        : Result<ToolDefinition, ValidationError> =

        let schema = SchemaGen.generateSchema<'TArgs> ()

        let rawHandler (arguments: Map<string, JsonElement>) (cancellationToken: CancellationToken) =
            task {
                let deserialized =
                    if not (TypedDeserializer.hasRequiredArguments schema arguments) then
                        Error(TypedDeserializer.invalidArguments "tool")
                    else
                        try
                            let jsonObject = JsonObject()
                            for keyValue in arguments do
                                jsonObject.[keyValue.Key] <- JsonNode.Parse(keyValue.Value.GetRawText())
                            Ok(JsonSerializer.Deserialize<'TArgs>(jsonObject.ToJsonString(), deserializerOptions))
                        with :? JsonException ->
                            Error(TypedDeserializer.invalidArguments "tool")

                match deserialized with
                | Error error -> return raise error
                | Ok typedArguments ->
                    try
                        return! handler typedArguments cancellationToken
                    with
                    | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                        return raise (OperationCanceledException(cancellationToken))
                    | error -> return Error(HandlerException error)
            }

        ToolName.create name
        |> Result.map (fun toolName ->
            { Name = toolName
              Description = description
              InputSchema = Some schema
              Handler = rawHandler })

module TypedResource =

    let private deserializerOptions = TypedDeserializer.protocolStringOptions ()

    let define<'TArgs>
        (uri: string)
        (name: string)
        (handler: 'TArgs -> CancellationToken -> Task<Result<ResourceContents, McpError>>)
        : Result<ResourceDefinition, ValidationError> =

        let schema = SchemaGen.generateSchema<'TArgs> ()

        let rawHandler (arguments: Map<string, string>) (cancellationToken: CancellationToken) =
            task {
                let deserialized =
                    if not (TypedDeserializer.hasRequiredStringArguments schema arguments) then
                        Error(TypedDeserializer.invalidResult "resource")
                    else
                        try
                            let jsonObject = JsonObject()
                            for keyValue in arguments do
                                jsonObject.[keyValue.Key] <- JsonValue.Create(keyValue.Value)
                            Ok(JsonSerializer.Deserialize<'TArgs>(jsonObject.ToJsonString(), deserializerOptions))
                        with :? JsonException ->
                            Error(TypedDeserializer.invalidResult "resource")

                match deserialized with
                | Error error -> return Error error
                | Ok typedArguments ->
                    try
                        return! handler typedArguments cancellationToken
                    with
                    | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                        return raise (OperationCanceledException(cancellationToken))
                    | error -> return Error(HandlerException error)
            }

        ResourceUri.create uri
        |> Result.map (fun resourceUri ->
            { Uri = resourceUri
              Name = name
              Description = None
              MimeType = None
              Handler = rawHandler })

module TypedPrompt =

    let private deserializerOptions = TypedDeserializer.protocolStringOptions ()

    let define<'TArgs>
        (name: string)
        (description: string)
        (handler: 'TArgs -> CancellationToken -> Task<Result<McpMessage list, McpError>>)
        : Result<PromptDefinition, ValidationError> =

        let schema = SchemaGen.generateSchema<'TArgs> ()
        let optionFields = SchemaGen.getOptionFields<'TArgs> ()
        let promptArguments =
            match schema.TryGetProperty("properties") with
            | true, properties ->
                properties.EnumerateObject()
                |> Seq.map (fun property ->
                    { Name = property.Name
                      Description = None
                      Required = not (optionFields.Contains property.Name) })
                |> Seq.toList
            | _ -> []

        let rawHandler (arguments: Map<string, string>) (cancellationToken: CancellationToken) =
            task {
                let deserialized =
                    if not (TypedDeserializer.hasRequiredStringArguments schema arguments) then
                        Error(TypedDeserializer.invalidResult "prompt")
                    else
                        try
                            let jsonObject = JsonObject()
                            for keyValue in arguments do
                                jsonObject.[keyValue.Key] <- JsonValue.Create(keyValue.Value)
                            Ok(JsonSerializer.Deserialize<'TArgs>(jsonObject.ToJsonString(), deserializerOptions))
                        with :? JsonException ->
                            Error(TypedDeserializer.invalidResult "prompt")

                match deserialized with
                | Error error -> return Error error
                | Ok typedArguments ->
                    try
                        return! handler typedArguments cancellationToken
                    with
                    | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                        return raise (OperationCanceledException(cancellationToken))
                    | error -> return Error(HandlerException error)
            }

        PromptName.create name
        |> Result.map (fun promptName ->
            { Name = promptName
              Description = Some description
              Arguments = promptArguments
              Handler = rawHandler })
