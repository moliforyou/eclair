module Tests

#nowarn "0044"

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

// Global fixtures
let pgContainer =
    lazy
        (let c = PostgreSqlBuilder().WithImage("postgres:16-alpine").Build ()
         c.StartAsync().GetAwaiter().GetResult ()
         c)

let redisContainer =
    lazy
        (let c = RedisBuilder().WithImage("redis:7-alpine").Build ()
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

            // 1. Create Game
            let! res1 = repo.CreateGame (record)
            Expect.isTrue (Result.isOk res1) "create should succeed"

            // 1b. Load Game (tests the postgres column parsing/casing)
            let! loadedOpt = repo.LoadGame (gameId)
            Expect.isTrue (Option.isSome loadedOpt) "load game should return Some record"
            let loaded = Option.get loadedOpt
            Expect.equal loaded.WhitePlayerId "w1" "WhitePlayerId should match"
            Expect.equal loaded.BlackPlayerId "b1" "BlackPlayerId should match"
            Expect.equal loaded.State.Version 0 "Version should match"

            // 2. Play a move
            let req: MoveRequest = {
                From = (4, 1)
                To = (4, 3)
                Promotion = None
            }

            let newState = Game.playMove initialState req |> unwrap
            let newRecord = { record with State = newState }

            // 3. Save game with correct version (0)
            let! res2 = repo.SaveGame (newRecord, 0)
            Expect.isTrue (Result.isOk res2) "save with correct version should succeed"

            // 4. Try saving again with the old version (0) -> should fail
            let! res3 = repo.SaveGame (newRecord, 0)

            Expect.equal
                (expectError res3)
                SaveGameError.ConcurrencyConflict
                "save with old version should fail with ConcurrencyConflict"

            // 5. Save with new version (1) -> should succeed
            let! res4 = repo.SaveGame (newRecord, 1)
            Expect.isTrue (Result.isOk res4) "save with correct updated version should succeed"
        }

        testAsync "RedisBus Pub/Sub" {
            let redisConnectionString = redisContainer.Value.GetConnectionString ()
            let bus = RedisBus (redisConnectionString) :> IMessageBus
            let mux = StackExchange.Redis.ConnectionMultiplexer.Connect (redisConnectionString)
            let sub = mux.GetSubscriber ()
            let channel = StackExchange.Redis.RedisChannel.Literal ("game:test-game-2:events")

            let mutable messageReceived = false
            sub.Subscribe (channel, fun _ _ -> messageReceived <- true) |> ignore

            // Publish
            let move = Normal ((4, 1), (4, 3))
            do! bus.PublishMovePlayed ("test-game-2", move)

            // Wait a bit for message to propagate
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
