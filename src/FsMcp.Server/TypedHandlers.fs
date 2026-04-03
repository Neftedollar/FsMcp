namespace FsMcp.Server

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Schema
open System.Text.Json.Serialization.Metadata
open System.Threading.Tasks
open TypeShape.Core
open FsMcp.Core
open FsMcp.Core.Validation

/// JSON Schema generation with F# option-awareness via TypeShape.
/// All TypeShape reflection results are cached per-type for performance.
module internal SchemaGen =

    open System.Collections.Concurrent

    let private jsonOptions =
        let opts = JsonSerializerOptions()
        opts.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
        opts

    /// Cache: Type → set of field names that are F# option types.
    let private optionFieldsCache = ConcurrentDictionary<Type, Set<string>>()

    /// Cache: Type → generated JSON Schema as JsonElement.
    let private schemaCache = ConcurrentDictionary<Type, JsonElement>()

    /// Get the set of field names that are F# option types using TypeShape.
    /// Result is cached per type.
    let getOptionFields<'T> () : Set<string> =
        optionFieldsCache.GetOrAdd(typeof<'T>, fun _ ->
            let shape = shapeof<'T>
            match shape with
            | Shape.FSharpRecord s ->
                s.Fields
                |> Array.choose (fun f ->
                    let propInfo = f.MemberInfo :?> Reflection.PropertyInfo
                    let propType = propInfo.PropertyType
                    if propType.IsGenericType
                       && propType.GetGenericTypeDefinition() = typedefof<option<_>> then
                        Some f.Label
                    else
                        None)
                |> Set.ofArray
            | _ -> Set.empty)

    /// Generate a JSON Schema for type 'T, with F# option fields
    /// marked as not-required and nullable.
    /// Result is cached per type.
    let generateSchema<'T> () : JsonElement =
        schemaCache.GetOrAdd(typeof<'T>, fun typ ->
            let schemaNode = jsonOptions.GetJsonSchemaAsNode(typ)
            let optionFields = getOptionFields<'T> ()

            match schemaNode with
            | :? JsonObject as obj ->
                // Always fix top-level type: ["object", "null"] → "object"
                match obj.["type"] with
                | :? JsonArray as typeArr when typeArr.Count = 2 ->
                    obj.["type"] <- JsonValue.Create("object")
                | _ -> ()

                // Fix property types: strip null from required property types
                match obj.["properties"] with
                | :? JsonObject as props ->
                    for prop in props do
                        match prop.Value with
                        | :? JsonObject as propObj ->
                            match propObj.["type"] with
                            | :? JsonArray as typeArr when typeArr.Count = 2 ->
                                if not (optionFields.Contains(prop.Key)) then
                                    let nonNull =
                                        typeArr |> Seq.cast<JsonNode>
                                        |> Seq.tryFind (fun n -> n.GetValue<string>() <> "null")
                                    match nonNull with
                                    | Some v -> propObj.["type"] <- JsonValue.Create(v.GetValue<string>())
                                    | None -> ()
                            | _ -> ()
                        | _ -> ()
                | _ -> ()

                // Remove option fields from "required"
                if not (Set.isEmpty optionFields) then
                    match obj.["required"] with
                    | :? JsonArray as required ->
                        let toRemove =
                            required
                            |> Seq.cast<JsonNode>
                            |> Seq.filter (fun n -> optionFields.Contains(n.GetValue<string>()))
                            |> Seq.toList
                        for node in toRemove do
                            required.Remove(node) |> ignore
                        if required.Count = 0 then
                            obj.Remove("required") |> ignore
                    | _ -> ()
            | _ -> ()

            JsonSerializer.SerializeToElement(schemaNode, jsonOptions))

/// Typed handler definitions using F# records as input.
/// TypeShape inspects the record to generate JSON Schema automatically.
module TypedTool =

    let private deserializerOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    /// Define a tool with a strongly-typed F# record as input.
    /// JSON Schema is auto-generated from the record type via TypeShape.
    let define<'TArgs>
        (name: string)
        (description: string)
        (handler: 'TArgs -> Task<Result<Content list, McpError>>)
        : Result<ToolDefinition, ValidationError> =

        let schema = SchemaGen.generateSchema<'TArgs> ()

        let rawHandler (args: Map<string, JsonElement>) =
            task {
                try
                    // Convert Map<string, JsonElement> → JSON string → 'TArgs
                    let jsonObj = JsonObject()
                    for kv in args do
                        jsonObj.[kv.Key] <- JsonNode.Parse(kv.Value.GetRawText())
                    let json = jsonObj.ToJsonString()
                    let typedArgs = JsonSerializer.Deserialize<'TArgs>(json, deserializerOptions)
                    return! handler typedArgs
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

module TypedResource =

    let private deserializerOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    /// Define a resource with a strongly-typed F# record as handler input.
    let define<'TArgs>
        (uri: string)
        (name: string)
        (handler: 'TArgs -> Task<Result<ResourceContents, McpError>>)
        : Result<ResourceDefinition, ValidationError> =

        let rawHandler (args: Map<string, string>) =
            task {
                try
                    let jsonObj = JsonObject()
                    for kv in args do
                        jsonObj.[kv.Key] <- JsonValue.Create(kv.Value)
                    let json = jsonObj.ToJsonString()
                    let typedArgs = JsonSerializer.Deserialize<'TArgs>(json, deserializerOptions)
                    return! handler typedArgs
                with ex ->
                    return Error (HandlerException ex)
            }

        match ResourceUri.create uri with
        | Ok ru ->
            Ok {
                Uri = ru
                Name = name
                Description = None
                MimeType = None
                Handler = rawHandler
            }
        | Error e -> Error e

module TypedPrompt =

    let private deserializerOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    /// Define a prompt with a strongly-typed F# record as handler input.
    let define<'TArgs>
        (name: string)
        (description: string)
        (handler: 'TArgs -> Task<Result<McpMessage list, McpError>>)
        : Result<PromptDefinition, ValidationError> =

        let schema = SchemaGen.generateSchema<'TArgs> ()

        // Extract argument definitions from schema
        let promptArgs =
            match schema.ValueKind with
            | JsonValueKind.Object ->
                match schema.TryGetProperty("properties") with
                | true, props ->
                    let optionFields = SchemaGen.getOptionFields<'TArgs> ()
                    props.EnumerateObject()
                    |> Seq.map (fun p ->
                        { PromptArgument.Name = p.Name
                          Description = None
                          Required = not (optionFields.Contains(p.Name)) })
                    |> Seq.toList
                | _ -> []
            | _ -> []

        let rawHandler (args: Map<string, string>) =
            task {
                try
                    let jsonObj = JsonObject()
                    for kv in args do
                        jsonObj.[kv.Key] <- JsonValue.Create(kv.Value)
                    let json = jsonObj.ToJsonString()
                    let typedArgs = JsonSerializer.Deserialize<'TArgs>(json, deserializerOptions)
                    return! handler typedArgs
                with ex ->
                    return Error (HandlerException ex)
            }

        match PromptName.create name with
        | Ok pn ->
            Ok {
                Name = pn
                Description = Some description
                Arguments = promptArgs
                Handler = rawHandler
            }
        | Error e -> Error e
