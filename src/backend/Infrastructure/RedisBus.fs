namespace ChessPlatform.Infrastructure

open ChessPlatform.Domain
open ChessPlatform.Application
open StackExchange.Redis
open System.Text.Json

/// A simple DTO for moves to be published
type MoveDto = {
    From: Position
    To: Position
    Promotion: string option
}

type MatchFoundPayload = {
    GameId: string
    WhitePlayerId: string
    BlackPlayerId: string
}

type RedisBus(connectionString: string) =
    let redis = ConnectionMultiplexer.Connect (connectionString)
    let db = redis.GetDatabase ()
    let subscriber = redis.GetSubscriber ()

    interface IMessageBus with
        member this.PublishMovePlayed(gameId: string, move: Move) =
            async {
                let channel = RedisChannel.Literal ($"game:{gameId}:events")
                let dto = MoveExtensions.toDto move
                let json = JsonSerializer.Serialize (dto)
                let! _ = subscriber.PublishAsync (channel, RedisValue json) |> Async.AwaitTask
                return ()
            }

        member this.PublishGameUpdated(gameId: string) =
            async {
                let channel = RedisChannel.Literal ($"game:{gameId}:events")

                let! _ =
                    subscriber.PublishAsync (channel, RedisValue """{"Event":"GameUpdated"}""")
                    |> Async.AwaitTask

                return ()
            }

        member this.EnqueueMatchmaking(userId: string, elo: int) =
            async {
                let key = RedisKey "matchmaking:queue"
                let member' = RedisValue userId
                let score = float elo
                let! _ = db.SortedSetAddAsync (key, member', score) |> Async.AwaitTask
                return ()
            }

        member this.SubscribeMovePlayed(handler: string -> Async<unit>) =
            async {
                let channel = RedisChannel.Pattern ("game:*:events")

                let! queue = subscriber.SubscribeAsync (channel) |> Async.AwaitTask

                queue.OnMessage (fun msg ->
                    let ch = msg.Channel.ToString ()
                    // Extract gameId from "game:{gameId}:events"
                    let parts = ch.Split (':')

                    if parts.Length = 3 && parts.[0] = "game" && parts.[2] = "events" then
                        let gameId = parts.[1]
                        Async.Start (handler gameId)
                )

                let disposable =
                    { new System.IDisposable with
                        member _.Dispose() = queue.Unsubscribe ()
                    }

                return disposable
            }

        member this.PublishMatchFound(gameId: string, whitePlayerId: string, blackPlayerId: string) =
            async {
                let channel = RedisChannel.Literal "matchmaking:matches"

                let payload = {
                    GameId = gameId
                    WhitePlayerId = whitePlayerId
                    BlackPlayerId = blackPlayerId
                }

                let json = JsonSerializer.Serialize (payload)
                let! _ = subscriber.PublishAsync (channel, RedisValue json) |> Async.AwaitTask
                return ()
            }

        member this.SubscribeMatchFound(handler: string * string * string -> Async<unit>) =
            async {
                let channel = RedisChannel.Literal "matchmaking:matches"
                let! queue = subscriber.SubscribeAsync (channel) |> Async.AwaitTask

                queue.OnMessage (fun msg ->
                    try
                        let payload =
                            JsonSerializer.Deserialize<MatchFoundPayload> (msg.Message.ToString ())

                        match Option.ofObj payload with
                        | Some p -> Async.Start (handler (p.GameId, p.WhitePlayerId, p.BlackPlayerId))
                        | None -> ()
                    with _ ->
                        ()
                )

                let disposable =
                    { new System.IDisposable with
                        member _.Dispose() = queue.Unsubscribe ()
                    }

                return disposable
            }

        member this.DequeueMatchmakingPair() =
            async {
                let key = RedisKey "matchmaking:queue"

                let! (results: SortedSetEntry array) =
                    db.SortedSetPopAsync (key, 2L, Order.Ascending) |> Async.AwaitTask

                return
                    results
                    |> Array.map (fun (item: SortedSetEntry) -> (item.Element.ToString (), int item.Score))
                    |> Array.toList
            }
