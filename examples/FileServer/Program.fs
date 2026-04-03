open System.IO
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

type ReadFileArgs = { path: string }
type ListDirArgs = { path: string; pattern: string option }
type FileInfoArgs = { path: string }

let server = mcpServer {
    name "FileServer"
    version "1.0.0"

    tool (TypedTool.define<ReadFileArgs> "read_file" "Read a file's contents" (fun args -> task {
        try
            let! content = File.ReadAllTextAsync(args.path)
            return Ok [ Content.text content ]
        with ex ->
            return Error (TransportError $"Failed to read file: {ex.Message}")
    }) |> unwrapResult)

    tool (TypedTool.define<ListDirArgs> "list_directory" "List files in a directory" (fun args -> task {
        try
            let pattern = args.pattern |> Option.defaultValue "*"
            let files = Directory.GetFiles(args.path, pattern) |> Array.map Path.GetFileName
            let dirs = Directory.GetDirectories(args.path) |> Array.map Path.GetFileName
            let result =
                [| yield! dirs |> Array.map (fun d -> $"[dir] {d}")
                   yield! files |> Array.map (fun f -> $"      {f}") |]
                |> String.concat "\n"
            return Ok [ Content.text result ]
        with ex ->
            return Error (TransportError $"Failed to list directory: {ex.Message}")
    }) |> unwrapResult)

    tool (TypedTool.define<FileInfoArgs> "file_info" "Get file metadata" (fun args -> task {
        try
            let info = FileInfo(args.path)
            if not info.Exists then
                return Error (TransportError $"File not found: {args.path}")
            else
                let result = $"""{{
  "name": "{info.Name}",
  "size": {info.Length},
  "created": "{info.CreationTimeUtc:O}",
  "modified": "{info.LastWriteTimeUtc:O}",
  "extension": "{info.Extension}"
}}"""
                return Ok [ Content.text result ]
        with ex ->
            return Error (TransportError $"Failed to get file info: {ex.Message}")
    }) |> unwrapResult)

    useStdio
}

[<EntryPoint>]
let main _ =
    Server.run server |> fun t -> t.GetAwaiter().GetResult()
    0
