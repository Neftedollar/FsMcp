open FsMcp.Core
open FsMcp.Server

type CalcArgs = { a: float; b: float }

let server = mcpServer {
    name "Calculator"
    version "1.0.0"

    tool (TypedTool.define<CalcArgs> "add" "Add two numbers" (fun args -> task {
        return Ok [ Content.text $"{args.a + args.b}" ]
    }) |> unwrapResult)

    tool (TypedTool.define<CalcArgs> "subtract" "Subtract b from a" (fun args -> task {
        return Ok [ Content.text $"{args.a - args.b}" ]
    }) |> unwrapResult)

    tool (TypedTool.define<CalcArgs> "multiply" "Multiply two numbers" (fun args -> task {
        return Ok [ Content.text $"{args.a * args.b}" ]
    }) |> unwrapResult)

    tool (TypedTool.define<CalcArgs> "divide" "Divide a by b" (fun args -> task {
        if args.b = 0.0 then
            return Error (TransportError "Division by zero")
        else
            return Ok [ Content.text $"{args.a / args.b}" ]
    }) |> unwrapResult)

    useStdio
}

[<EntryPoint>]
let main _ =
    Server.run server |> fun t -> t.GetAwaiter().GetResult()
    0
