namespace ChessPlatform.WebApi

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.AspNetCore.SignalR
open ChessPlatform.Application
open ChessPlatform.Domain
open System.Threading

type SignalRMoveBroadcaster(logger: ILogger<SignalRMoveBroadcaster>, repo: IGameRepository, bus: IMessageBus, hubContext: IHubContext<GameHub>) =
    inherit BackgroundService()

    let handleMovePlayed (gameId: string) =
        async {
            try
                logger.LogInformation ("SignalRMoveBroadcaster: Received move event for game {GameId}", gameId)
                let! gameOpt = repo.LoadGame gameId
                match gameOpt with
                | None -> logger.LogWarning("SignalRMoveBroadcaster: Game {GameId} not found in database", gameId)
                | Some record ->
                    let fen = FenParser.toFen record.State
                    let turnChar = 
                        match record.State.ActiveColor with
                        | White -> "w"
                        | Black -> "b"
                    let moves = 
                        record.State.History
                        |> List.rev
                        |> List.map MoveExtensions.toDto
                        |> List.toArray
                    
                    logger.LogInformation ("SignalRMoveBroadcaster: Broadcasting move for game {GameId}. New FEN: {Fen}", gameId, fen)
                    
                    // Broadcast to SignalR group gameId
                    let status =
                        match record.State.Status with
                        | ChessPlatform.Domain.InProgress -> "InProgress"
                        | ChessPlatform.Domain.Check _ -> "Check"
                        | ChessPlatform.Domain.Checkmate w -> sprintf "Checkmate:%A" w
                        | ChessPlatform.Domain.Stalemate -> "Stalemate"
                        | ChessPlatform.Domain.Draw r -> sprintf "Draw:%s" r
                    do! hubContext.Clients.Group(gameId).SendAsync("MovePlayed", {| Fen = fen; Turn = turnChar; WhitePlayerId = record.WhitePlayerId; BlackPlayerId = record.BlackPlayerId; Moves = moves; Status = status |}) |> Async.AwaitTask
            with ex ->
                logger.LogError (ex, "SignalRMoveBroadcaster: Error handling move event for game {GameId}", gameId)
        }

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        let tcs = new System.Threading.Tasks.TaskCompletionSource<unit> ()
        stoppingToken.Register (fun () -> tcs.SetResult ()) |> ignore

        let startSubscription () =
            async {
                logger.LogInformation ("SignalRMoveBroadcaster starting subscription to message bus events...")
                let! disposable = bus.SubscribeMovePlayed (handleMovePlayed)
                let! _ = Async.AwaitTask tcs.Task
                disposable.Dispose ()
                logger.LogInformation ("SignalRMoveBroadcaster stopped.")
            }

        Async.StartAsTask (startSubscription (), cancellationToken = stoppingToken) :> System.Threading.Tasks.Task
