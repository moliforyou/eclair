module Tests

open Expecto
open ChessPlatform.Domain
open ChessPlatform.Application
open ChessPlatform.BackgroundWorkers
open ChessPlatform.BackgroundWorkers.Engine
open Microsoft.Extensions.Logging.Abstractions

let unwrap =
    function
    | Ok x -> x
    | Error e -> failwithf "Expected Ok, got Error: %A" e

type MockMessageBus() =
    let mutable subscribeHandler = None
    let mutable publishedMoves = []
    let mutable dequeuedPair = []
    let mutable publishedMatch = None
    let mutable enqueuedEntries = []

    member _.SubscribeHandler = subscribeHandler
    member _.PublishedMoves = publishedMoves
    member _.SetDequeuedPair(pair) = dequeuedPair <- pair
    member _.PublishedMatch = publishedMatch
    member _.EnqueuedEntries = enqueuedEntries

    interface IMessageBus with
        member _.PublishMovePlayed(gameId, move) =
            async { publishedMoves <- (gameId, move) :: publishedMoves }

        member _.PublishGameUpdated _ = async { () }

        member _.EnqueueMatchmaking(p, elo) =
            async { enqueuedEntries <- (p, elo) :: enqueuedEntries }

        member _.SubscribeMovePlayed(handler) =
            async {
                subscribeHandler <- Some handler

                return
                    { new System.IDisposable with
                        member _.Dispose() = ()
                    }
            }

        member _.PublishMatchFound(gameId, white, black) =
            async { publishedMatch <- Some (gameId, white, black) }

        member _.SubscribeMatchFound(handler) =
            async {
                return
                    { new System.IDisposable with
                        member _.Dispose() = ()
                    }
            }

        member _.DequeueMatchmakingPair() = async { return dequeuedPair }

        member _.RemoveFromMatchmakingQueue _ = async { () }


let waitForSubscription (bus: MockMessageBus) =
    async {
        let mutable handler = None

        while Option.isNone handler do
            do! Async.Sleep 10
            handler <- bus.SubscribeHandler

        return Option.get handler
    }

type MockGameRepository(initialGames: Map<string, GameRecord>) =
    let mutable games = initialGames
    let mutable savedRecords = []

    member _.Games = games
    member _.SavedRecords = savedRecords

    interface IGameRepository with
        member _.LoadGame(gameId) =
            async { return Map.tryFind gameId games }

        member _.SaveGame(record, expectedVersion) =
            async {
                let currentRecord = games.[record.GameId]

                if currentRecord.State.Version <> expectedVersion then
                    return Error SaveGameError.ConcurrencyConflict
                else
                    games <- Map.add record.GameId record games
                    savedRecords <- record :: savedRecords
                    return Ok ()
            }

        member _.CreateGame(record) =
            async {
                games <- Map.add record.GameId record games
                return Ok ()
            }

type MockComputerEngine(bestMoveToReturn: MoveRequest option) =
    let mutable calledWithStates = []

    member _.CalledWithStates = calledWithStates

    interface IComputerEngine with
        member _.GetBestMove(state) =
            async {
                calledWithStates <- state :: calledWithStates
                return bestMoveToReturn
            }

[<Tests>]
let tests =
    testList "ComputerPlayerWorker Tests" [
        testAsync "Does nothing when it is not the computer's turn" {
            let state = Game.newGame ()

            let record = {
                GameId = "game1"
                State = state
                WhitePlayerId = "human1"
                BlackPlayerId = "computer"
            }

            let repo = MockGameRepository (Map [ ("game1", record) ])
            let bus = MockMessageBus ()
            let engine = MockComputerEngine (None)
            let logger = NullLogger<ComputerPlayerWorker>.Instance

            let worker = new ComputerPlayerWorker (logger, repo, bus, engine)
            let cancellationTokenSource = new System.Threading.CancellationTokenSource ()

            let! _ = Async.AwaitTask (worker.StartAsync (cancellationTokenSource.Token))
            let! handler = waitForSubscription bus

            let! _ = handler "game1"

            let! _ = Async.AwaitTask (worker.StopAsync (cancellationTokenSource.Token))

            Expect.isEmpty engine.CalledWithStates "Should not consult the engine on player's turn"
            Expect.isEmpty repo.SavedRecords "Should not write anything to DB"
            Expect.isEmpty bus.PublishedMoves "Should not publish any moves"
        }

        testAsync "Plays a move automatically when it is the computer's turn" {
            let state = Game.newGame ()

            let whiteMove: MoveRequest = {
                From = (4, 1)
                To = (4, 3)
                Promotion = None
            }

            let stateAfterWhite = Game.playMove state whiteMove |> unwrap

            let record = {
                GameId = "game1"
                State = stateAfterWhite
                WhitePlayerId = "human1"
                BlackPlayerId = "computer"
            }

            let repo = MockGameRepository (Map [ ("game1", record) ])
            let bus = MockMessageBus ()

            let computerMove: MoveRequest option =
                Some {
                    From = (4, 6)
                    To = (4, 4)
                    Promotion = None
                }

            let engine = MockComputerEngine (computerMove)
            let logger = NullLogger<ComputerPlayerWorker>.Instance

            let worker = new ComputerPlayerWorker (logger, repo, bus, engine)
            let cancellationTokenSource = new System.Threading.CancellationTokenSource ()

            let! _ = Async.AwaitTask (worker.StartAsync (cancellationTokenSource.Token))
            let! handler = waitForSubscription bus

            let! _ = handler "game1"

            let! _ = Async.AwaitTask (worker.StopAsync (cancellationTokenSource.Token))

            Expect.equal engine.CalledWithStates.Length 1 "Engine should be consulted exactly once"

            Expect.equal
                engine.CalledWithStates.[0]
                stateAfterWhite
                "Consulted state should match state after White move"

            Expect.equal repo.SavedRecords.Length 1 "Should save computer's move to DB"
            let savedState = repo.SavedRecords.[0].State
            Expect.equal savedState.ActiveColor White "It should be White's turn again"

            Expect.equal bus.PublishedMoves.Length 1 "Should publish the computer's move"
            let publishedGameId, publishedMove = bus.PublishedMoves.[0]
            Expect.equal publishedGameId "game1" "GameId should match"

            let fromPos, toPos = Game.moveFromTo publishedMove
            Expect.equal fromPos (4, 6) "Computer should play e7 (4, 6)"
            Expect.equal toPos (4, 4) "Computer should play e5 (4, 4)"
        }

        testAsync "Handles case where computer has no legal moves (Checkmate or Stalemate)" {
            let state = Game.newGame ()

            let whiteMove: MoveRequest = {
                From = (4, 1)
                To = (4, 3)
                Promotion = None
            }

            let stateAfterWhite = Game.playMove state whiteMove |> unwrap

            let record = {
                GameId = "game1"
                State = stateAfterWhite
                WhitePlayerId = "human1"
                BlackPlayerId = "computer"
            }

            let repo = MockGameRepository (Map [ ("game1", record) ])
            let bus = MockMessageBus ()
            let engine = MockComputerEngine (None)
            let logger = NullLogger<ComputerPlayerWorker>.Instance

            let worker = new ComputerPlayerWorker (logger, repo, bus, engine)
            let cancellationTokenSource = new System.Threading.CancellationTokenSource ()

            let! _ = Async.AwaitTask (worker.StartAsync (cancellationTokenSource.Token))
            let! handler = waitForSubscription bus
            let! _ = handler "game1"
            let! _ = Async.AwaitTask (worker.StopAsync (cancellationTokenSource.Token))

            Expect.equal engine.CalledWithStates.Length 1 "Engine consulted once"
            Expect.isEmpty repo.SavedRecords "Should not save any move"
            Expect.isEmpty bus.PublishedMoves "Should not publish any moves"
        }

        testAsync "MatchmakingWorker pairs players and creates a game" {
            let bus = MockMessageBus ()
            bus.SetDequeuedPair [ ("playerA", 1500); ("playerB", 1520) ]

            let repo = MockGameRepository (Map.empty)
            let logger = NullLogger<MatchmakingWorker>.Instance

            let worker = new MatchmakingWorker (logger, repo, bus)
            let cancellationTokenSource = new System.Threading.CancellationTokenSource ()

            let! _ = Async.AwaitTask (worker.StartAsync (cancellationTokenSource.Token))

            let mutable matchFound = None
            let mutable retries = 0

            while matchFound.IsNone && retries < 50 do
                do! Async.Sleep 50
                matchFound <- bus.PublishedMatch
                retries <- retries + 1

            let! _ = Async.AwaitTask (worker.StopAsync (cancellationTokenSource.Token))

            Expect.isTrue matchFound.IsSome "Match found should be published"
            let gameId, white, black = matchFound.Value
            Expect.isTrue (gameId.StartsWith ("game_")) "gameId should start with game_"
            Expect.isTrue (white = "playerA" || white = "playerB") "White player should be one of the matched players"
            Expect.isTrue (black = "playerA" || black = "playerB") "Black player should be one of the matched players"
            Expect.notEqual white black "White and Black should not be the same player"

            let createdGame = Map.tryFind gameId repo.Games
            Expect.isTrue createdGame.IsSome "Created game should be in the repository"
            let record = createdGame.Value
            Expect.equal record.WhitePlayerId white "White player ID must match record"
            Expect.equal record.BlackPlayerId black "Black player ID must match record"
        }

        testAsync "MatchmakingWorker requeues when the same player is popped twice" {
            let bus = MockMessageBus ()
            bus.SetDequeuedPair [ ("p1", 1200); ("p1", 1200) ]

            let repo = MockGameRepository (Map.empty)
            let logger = NullLogger<MatchmakingWorker>.Instance

            let worker = new MatchmakingWorker (logger, repo, bus)
            let cancellationTokenSource = new System.Threading.CancellationTokenSource ()

            let! _ = Async.AwaitTask (worker.StartAsync (cancellationTokenSource.Token))

            let mutable retries = 0

            while List.isEmpty bus.EnqueuedEntries && retries < 50 do
                do! Async.Sleep 50
                retries <- retries + 1

            let! _ = Async.AwaitTask (worker.StopAsync (cancellationTokenSource.Token))

            Expect.isNonEmpty bus.EnqueuedEntries "Duplicate player should be requeued, not dropped"
            Expect.all bus.EnqueuedEntries (fun e -> e = ("p1", 1200)) "Requeued entry should be the popped player"
            Expect.isTrue bus.PublishedMatch.IsNone "No match should be published for a duplicate pair"
            Expect.isTrue (Map.isEmpty repo.Games) "No game should be created for a duplicate pair"
        }
    ]

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args tests
