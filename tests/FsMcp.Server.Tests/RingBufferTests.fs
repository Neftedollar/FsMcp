module FsMcp.Server.Tests.RingBufferTests

open Expecto
open FsMcp.Server

// Telemetry.RingBuffer is internal in FsMcp.Server; accessible here via InternalsVisibleTo.

[<Tests>]
let ringBufferTests =
    testList "RingBuffer" [
        testCase "Add below capacity tracks count correctly" <| fun _ ->
            let buf = Telemetry.RingBuffer(10)
            for i in 1..5 do buf.Add(int64 i)
            Expect.equal buf.Count 5 "count should be 5"
            // Average of 1+2+3+4+5 = 15 / 5 = 3.0
            Expect.equal (buf.Average()) 3.0 "average of 1..5"

        testCase "Add at capacity overwrites oldest" <| fun _ ->
            let buf = Telemetry.RingBuffer(10)
            // Add 11 values: 1..11
            for i in 1..11 do buf.Add(int64 i)
            Expect.equal buf.Count 10 "count should be 10 (at capacity)"
            // After overwrite, buffer should contain 2..11
            // Average of (2+3+...+11) = sum(2..11) = 65, /10 = 6.5
            Expect.equal (buf.Average()) 6.5 "average of 2..11 = 6.5"

        testCase "Add many wraps multiple times" <| fun _ ->
            let buf = Telemetry.RingBuffer(10)
            // Add 100 values: 1..100
            for i in 1..100 do buf.Add(int64 i)
            Expect.equal buf.Count 10 "count should be 10 (at capacity)"
            // Last 10 values are 91..100, average = (91+92+...+100)/10 = 955/10 = 95.5
            Expect.equal (buf.Average()) 95.5 "average of 91..100 = 95.5"

        testCase "Empty buffer returns Average 0.0" <| fun _ ->
            let buf = Telemetry.RingBuffer(10)
            Expect.equal buf.Count 0 "count should be 0"
            Expect.equal (buf.Average()) 0.0 "average of empty buffer is 0.0"
    ]
