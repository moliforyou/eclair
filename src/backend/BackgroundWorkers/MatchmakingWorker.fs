namespace ChessPlatform.BackgroundWorkers

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System
open System.Threading
open ChessPlatform.Application
open ChessPlatform.Domain

type MatchmakingWorker(logger: ILogger<MatchmakingWorker>, repo: IGameRepository, bus: IMessageBus) =

    inherit BackgroundService()

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        let runLoop () =
            async {
                logger.LogInformation "MatchmakingWorker starting loop..."
                let rand = Random ()

                while not stoppingToken.IsCancellationRequested do
                    try
                        let! pair = bus.DequeueMatchmakingPair ()

                        match pair with
                        | [ (p1, elo1); (p2, elo2) ] when p1 <> p2 ->
                            logger.LogInformation ("MatchmakingWorker: Matched {Player1} and {Player2}", p1, p2)

                            let white, black = if rand.Next (2) = 0 then (p1, p2) else (p2, p1)

                            let gameId = sprintf "game_%s" (Guid.NewGuid().ToString ("N"))

                            let record = {
                                GameId = gameId
                                State = Game.newGame ()
                                WhitePlayerId = white
                                BlackPlayerId = black
                            }

                            let! createResult = repo.CreateGame record

                            match createResult with
                            | Ok _ ->
                                logger.LogInformation (
                                    "MatchmakingWorker: Created game {GameId} with White={White} Black={Black}",
                                    gameId,
                                    white,
                                    black
                                )

                                do! bus.PublishMatchFound (gameId, white, black)
                            | Error e ->
                                logger.LogError (
                                    "MatchmakingWorker: Failed to create game {GameId}: {Error}. Requeueing both players.",
                                    gameId,
                                    e
                                )

                                do! bus.EnqueueMatchmaking (p1, elo1)
                                do! bus.EnqueueMatchmaking (p2, elo2)
                        | [ (p, elo) ] ->
                            // Dequeue pops up to two entries; a lone player must be put back to wait for an opponent
                            do! bus.EnqueueMatchmaking (p, elo)
                            do! Async.Sleep 2000
                        | [ (p1, elo1); (p2, _) ] when p1 = p2 ->
                            // Same player popped twice; requeue one entry so they keep waiting
                            do! bus.EnqueueMatchmaking (p1, elo1)
                            do! Async.Sleep 2000
                        | _ -> do! Async.Sleep 2000
                    with ex ->
                        logger.LogError (ex, "MatchmakingWorker error in loop")
                        do! Async.Sleep 5000
            }

        Async.StartAsTask (runLoop (), cancellationToken = stoppingToken) :> System.Threading.Tasks.Task
