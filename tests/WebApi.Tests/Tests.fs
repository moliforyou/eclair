module Tests

#nowarn "3261"
#nowarn "0760"

open System
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open ChessPlatform.Domain
open ChessPlatform.Application
open ChessPlatform.WebApi

type MockRepo(initialState: GameRecord option, saveResult: Result<unit, SaveGameError>) =
    interface IGameRepository with
        member _.LoadGame _ = async { return initialState }
        member _.SaveGame(_, _) = async { return saveResult }
        member _.CreateGame _ = async { return Ok () }

type IntegrationRepo(initialRecord: GameRecord) =
    let mutable current = initialRecord

    interface IGameRepository with
        member _.LoadGame _ = async { return Some current }

        member _.SaveGame(record, _) =
            async {
                current <- record
                return Ok ()
            }

        member _.CreateGame record =
            async {
                current <- record
                return Ok ()
            }

type GameDetailsDto = {
    GameId: string
    Fen: string
    Turn: string
    WhitePlayerId: string
    BlackPlayerId: string
}

/// Repo simulating an already-existing game: CreateGame conflicts, forcing the reset path.
type ConflictingCreateRepo(existing: GameRecord) =
    let mutable saved = None
    member _.Saved = saved

    interface IGameRepository with
        member _.LoadGame _ = async { return Some existing }

        member _.SaveGame(record, expectedVersion) =
            async {
                saved <- Some (record, expectedVersion)
                return Ok ()
            }

        member _.CreateGame _ =
            async { return Error SaveGameError.ConcurrencyConflict }

type MockBus() =
    let mutable enqueued = []
    let mutable gameUpdates = []
    member _.Enqueued = enqueued
    member _.GameUpdates = gameUpdates

    interface IMessageBus with
        member _.PublishMovePlayed(_, _) = async { () }

        member _.PublishGameUpdated gameId =
            async { gameUpdates <- gameId :: gameUpdates }

        member _.EnqueueMatchmaking(user, elo) =
            async { enqueued <- (user, elo) :: enqueued }

        member _.SubscribeMovePlayed _ =
            async {
                return
                    { new System.IDisposable with
                        member _.Dispose() = ()
                    }
            }

        member _.PublishMatchFound(_, _, _) = async { () }

        member _.SubscribeMatchFound _ =
            async {
                return
                    { new System.IDisposable with
                        member _.Dispose() = ()
                    }
            }

        member _.DequeueMatchmakingPair() = async { return [] }

type AppFactory(repo: IGameRepository, bus: IMessageBus) =
    inherit WebApplicationFactory<Program.Marker>()

    new(repo: IGameRepository) = AppFactory (repo, MockBus () :> IMessageBus)

    override this.ConfigureWebHost(builder) =
        builder.UseSolutionRelativeContentRoot ("src/backend/WebApi") |> ignore

        builder.ConfigureServices (fun services ->
            services.AddSingleton<IGameRepository> (fun _ -> repo) |> ignore
            services.AddSingleton<IMessageBus> (fun _ -> bus) |> ignore
        )
        |> ignore

[<Tests>]
let tests =
    testList "WebApi HTTP E2E Tests" [

        testTask "POST /api/games/{id}/move - 200 OK on valid move" {
            let state = Game.newGame ()

            let record = {
                GameId = "game1"
                State = state
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = MockRepo (Some record, Ok ())
            let factory = new AppFactory (repo)
            let client = factory.CreateClient ()

            client.DefaultRequestHeaders.Add ("X-User-Id", "w1")

            let json =
                """{"FromRow": 1, "FromCol": 4, "ToRow": 3, "ToCol": 4, "Promotion": ""}"""

            let content =
                new StringContent (json, System.Text.Encoding.UTF8, "application/json")

            let! (response: System.Net.Http.HttpResponseMessage) = client.PostAsync ("/api/games/game1/move", content)
            let! body = response.Content.ReadAsStringAsync ()
            Expect.equal response.StatusCode HttpStatusCode.OK (sprintf "Should return 200 OK. Body: %s" body)
        }

        testTask "POST /api/games/{id}/move - 404 Not Found if game missing" {
            let repo = MockRepo (None, Ok ())
            let factory = new AppFactory (repo)
            let client = factory.CreateClient ()
            client.DefaultRequestHeaders.Add ("X-User-Id", "w1")

            let json =
                """{"FromRow": 1, "FromCol": 4, "ToRow": 3, "ToCol": 4, "Promotion": ""}"""

            let content =
                new StringContent (json, System.Text.Encoding.UTF8, "application/json")

            let! (response: System.Net.Http.HttpResponseMessage) = client.PostAsync ("/api/games/game1/move", content)

            Expect.equal response.StatusCode HttpStatusCode.NotFound "Should return 404 Not Found"
        }

        testTask "POST /api/games/{id}/move - 400 Bad Request on invalid domain move" {
            let state = Game.newGame ()

            let record = {
                GameId = "game1"
                State = state
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = MockRepo (Some record, Ok ())
            let factory = new AppFactory (repo)
            let client = factory.CreateClient ()
            client.DefaultRequestHeaders.Add ("X-User-Id", "w1")

            // Invalid move: e2 to e5 (illegal for pawn)
            let json =
                """{"FromRow": 1, "FromCol": 4, "ToRow": 4, "ToCol": 4, "Promotion": ""}"""

            let content =
                new StringContent (json, System.Text.Encoding.UTF8, "application/json")

            let! (response: System.Net.Http.HttpResponseMessage) = client.PostAsync ("/api/games/game1/move", content)

            Expect.equal response.StatusCode HttpStatusCode.BadRequest "Should return 400 Bad Request"
        }

        testTask "POST /api/games/{id}/move - 409 Conflict on OCC failure" {
            let state = Game.newGame ()

            let record = {
                GameId = "game1"
                State = state
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = MockRepo (Some record, Error SaveGameError.ConcurrencyConflict)
            let factory = new AppFactory (repo)
            let client = factory.CreateClient ()
            client.DefaultRequestHeaders.Add ("X-User-Id", "w1")

            let json =
                """{"FromRow": 1, "FromCol": 4, "ToRow": 3, "ToCol": 4, "Promotion": ""}"""

            let content =
                new StringContent (json, System.Text.Encoding.UTF8, "application/json")

            let! (response: System.Net.Http.HttpResponseMessage) = client.PostAsync ("/api/games/game1/move", content)
            let! body = response.Content.ReadAsStringAsync ()

            Expect.equal
                response.StatusCode
                HttpStatusCode.Conflict
                (sprintf "Should return 409 Conflict. Body: %s" body)
        }

        testTask "POST /api/games/{id}/move - Multi-move game integration flow" {
            let state = Game.newGame ()

            let record = {
                GameId = "game_int_1"
                State = state
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = IntegrationRepo (record)
            let factory = new AppFactory (repo)
            let client = factory.CreateClient ()

            // 1. White player w1 plays e2-e4
            let move1Json =
                """{"FromRow": 1, "FromCol": 4, "ToRow": 3, "ToCol": 4, "Promotion": ""}"""

            let content1 =
                new StringContent (move1Json, System.Text.Encoding.UTF8, "application/json")

            client.DefaultRequestHeaders.Remove ("X-User-Id") |> ignore
            client.DefaultRequestHeaders.Add ("X-User-Id", "w1")

            let! (response1: System.Net.Http.HttpResponseMessage) =
                client.PostAsync ("/api/games/game_int_1/move", content1)

            let! body1 = response1.Content.ReadAsStringAsync ()
            Expect.equal response1.StatusCode HttpStatusCode.OK (sprintf "White move e4 should succeed. Body: %s" body1)

            // 2. Black player b1 plays e7-e5
            let move2Json =
                """{"FromRow": 6, "FromCol": 4, "ToRow": 4, "ToCol": 4, "Promotion": ""}"""

            let content2 =
                new StringContent (move2Json, System.Text.Encoding.UTF8, "application/json")

            client.DefaultRequestHeaders.Remove ("X-User-Id") |> ignore
            client.DefaultRequestHeaders.Add ("X-User-Id", "b1")

            let! (response2: System.Net.Http.HttpResponseMessage) =
                client.PostAsync ("/api/games/game_int_1/move", content2)

            let! body2 = response2.Content.ReadAsStringAsync ()
            Expect.equal response2.StatusCode HttpStatusCode.OK (sprintf "Black move e5 should succeed. Body: %s" body2)

            // 3. Verify final state via GET /api/games/{id}
            let! (response3: System.Net.Http.HttpResponseMessage) = client.GetAsync ("/api/games/game_int_1")
            Expect.equal response3.StatusCode HttpStatusCode.OK "Get game should return 200"
            let! (gameDetails: GameDetailsDto) = response3.Content.ReadFromJsonAsync<GameDetailsDto> ()

            Expect.equal gameDetails.Turn "w" "Turn should be White's after 1. e4 e5"

            Expect.stringContains
                gameDetails.Fen
                "rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR"
                "FEN should contain e4 and e5 pawn positions"
        }

        testTask "POST /api/games/{id}/move - 400 Bad Request on out-of-bounds coordinates" {
            let record = {
                GameId = "game1"
                State = Game.newGame ()
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = MockRepo (Some record, Ok ())
            let factory = new AppFactory (repo)
            let client = factory.CreateClient ()
            client.DefaultRequestHeaders.Add ("X-User-Id", "w1")

            let json =
                """{"FromRow": 1, "FromCol": 10, "ToRow": -5, "ToCol": 4, "Promotion": ""}"""

            let content =
                new StringContent (json, System.Text.Encoding.UTF8, "application/json")

            let! (response: System.Net.Http.HttpResponseMessage) = client.PostAsync ("/api/games/game1/move", content)
            let! (body: string) = response.Content.ReadAsStringAsync ()

            Expect.equal response.StatusCode HttpStatusCode.BadRequest "Should return 400 Bad Request"
            Expect.stringContains body "Coordinates" "Error should mention coordinates"
        }

        testTask "POST /api/games/{id}/move - 400 Bad Request on invalid promotion piece" {
            let record = {
                GameId = "game1"
                State = Game.newGame ()
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = MockRepo (Some record, Ok ())
            let factory = new AppFactory (repo)
            let client = factory.CreateClient ()
            client.DefaultRequestHeaders.Add ("X-User-Id", "w1")

            let json =
                """{"FromRow": 1, "FromCol": 4, "ToRow": 3, "ToCol": 4, "Promotion": "X"}"""

            let content =
                new StringContent (json, System.Text.Encoding.UTF8, "application/json")

            let! (response: System.Net.Http.HttpResponseMessage) = client.PostAsync ("/api/games/game1/move", content)
            let! (body: string) = response.Content.ReadAsStringAsync ()

            Expect.equal response.StatusCode HttpStatusCode.BadRequest "Should return 400 Bad Request"
            Expect.stringContains body "promotion" "Error should mention the promotion piece"
        }

        testTask "POST /api/games/{id}/move - 400 when X-User-Id header is missing" {
            let record = {
                GameId = "game1"
                State = Game.newGame ()
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = MockRepo (Some record, Ok ())
            let factory = new AppFactory (repo)
            let client = factory.CreateClient ()
            // No X-User-Id header: the caller resolves to "anonymous", which is never the active player

            let json =
                """{"FromRow": 1, "FromCol": 4, "ToRow": 3, "ToCol": 4, "Promotion": ""}"""

            let content =
                new StringContent (json, System.Text.Encoding.UTF8, "application/json")

            let! (response: System.Net.Http.HttpResponseMessage) = client.PostAsync ("/api/games/game1/move", content)

            Expect.equal response.StatusCode HttpStatusCode.BadRequest "Should return 400 (not your turn)"
        }

        testTask "POST /api/games/{id}/create - 201 Created and publishes GameUpdated" {
            let repo = MockRepo (None, Ok ())
            let (bus: MockBus) = MockBus ()
            let factory = new AppFactory (repo, bus)
            let client = factory.CreateClient ()

            let json = """{"WhitePlayerId": "w1", "BlackPlayerId": "computer"}"""

            let content =
                new StringContent (json, System.Text.Encoding.UTF8, "application/json")

            let! (response: System.Net.Http.HttpResponseMessage) =
                client.PostAsync ("/api/games/game_new/create", content)

            let! (body: string) = response.Content.ReadAsStringAsync ()

            Expect.equal response.StatusCode HttpStatusCode.Created (sprintf "Should return 201 Created. Body: %s" body)
            Expect.equal bus.GameUpdates [ "game_new" ] "Should publish GameUpdated for the new game"
        }

        testTask "POST /api/games/{id}/create - 200 resets an existing game" {
            let existingState = Game.newGame () |> fun s -> { s with Version = 5 }

            let existing = {
                GameId = "game_existing"
                State = existingState
                WhitePlayerId = "old_w"
                BlackPlayerId = "old_b"
            }

            let repo = ConflictingCreateRepo (existing)
            let (bus: MockBus) = MockBus ()
            let factory = new AppFactory (repo, bus)
            let client = factory.CreateClient ()

            let json = """{"WhitePlayerId": "new_w", "BlackPlayerId": "computer"}"""

            let content =
                new StringContent (json, System.Text.Encoding.UTF8, "application/json")

            let! (response: System.Net.Http.HttpResponseMessage) =
                client.PostAsync ("/api/games/game_existing/create", content)

            let! (body: string) = response.Content.ReadAsStringAsync ()

            Expect.equal response.StatusCode HttpStatusCode.OK (sprintf "Should return 200 OK. Body: %s" body)
            Expect.stringContains body "reset" "Response should say the game was reset"
            Expect.equal bus.GameUpdates [ "game_existing" ] "Should publish GameUpdated for the reset game"

            match repo.Saved with
            | Some (record, expectedVersion) ->
                Expect.equal record.WhitePlayerId "new_w" "Reset should apply the new white player"
                Expect.equal expectedVersion 5 "Reset should use the existing version for OCC"
            | None -> failtest "Reset should have saved the game"
        }

        testTask "GET /api/games/{id} - 404 when the game does not exist" {
            let repo = MockRepo (None, Ok ())
            let factory = new AppFactory (repo)
            let client = factory.CreateClient ()

            let! (response: System.Net.Http.HttpResponseMessage) = client.GetAsync ("/api/games/missing_game")

            Expect.equal response.StatusCode HttpStatusCode.NotFound "Should return 404 Not Found"
        }

        testTask "POST /api/matchmaking - 202 Accepted and queues player" {
            let repo = MockRepo (None, Ok ())
            let (bus: MockBus) = MockBus ()
            let factory = new AppFactory (repo, bus)
            let client = factory.CreateClient ()

            client.DefaultRequestHeaders.Add ("X-User-Id", "player123")

            let content =
                new StringContent ("{\"Elo\": 1600}", System.Text.Encoding.UTF8, "application/json")

            let! (response: System.Net.Http.HttpResponseMessage) = client.PostAsync ("/api/matchmaking", content)
            let! body = response.Content.ReadAsStringAsync ()

            Expect.equal
                response.StatusCode
                HttpStatusCode.Accepted
                (sprintf "Should return 202 Accepted. Body: %s" body)

            Expect.equal bus.Enqueued [ ("player123", 1600) ] "Should have enqueued player123 with ELO 1600"
        }
    ]

/// Bus mock that captures subscription handlers so tests can invoke them deterministically.
type SubscribingBus() =
    let mutable moveHandler: (string -> Async<unit>) option = None
    let mutable matchHandler: (string * string * string -> Async<unit>) option = None
    member _.MoveHandler = moveHandler
    member _.MatchHandler = matchHandler

    interface IMessageBus with
        member _.PublishMovePlayed(_, _) = async { () }
        member _.PublishGameUpdated _ = async { () }
        member _.EnqueueMatchmaking(_, _) = async { () }

        member _.SubscribeMovePlayed h =
            async {
                moveHandler <- Some h

                return
                    { new System.IDisposable with
                        member _.Dispose() = ()
                    }
            }

        member _.PublishMatchFound(_, _, _) = async { () }

        member _.SubscribeMatchFound h =
            async {
                matchHandler <- Some h

                return
                    { new System.IDisposable with
                        member _.Dispose() = ()
                    }
            }

        member _.DequeueMatchmakingPair() = async { return [] }

/// Hub context mock recording (target, method) pairs for every SignalR send.
type RecordingHubContext() =
    let mutable sent: (string * string) list = []

    let proxy target =
        { new IClientProxy with
            member _.SendCoreAsync(method, _args, _ct) =
                sent <- (target, method) :: sent
                Task.CompletedTask
        }

    member _.Sent = List.rev sent

    interface IHubContext<GameHub> with
        member _.Clients =
            { new IHubClients with
                member _.All = proxy "all"
                member _.AllExcept _ = proxy "all-except"
                member _.Client(id: string) : IClientProxy = proxy ("client:" + id)
                member _.Clients _ = proxy "clients"
                member _.Group name = proxy ("group:" + name)
                member _.GroupExcept(name, _) = proxy ("group-except:" + name)
                member _.Groups _ = proxy "groups"
                member _.User id = proxy ("user:" + id)
                member _.Users _ = proxy "users"
            }

        member _.Groups =
            { new IGroupManager with
                member _.AddToGroupAsync(_, _, _) = Task.CompletedTask
                member _.RemoveFromGroupAsync(_, _, _) = Task.CompletedTask
            }

let waitFor (predicate: unit -> bool) =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < 200 do
            do! Task.Delay 10
            tries <- tries + 1
    }

[<Tests>]
let broadcasterTests =
    testList "Broadcaster Tests" [
        testTask "SignalRMoveBroadcaster broadcasts MovePlayed to the game group" {
            let record = {
                GameId = "g1"
                State = Game.newGame ()
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = MockRepo (Some record, Ok ())
            let bus = SubscribingBus ()
            let hub = RecordingHubContext ()

            let broadcaster =
                new SignalRMoveBroadcaster (NullLogger<SignalRMoveBroadcaster>.Instance, repo, bus, hub)

            do! broadcaster.StartAsync CancellationToken.None
            do! waitFor (fun () -> Option.isSome bus.MoveHandler)

            let handler = Option.get bus.MoveHandler
            do! Async.StartAsTask (handler "g1")

            Expect.equal hub.Sent [ ("group:g1", "MovePlayed") ] "Should broadcast MovePlayed to the game group"

            do! broadcaster.StopAsync CancellationToken.None
        }

        testTask "SignalRMoveBroadcaster sends nothing when the game is missing" {
            let repo = MockRepo (None, Ok ())
            let bus = SubscribingBus ()
            let hub = RecordingHubContext ()

            let broadcaster =
                new SignalRMoveBroadcaster (NullLogger<SignalRMoveBroadcaster>.Instance, repo, bus, hub)

            do! broadcaster.StartAsync CancellationToken.None
            do! waitFor (fun () -> Option.isSome bus.MoveHandler)

            let handler = Option.get bus.MoveHandler
            do! Async.StartAsTask (handler "ghost-game")

            Expect.equal hub.Sent [] "Should not broadcast anything for an unknown game"

            do! broadcaster.StopAsync CancellationToken.None
        }

        testTask "MatchmakingBroadcaster notifies both players' lobby groups" {
            let bus = SubscribingBus ()
            let hub = RecordingHubContext ()

            let broadcaster =
                new MatchmakingBroadcaster (NullLogger<MatchmakingBroadcaster>.Instance, bus, hub)

            do! broadcaster.StartAsync CancellationToken.None
            do! waitFor (fun () -> Option.isSome bus.MatchHandler)

            let handler = Option.get bus.MatchHandler
            do! Async.StartAsTask (handler ("g1", "w1", "b1"))

            Expect.equal
                hub.Sent
                [ ("group:user_w1", "MatchFound"); ("group:user_b1", "MatchFound") ]
                "Should notify white then black on their lobby groups"

            do! broadcaster.StopAsync CancellationToken.None
        }
    ]

[<EntryPoint>]
let main args =
    runTestsWithCLIArgs [] args (testList "WebApi Tests" [ tests; broadcasterTests ])
