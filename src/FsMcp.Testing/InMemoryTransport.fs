namespace FsMcp.Testing

open System
open System.IO
open System.IO.Pipelines
open System.Threading.Tasks

/// A pair of connected duplex streams for in-process MCP communication.
/// Pipe 1 (client -> server): client writes to ClientOutput, server reads from ServerInput.
/// Pipe 2 (server -> client): server writes to ServerOutput, client reads from ClientInput.
type TransportPair = {
    /// Stream the server reads requests from (connected to client writes).
    ServerInput: Stream
    /// Stream the server writes responses to (connected to client reads).
    ServerOutput: Stream
    /// Stream the client reads responses from (connected to server writes).
    ClientInput: Stream
    /// Stream the client writes requests to (connected to server reads).
    ClientOutput: Stream
} with
    interface IAsyncDisposable with
        member this.DisposeAsync() =
            task {
                this.ServerInput.Dispose()
                this.ServerOutput.Dispose()
                this.ClientInput.Dispose()
                this.ClientOutput.Dispose()
            }
            |> ValueTask

/// Functions for creating in-memory transport pairs.
module InMemoryTransport =

    /// Create a new isolated transport pair using System.IO.Pipelines.Pipe.
    /// Two pipes form a bidirectional channel:
    ///   Pipe 1: client writes -> server reads
    ///   Pipe 2: server writes -> client reads
    let create () : TransportPair =
        let clientToServer = Pipe()
        let serverToClient = Pipe()
        {
            ServerInput = clientToServer.Reader.AsStream()
            ClientOutput = clientToServer.Writer.AsStream()
            ServerOutput = serverToClient.Writer.AsStream()
            ClientInput = serverToClient.Reader.AsStream()
        }
