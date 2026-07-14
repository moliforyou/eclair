namespace ChessPlatform.WebApi

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.AspNetCore.SignalR
open ChessPlatform.Application
open System.Threading

type MatchmakingBroadcaster(logger: ILogger<MatchmakingBroadcaster>, bus: IMessageBus, hubContext: IHubContext<GameHub>)
    =
    inherit BackgroundService()

    let handleMatchFound (gameId: string, white: string, black: string) =
        async {
            try
                logger.LogInformation (
                    "MatchmakingBroadcaster: Match found for game {GameId} with White={White} Black={Black}",
                    gameId,
                    white,
                    black
                )

                do!
                    hubContext.Clients
                        .Group("user_" + white)
                        .SendAsync (
                            "MatchFound",
                            {|
                                GameId = gameId
                                Role = "white"
                                WhitePlayerId = white
                                BlackPlayerId = black
                            |}
                        )
                    |> Async.AwaitTask

                do!
                    hubContext.Clients
                        .Group("user_" + black)
                        .SendAsync (
                            "MatchFound",
                            {|
                                GameId = gameId
                                Role = "black"
                                WhitePlayerId = white
                                BlackPlayerId = black
                            |}
                        )
                    |> Async.AwaitTask
            with ex ->
                logger.LogError (ex, "MatchmakingBroadcaster error handling match event")
        }

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        let tcs = new System.Threading.Tasks.TaskCompletionSource<unit> ()
        stoppingToken.Register (fun () -> tcs.SetResult ()) |> ignore

        let startSubscription () =
            async {
                logger.LogInformation ("MatchmakingBroadcaster starting subscription...")
                let! disposable = bus.SubscribeMatchFound (handleMatchFound)
                let! _ = Async.AwaitTask tcs.Task
                disposable.Dispose ()
                logger.LogInformation ("MatchmakingBroadcaster stopped.")
            }

        Async.StartAsTask (startSubscription (), cancellationToken = stoppingToken) :> System.Threading.Tasks.Task
