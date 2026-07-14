namespace ChessPlatform.BackgroundWorkers

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System.Threading
open ChessPlatform.Application
open ChessPlatform.Domain
open Engine

type ComputerPlayerWorker
    (logger: ILogger<ComputerPlayerWorker>, repo: IGameRepository, bus: IMessageBus, engine: IComputerEngine)
    =

    inherit BackgroundService()

    let computerUserId = "computer"

    // Guards against overlapping engine runs for the same game: events can arrive
    // faster than a move computation completes, and handlers run concurrently.
    let inFlightGames =
        System.Collections.Concurrent.ConcurrentDictionary<string, byte> ()

    let handleMovePlayed (gameId: string) =
        async {
            if not (inFlightGames.TryAdd (gameId, 0uy)) then
                logger.LogInformation ("Move computation already in flight for game {GameId}. Skipping event.", gameId)
            else
                try
                    try
                        logger.LogInformation ("Received move event for game {GameId}", gameId)

                        let! gameOpt = repo.LoadGame gameId

                        match gameOpt with
                        | None -> ()
                        | Some record ->
                            match record.State.Status with
                            | Checkmate _
                            | Stalemate
                            | Draw _ ->
                                logger.LogInformation (
                                    "Game {GameId} is already over (Status: {Status}). Doing nothing.",
                                    gameId,
                                    record.State.Status
                                )
                            | InProgress
                            | Check _ ->
                                let expectedUserId =
                                    match record.State.ActiveColor with
                                    | White -> record.WhitePlayerId
                                    | Black -> record.BlackPlayerId

                                if expectedUserId = computerUserId then
                                    logger.LogInformation ("Computer's turn in game {GameId}. Thinking...", gameId)
                                    let! bestMoveOpt = engine.GetBestMove record.State

                                    match bestMoveOpt with
                                    | None ->
                                        logger.LogWarning (
                                            "Computer has no legal moves in game {GameId} despite status being InProgress/Check",
                                            gameId
                                        )
                                    | Some moveReq ->
                                        do! Async.Sleep 500
                                        let! result = PlayMoveUseCase.execute repo bus gameId computerUserId moveReq

                                        match result with
                                        | Ok _ ->
                                            logger.LogInformation ("Computer move successful for game {GameId}", gameId)
                                        | Error e ->
                                            logger.LogError (
                                                "Computer move failed in game {GameId}: {Error}",
                                                gameId,
                                                e
                                            )
                    with ex ->
                        logger.LogError (ex, "Error handling move event for game {GameId}", gameId)
                finally
                    inFlightGames.TryRemove gameId |> ignore
        }

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        let tcs = new System.Threading.Tasks.TaskCompletionSource<unit> ()
        stoppingToken.Register (fun () -> tcs.SetResult ()) |> ignore

        let startSubscription () =
            async {
                logger.LogInformation ("ComputerPlayerWorker starting subscription...")
                let! disposable = bus.SubscribeMovePlayed (handleMovePlayed)

                let! _ = Async.AwaitTask tcs.Task

                disposable.Dispose ()
                logger.LogInformation ("ComputerPlayerWorker stopped.")
            }

        Async.StartAsTask (startSubscription (), cancellationToken = stoppingToken) :> System.Threading.Tasks.Task
