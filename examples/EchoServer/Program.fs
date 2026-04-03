open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

type EchoArgs = { message: string }
type ReverseArgs = { text: string; uppercase: bool option }

let server = mcpServer {
    name "EchoServer"
    version "1.0.0"

    tool (TypedTool.define<EchoArgs> "echo" "Echoes the message back" (fun args -> task {
        return Ok [ Content.text $"Echo: {args.message}" ]
    }) |> unwrapResult)

    tool (TypedTool.define<ReverseArgs> "reverse" "Reverses the text" (fun args -> task {
        let reversed = args.text |> Seq.rev |> System.String.Concat
        let result = if args.uppercase |> Option.defaultValue false then reversed.ToUpper() else reversed
        return Ok [ Content.text result ]
    }) |> unwrapResult)

    resource (
        Resource.define "info://server/status" "Server Status" (fun _ -> task {
            let uri = ResourceUri.create "info://server/status" |> unwrapResult
            let mime = MimeType.create "application/json" |> unwrapResult
            return Ok (TextResource (uri, mime, """{"status":"running","uptime":"∞"}"""))
        }) |> unwrapResult)

    prompt (
        Prompt.define "explain" [] (fun args -> task {
            let topic = args |> Map.tryFind "topic" |> Option.defaultValue "something"
            return Ok [
                { Role = User; Content = Content.text $"Please explain {topic} simply." }
                { Role = Assistant; Content = Content.text $"I'll explain {topic} in simple terms." }
            ]
        }) |> unwrapResult)

    useStdio
}

[<EntryPoint>]
let main _ =
    Server.run server |> fun t -> t.GetAwaiter().GetResult()
    0
