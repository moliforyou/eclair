module Tests

#nowarn "3261"
#nowarn "0760"

open System
open System.Net
open System.Net.Http
open System.Net.Http.Json
open Expecto
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

type MockBus() =
    let mutable enqueued = []
    member _.Enqueued = enqueued

    interface IMessageBus with
        member _.PublishMovePlayed(_, _) = async { () }

        member _.PublishGameUpdated _ = async { () }

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

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args tests
