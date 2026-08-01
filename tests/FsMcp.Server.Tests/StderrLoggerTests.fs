module FsMcp.Server.Tests.StderrLoggerTests

open System
open System.Diagnostics
open Expecto
open Microsoft.Extensions.Logging
open FsMcp.Server

// Regression cover for the stdio wedge fixed in 1.2.0.
//
// The original defect: the built-in console logger writes stderr through
// ConsolePal, which takes ONE process-wide monitor shared with stdout. When a host
// spawns an MCP server over stdio and never reads its stderr, the pipe fills at
// 64 KB, the logger's write parks inside that lock, and the server's next response
// to stdout blocks behind it. Measured end-to-end against the EchoServer example
// with stderr never drained: 204 replies before going silent, versus 938,933 after
// the fix.
//
// Two properties keep that from coming back, and both are asserted below:
//   1. Logging never blocks the calling thread, whatever the sink is doing.
//   2. The provider survives more traffic than its channel can hold, by dropping.
//
// The lock itself cannot be exercised in-process — it needs a real child process
// with an unread stderr pipe. See CHANGELOG 1.2.0 for the reproduction.

[<Tests>]
let stderrLoggerTests =
    testList "NonBlockingStderrLogger" [

        testCase "logging never blocks the caller" <| fun _ ->
            // Capacity 8 against 50k messages: the channel is overwhelmed by four
            // orders of magnitude. If enqueue ever waits on the writer, this hangs.
            use provider = new NonBlockingStderrLoggerProvider(LogLevel.Information, 8)
            let logger = (provider :> ILoggerProvider).CreateLogger("regression")

            let sw = Stopwatch.StartNew()
            for i in 1..50_000 do
                logger.LogInformation("message {Index}", i)
            sw.Stop()

            Expect.isLessThan
                sw.Elapsed.TotalSeconds
                10.0
                "50k log calls against a capacity-8 channel must not block the caller"

        testCase "respects the minimum level" <| fun _ ->
            use provider = new NonBlockingStderrLoggerProvider(LogLevel.Warning, 16)
            let logger = (provider :> ILoggerProvider).CreateLogger("levels")
            Expect.isFalse (logger.IsEnabled LogLevel.Information) "Information is below Warning"
            Expect.isTrue (logger.IsEnabled LogLevel.Warning) "Warning is enabled"
            Expect.isTrue (logger.IsEnabled LogLevel.Error) "Error is above Warning"
            Expect.isFalse (logger.IsEnabled LogLevel.None) "None is never enabled"

        testCase "disposing twice is safe" <| fun _ ->
            let provider = new NonBlockingStderrLoggerProvider(LogLevel.Information, 16)
            let disposable = provider :> IDisposable
            disposable.Dispose()
            disposable.Dispose()

        testCase "logging after dispose does not throw" <| fun _ ->
            let provider = new NonBlockingStderrLoggerProvider(LogLevel.Information, 16)
            let logger = (provider :> ILoggerProvider).CreateLogger("after-dispose")
            (provider :> IDisposable).Dispose()
            // The channel is completed; TryWrite returns false and the call is a no-op.
            // A host tearing down while requests drain must not see an exception here.
            logger.LogInformation("late message")
    ]
