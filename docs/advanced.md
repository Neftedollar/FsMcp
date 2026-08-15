---
title: Advanced
category: Guides
categoryindex: 1
index: 0
---

# Advanced

## Cancellation-aware streaming

`StreamingTool.define` collects an `IAsyncEnumerable<Content>` into the MCP tool
result. In 2.0 both the handler and the enumerator receive the protocol request
token. Long-running producers should observe it when acquiring and yielding
items.

```fsharp
open System.Collections.Generic
open FsMcp.Core
open FsMcp.Server

let streamTool =
    StreamingTool.define "count" "Count to N" (fun args cancellationToken ->
        let count =
            args
            |> Map.tryFind "count"
            |> Option.map (fun value -> value.GetInt32())
            |> Option.defaultValue 5

        { new IAsyncEnumerable<Content> with
            member _.GetAsyncEnumerator(requestToken) =
                let linked =
                    System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        requestToken)
                let mutable current = 0

                { new IAsyncEnumerator<Content> with
                    member _.Current = Content.text $"Count: {current}"

                    member _.MoveNextAsync() =
                        linked.Token.ThrowIfCancellationRequested()
                        current <- current + 1
                        ValueTask<bool>(current <= count)

                    member _.DisposeAsync() =
                        linked.Dispose()
                        ValueTask() } })
    |> unwrapResult
```

`StreamingTool.defineTyped<'T>` provides the same cancellation contract with a
strictly deserialized F# record input. JSON strings are not coerced into numeric
or Boolean record fields.

## Runtime extension points

FsMcp 2.0 composes with the official SDK instead of advertising disconnected
parallel middleware and notification runtimes:

- use SDK request filters for protocol request interception;
- use caller-owned dependency injection for services and request-scoped SDK
  primitives;
- use ASP.NET Core middleware, authentication, and authorization around
  `MapMcp` for Streamable HTTP;
- use the SDK request-scoped sampling primitive when a server must ask the
  connected client to sample.

The legacy FsMcp middleware, `DynamicServer`, contextual notification, and
`SamplingTool.define` entry points are obsolete and fail closed when their old
behavior was never connected to the transport.

## Resource subscriptions

Streamable HTTP subscriptions are stateful. Subscription identifiers and errors
are opaque FsMcp values; create them through the public modules instead of
depending on their representation. Notification fan-out is bounded, observes
caller cancellation, and removes failed sessions. Disconnect cleanup removes
the session's subscriptions.

Stateless HTTP deliberately omits the subscribe capability and rejects
subscription requests. It does not claim a stateful feature that it cannot
honor.

## Sampling package boundary

`FsMcp.Sampling` continues to provide sampling domain types, request builders,
and explicit test helpers. It does not provide working server-to-client runtime
wiring in 2.0. `SamplingTool.define` rejects registration rather than silently
using a no-op sampling context.

## TypeShape schema caching

Typed handler schemas and optional-field detection are cached per input type.
The first definition pays the reflection cost; later definitions reuse the
generated schema. F# option fields are omitted from `required` and represented
as nullable where appropriate.
