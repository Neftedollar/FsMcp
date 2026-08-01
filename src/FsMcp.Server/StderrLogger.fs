namespace FsMcp.Server

open System
open System.IO
open System.Text
open System.Threading.Channels
open Microsoft.Extensions.Logging
open Microsoft.Win32.SafeHandles

// ─────────────────────────────────────────────────────────────────────────────
//  Non-blocking stderr logging
// ─────────────────────────────────────────────────────────────────────────────
//
//  Why this exists instead of Microsoft.Extensions.Logging.Console:
//
//  Over stdio the host owns the child's stderr pipe. A host that spawns an MCP
//  server and never reads its stderr lets that pipe fill — 64 KB on macOS and
//  Linux, reached after only a couple of hundred logged requests.
//
//  At that point the built-in console logger wedges the entire server. Its
//  queue-processing thread blocks inside the write to stderr while holding the
//  message-queue monitor, so every subsequent ILogger call blocks acquiring that
//  same monitor — including calls made on the request path. The server stops
//  answering while still holding live requests.
//
//  ConsoleLoggerOptions.QueueFullMode = DropWrite does NOT fix this. That setting
//  governs behaviour once the queue is *full*, but the block happens on the
//  monitor long before the queue fills. Measured: 204 requests to wedge with
//  DropWrite set, versus 976,522 with stderr logging off entirely.
//
//  The actual mechanism, captured with dotnet-stack on a wedged server:
//
//      writer thread   ConsolePal.WriteFromConsoleStream   <- holds the lock
//                      Interop.Sys.Write                   <- parked, pipe full
//
//      SDK response    ConsoleStream.Write
//                      ConsolePal.WriteFromConsoleStream
//                      Monitor.Enter_Slowpath              <- waits on that lock
//
//  ConsolePal.WriteFromConsoleStream takes ONE process-wide monitor shared by every
//  console stream. stdout and stderr are not independent: a stalled write to stderr
//  holds the lock, and the server's next response to stdout blocks behind it. The
//  server goes silent while every caller is perfectly healthy.
//
//  Two consequences shape the design below:
//
//  1. Never reach stderr through Console.OpenStandardError / Console.Error. Those
//     produce a ConsoleStream and take that shared lock. We open file descriptor 2
//     directly, so a blocked stderr write can never stall stdout.
//  2. Callers only ever Channel.TryWrite on a bounded channel in DropWrite mode,
//     which returns immediately and never blocks or throws. A single dedicated
//     thread owns the descriptor; if the pipe fills, that one thread parks and log
//     lines are dropped.

/// Writes formatted log lines to stderr from a single background task.
/// Callers never block: enqueue is a non-blocking TryWrite that drops on overflow.
type NonBlockingStderrLoggerProvider(minLevel: LogLevel, capacity: int) =

    let channel =
        Channel.CreateBounded<string>(
            BoundedChannelOptions(
                capacity,
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false))

    /// The stderr sink.
    ///
    /// On Unix this is file descriptor 2 opened directly, deliberately NOT
    /// Console.OpenStandardError(): a ConsoleStream routes every write through
    /// ConsolePal's process-wide console monitor, and a write stalled on a full pipe
    /// takes stdout down with it. ownsHandle is false — disposing this stream must
    /// not close the host's stderr.
    ///
    /// On Windows the raw descriptor number is meaningless (handles are opaque, and
    /// `nativeint 2` throws "The handle is invalid"), and ConsolePal.Windows does not
    /// share that monitor, so the standard stream is both correct and safe there.
    let stderr : Stream =
        if OperatingSystem.IsWindows() then
            Console.OpenStandardError()
        else
            new FileStream(
                new SafeFileHandle(nativeint 2, ownsHandle = false),
                FileAccess.Write,
                bufferSize = 4096)

    // Owns the descriptor for the lifetime of the provider. A dedicated thread, not
    // a ThreadPool work item: writing to a full pipe blocks, and a blocked pool
    // thread is one the request pipeline no longer has.
    let pumpThread =
        let run () =
            try
                let reader = channel.Reader
                let mutable draining = true
                while draining do
                    if not (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) then
                        draining <- false
                    else
                        let mutable line = Unchecked.defaultof<string>
                        while reader.TryRead(&line) do
                            let bytes = Encoding.UTF8.GetBytes line
                            stderr.Write(bytes, 0, bytes.Length)
                        stderr.Flush()
            with _ ->
                // Broken pipe, closed handle, host teardown — diagnostics are
                // best-effort and must never surface into the server.
                ()

        let t = Threading.Thread(run, IsBackground = true, Name = "FsMcp stderr log writer")
        t.Start()
        t

    let mutable disposed = false

    /// Enqueue a preformatted line. Never blocks; returns whether it was accepted.
    member internal _.TryEnqueue(line: string) = channel.Writer.TryWrite(line)

    member internal _.MinLevel = minLevel

    new(minLevel: LogLevel) = new NonBlockingStderrLoggerProvider(minLevel, 4096)

    interface ILoggerProvider with
        member this.CreateLogger(categoryName: string) : ILogger =
            NonBlockingStderrLogger(categoryName, this) :> ILogger

        member _.Dispose() =
            if not disposed then
                disposed <- true
                channel.Writer.TryComplete() |> ignore
                // Bounded join: if the host never drained stderr the writer is parked
                // in a write that will not return, and shutdown must not hang on it.
                // The thread is IsBackground, so abandoning it cannot keep the process up.
                pumpThread.Join(TimeSpan.FromMilliseconds 250.) |> ignore
                try stderr.Dispose() with _ -> ()

/// ILogger that formats a message and hands it to the provider's channel.
and internal NonBlockingStderrLogger(category: string, provider: NonBlockingStderrLoggerProvider) =

    static let shortName (level: LogLevel) =
        match level with
        | LogLevel.Trace -> "trce"
        | LogLevel.Debug -> "dbug"
        | LogLevel.Information -> "info"
        | LogLevel.Warning -> "warn"
        | LogLevel.Error -> "fail"
        | LogLevel.Critical -> "crit"
        | _ -> "none"

    interface ILogger with
        member _.BeginScope(_state) = null

        member _.IsEnabled(level: LogLevel) =
            level <> LogLevel.None && level >= provider.MinLevel

        member this.Log(level, _eventId, state, ex, formatter) =
            if (this :> ILogger).IsEnabled level then
                let message = formatter.Invoke(state, ex)
                if not (String.IsNullOrEmpty message) then
                    let sb = StringBuilder(message.Length + category.Length + 32)
                    sb
                        .Append(shortName level)
                        .Append(": ")
                        .Append(category)
                        .Append(Environment.NewLine)
                        .Append("      ")
                        .Append(message)
                        .Append(Environment.NewLine)
                    |> ignore
                    if not (isNull (box ex)) then
                        sb.Append(ex.ToString()).Append(Environment.NewLine) |> ignore
                    // Dropped on overflow by design — see the note at the top of this file.
                    provider.TryEnqueue(sb.ToString()) |> ignore
