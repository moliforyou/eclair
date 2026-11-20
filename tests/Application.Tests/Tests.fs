module Tests

open Expecto
open ChessPlatform.Domain
open ChessPlatform.Application

type MockRepo(initialState: GameRecord option, saveResult: Result<unit, SaveGameError>) =
    interface IGameRepository with
        member _.LoadGame _ = async { return initialState }
        member _.SaveGame(_, _) = async { return saveResult }
        member _.CreateGame _ = async { return Ok () }

type MockBus() =
    let mutable publishedMoves = []
    member _.PublishedMoves = publishedMoves

    interface IMessageBus with
        member _.PublishMovePlayed(_, move) =
            async { publishedMoves <- move :: publishedMoves }

        member _.PublishGameUpdated _ = async { () }

        member _.EnqueueMatchmaking(_, _) = async { () }

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

        member _.RemoveFromMatchmakingQueue _ = async { () }


[<Tests>]
let tests =
    testList "PlayMoveUseCase Tests" [
        testAsync "Returns GameNotFound when game does not exist" {
            let repo = MockRepo (None, Ok ())
            let bus = MockBus ()

            let req: MoveRequest = {
                From = (0, 0)
                To = (0, 1)
                Promotion = None
            }

            let! result = PlayMoveUseCase.execute repo bus "game1" "user1" req
            Expect.equal result (Error PlayMoveUseCase.GameNotFound) "Should be GameNotFound"
        }

        testAsync "Returns NotYourTurn when user is not the active player" {
            let state = Game.newGame ()

            let record = {
                GameId = "game1"
                State = state
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = MockRepo (Some record, Ok ())
            let bus = MockBus ()

            let req: MoveRequest = {
                From = (4, 1)
                To = (4, 3)
                Promotion = None
            }

            let! result = PlayMoveUseCase.execute repo bus "game1" "b1" req
            Expect.equal result (Error PlayMoveUseCase.NotYourTurn) "Should be NotYourTurn"
        }

        testAsync "Returns DomainError on invalid move" {
            let state = Game.newGame ()

            let record = {
                GameId = "game1"
                State = state
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = MockRepo (Some record, Ok ())
            let bus = MockBus ()

            let req: MoveRequest = {
                From = (4, 1)
                To = (4, 5)
                Promotion = None
            }

            let! result = PlayMoveUseCase.execute repo bus "game1" "w1" req

            match result with
            | Error (PlayMoveUseCase.DomainError _) -> ()
            | _ -> failwith "Expected DomainError"
        }

        testAsync "Returns ConcurrencyConflict on OCC failure" {
            let state = Game.newGame ()

            let record = {
                GameId = "game1"
                State = state
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = MockRepo (Some record, Error SaveGameError.ConcurrencyConflict)
            let bus = MockBus ()

            let req: MoveRequest = {
                From = (4, 1)
                To = (4, 3)
                Promotion = None
            }

            let! result = PlayMoveUseCase.execute repo bus "game1" "w1" req
            Expect.equal result (Error PlayMoveUseCase.ConcurrencyConflict) "Should be ConcurrencyConflict"
            Expect.isEmpty bus.PublishedMoves "Should not publish move on conflict"
        }

        testAsync "Succeeds and publishes event on valid move" {
            let state = Game.newGame ()

            let record = {
                GameId = "game1"
                State = state
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo = MockRepo (Some record, Ok ())
            let bus = MockBus ()

            let req: MoveRequest = {
                From = (4, 1)
                To = (4, 3)
                Promotion = None
            }

            let! result = PlayMoveUseCase.execute repo bus "game1" "w1" req

            match result with
            | Ok _ -> Expect.equal bus.PublishedMoves.Length 1 "Should publish exactly one event"
            | Error e -> failwithf "Expected success but got %A" e
        }

        testAsync "Returns PersistenceError on database failure" {
            let state = Game.newGame ()

            let record = {
                GameId = "game1"
                State = state
                WhitePlayerId = "w1"
                BlackPlayerId = "b1"
            }

            let repo =
                MockRepo (Some record, Error (SaveGameError.PersistenceFailure "DB Down"))

            let bus = MockBus ()

            let req: MoveRequest = {
                From = (4, 1)
                To = (4, 3)
                Promotion = None
            }

            let! result = PlayMoveUseCase.execute repo bus "game1" "w1" req
            Expect.equal result (Error (PlayMoveUseCase.PersistenceError "DB Down")) "Should be PersistenceError"
            Expect.isEmpty bus.PublishedMoves "Should not publish move on persistence failure"
        }
    ]

let matchmakingTests =
    testList "MatchmakingUseCase Tests" [
        testAsync "Enqueues player successfully" {
            let mutable enqueuedUser = None
            let mutable enqueuedElo = 0

            let bus =
                { new IMessageBus with
                    member _.PublishMovePlayed(_, _) = async { () }
                    member _.PublishGameUpdated _ = async { () }

                    member _.EnqueueMatchmaking(user, elo) =
                        async {
                            enqueuedUser <- Some user
                            enqueuedElo <- elo
                        }

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
                    member _.RemoveFromMatchmakingQueue _ = async { () }
                }


            do! MatchmakingUseCase.enqueue bus "user1" 1500
            Expect.equal enqueuedUser (Some "user1") "Should enqueue user1"
            Expect.equal enqueuedElo 1500 "Should enqueue with ELO 1500"
        }

        testAsync "Dequeues player successfully" {
            let mutable dequeuedUser = None

            let bus =
                { new IMessageBus with
                    member _.PublishMovePlayed(_, _) = async { () }
                    member _.PublishGameUpdated _ = async { () }

                    member _.EnqueueMatchmaking(_, _) = async { () }

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

                    member _.RemoveFromMatchmakingQueue user = async { dequeuedUser <- Some user }
                }

            do! MatchmakingUseCase.dequeue bus "user1"
            Expect.equal dequeuedUser (Some "user1") "Should dequeue user1"
        }
    ]


[<Tests>]
let allTests = testList "ChessPlatform.Application" [ tests; matchmakingTests ]

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args allTests
