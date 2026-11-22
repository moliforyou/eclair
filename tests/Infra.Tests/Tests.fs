module Tests

open Expecto
open Testcontainers.PostgreSql
open Testcontainers.Redis
open System.Threading.Tasks
open ChessPlatform.Domain
open ChessPlatform.Application
open ChessPlatform.Infrastructure

let unwrap =
    function
    | Ok x -> x
    | Error e -> failwithf "Expected Ok, got Error: %A" e

let expectError =
    function
    | Ok _ -> failwith "Expected Error, got Ok"
    | Error e -> e

let pgContainer =
    lazy
        (let c = PostgreSqlBuilder("postgres:16-alpine").Build ()
         c.StartAsync().GetAwaiter().GetResult ()
         c)

let redisContainer =
    lazy
        (let c = RedisBuilder("redis:7-alpine").Build ()
         c.StartAsync().GetAwaiter().GetResult ()
         c)

[<Tests>]
let tests =
    testList "Infrastructure Tests" [
        testAsync "PostgresRepo OCC" {
            let pgConnectionString = pgContainer.Value.GetConnectionString ()
            let repo = PostgresRepo (pgConnectionString) :> IGameRepository
            let initialState = Game.newGame ()
            let gameId = "test-game-1"

            let record = {
                GameId = gameId
                State = initialState
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let! res1 = repo.CreateGame (record)
            Expect.isTrue (Result.isOk res1) "create should succeed"

            let! loadedOpt = repo.LoadGame (gameId)
            let loaded = Expect.wantSome loadedOpt "load game should return Some record"
            Expect.equal loaded.WhitePlayerId "w1" "WhitePlayerId should match"
            Expect.equal loaded.BlackPlayerId "b1" "BlackPlayerId should match"
            Expect.equal loaded.State.Version 0 "Version should match"

            let req: MoveRequest = {
                From = (4, 1)
                To = (4, 3)
                Promotion = None
            }

            let newState = Game.playMove initialState req |> unwrap
            let newRecord = { record with State = newState }

            let! res2 = repo.SaveGame (newRecord, 0)
            Expect.isTrue (Result.isOk res2) "save with correct version should succeed"

            let! res3 = repo.SaveGame (newRecord, 0)

            Expect.equal
                (expectError res3)
                SaveGameError.ConcurrencyConflict
                "save with old version should fail with ConcurrencyConflict"

            let! res4 = repo.SaveGame (newRecord, 1)
            Expect.isTrue (Result.isOk res4) "save with correct updated version should succeed"
        }

        testAsync "PostgresRepo LoadGame returns None for missing game" {
            let pgConnectionString = pgContainer.Value.GetConnectionString ()
            let repo = PostgresRepo (pgConnectionString) :> IGameRepository

            let! loaded = repo.LoadGame ("no-such-game")
            Expect.isNone loaded "loading a missing game should return None"
        }

        testAsync "PostgresRepo LoadGame returns None for malformed stored FEN" {
            let pgConnectionString = pgContainer.Value.GetConnectionString ()
            let repo = PostgresRepo (pgConnectionString) :> IGameRepository

            pgConnectionString
            |> Npgsql.FSharp.Sql.connect
            |> Npgsql.FSharp.Sql.query
                "INSERT INTO Games (GameId, StateFen, WhitePlayerId, BlackPlayerId, Version) VALUES ('corrupt-game', 'this is not a fen', 'w1', 'b1', 0)"
            |> Npgsql.FSharp.Sql.executeNonQuery
            |> ignore

            let! loaded = repo.LoadGame ("corrupt-game")
            Expect.isNone loaded "loading a game with a malformed FEN should return None, not crash"
        }

        testAsync "RedisBus PublishGameUpdated notifies the game events channel" {
            let redisConnectionString = redisContainer.Value.GetConnectionString ()
            let bus = RedisBus (redisConnectionString) :> IMessageBus
            let mux = StackExchange.Redis.ConnectionMultiplexer.Connect (redisConnectionString)
            let sub = mux.GetSubscriber ()

            let channel = StackExchange.Redis.RedisChannel.Literal ("game:test-game-3:events")

            let mutable messageReceived = false
            sub.Subscribe (channel, fun _ _ -> messageReceived <- true) |> ignore

            do! bus.PublishGameUpdated ("test-game-3")

            do! Async.Sleep 500

            Expect.isTrue messageReceived "GameUpdated notification should arrive on the game events channel"
        }

        testAsync "RedisBus DequeueMatchmakingPair handles empty queue, lone player, and full pair" {
            let redisConnectionString = redisContainer.Value.GetConnectionString ()
            let bus = RedisBus (redisConnectionString) :> IMessageBus

            let! empty = bus.DequeueMatchmakingPair ()
            Expect.equal empty [] "empty queue should dequeue an empty list"

            do! bus.EnqueueMatchmaking ("solo", 1000)
            let! single = bus.DequeueMatchmakingPair ()
            Expect.equal single [ ("solo", 1000) ] "a lone player should be popped as a single-entry list"

            do! bus.EnqueueMatchmaking ("bob", 1400)
            do! bus.EnqueueMatchmaking ("alice", 1200)
            let! pair = bus.DequeueMatchmakingPair ()
            Expect.equal pair [ ("alice", 1200); ("bob", 1400) ] "a full pair should be popped in elo order"
        }

        testAsync "RedisBus Pub/Sub" {
            let redisConnectionString = redisContainer.Value.GetConnectionString ()
            let bus = RedisBus (redisConnectionString) :> IMessageBus
            let mux = StackExchange.Redis.ConnectionMultiplexer.Connect (redisConnectionString)
            let sub = mux.GetSubscriber ()
            let channel = StackExchange.Redis.RedisChannel.Literal ("game:test-game-2:events")

            let mutable messageReceived = false
            sub.Subscribe (channel, fun _ _ -> messageReceived <- true) |> ignore

            let move = Normal ((4, 1), (4, 3))
            do! bus.PublishMovePlayed ("test-game-2", move)

            do! Async.Sleep 500

            Expect.isTrue messageReceived "message should be received"
        }
    ]

[<EntryPoint>]
let main args =
    let res = runTestsWithCLIArgs [] args tests

    if pgContainer.IsValueCreated then
        pgContainer.Value.DisposeAsync().AsTask().GetAwaiter().GetResult ()

    if redisContainer.IsValueCreated then
        redisContainer.Value.DisposeAsync().AsTask().GetAwaiter().GetResult ()

    res
