namespace FsMcp.Server

open System.Text.Json
open FsMcp.Core
open FsMcp.Core.Validation

/// Built-in middleware that validates tool call arguments against the
/// tool's JSON Schema before invoking the handler.
module ValidationMiddleware =

    /// Find a tool definition by method name in a ServerConfig.
    let private findTool (config: ServerConfig) (method': string) (paramsEl: JsonElement option) =
        match method' with
        | "tools/call" ->
            paramsEl
            |> Option.bind (fun p ->
                match p.TryGetProperty("name") with
                | true, nameEl -> Some (nameEl.GetString())
                | _ -> None)
            |> Option.bind (fun name ->
                config.Tools
                |> List.tryFind (fun t -> ToolName.value t.Name = name))
        | _ -> None

    /// Validates that required schema properties are present in the arguments.
    let private validateArgs (schema: JsonElement) (args: JsonElement option) : Result<unit, string> =
        match schema.TryGetProperty("required") with
        | true, required ->
            let requiredFields =
                required.EnumerateArray()
                |> Seq.map (fun e -> e.GetString())
                |> Seq.toList
            let presentFields =
                match args with
                | Some a when a.ValueKind = JsonValueKind.Object ->
                    a.EnumerateObject()
                    |> Seq.map (fun p -> p.Name)
                    |> Set.ofSeq
                | _ -> Set.empty
            let missing =
                requiredFields
                |> List.filter (fun f -> not (presentFields.Contains f))
            if List.isEmpty missing then Ok ()
            else
                let missingStr = missing |> String.concat ", "
                Error $"Missing required fields: {missingStr}"
        | _ -> Ok ()

    /// Create a validation middleware for the given ServerConfig.
    /// Rejects tool calls with missing required fields before the handler runs.
    let create (config: ServerConfig) : McpMiddleware =
        fun ctx next ->
            match findTool config ctx.Method ctx.Params with
            | Some tool ->
                match tool.InputSchema with
                | Some schema ->
                    let args =
                        ctx.Params
                        |> Option.bind (fun p ->
                            match p.TryGetProperty("arguments") with
                            | true, a -> Some a
                            | _ -> None)
                    match validateArgs schema args with
                    | Ok () -> next ctx
                    | Error msg ->
                        System.Threading.Tasks.Task.FromResult(
                            McpResponseError (TransportError $"Validation failed: {msg}"))
                | None -> next ctx
            | None -> next ctx
