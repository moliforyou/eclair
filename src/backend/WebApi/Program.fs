namespace ChessPlatform.WebApi

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Falco
open Falco.Routing
open ChessPlatform.Application
open ChessPlatform.Infrastructure

module Program =

    type Marker = class end

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder (args)

        builder.Services.AddSignalR () |> ignore

        let pgConn =
            builder.Configuration.["ConnectionStrings:Postgres"]
            |> Option.ofObj
            |> Option.defaultValue ""

        let redisConn =
            builder.Configuration.["ConnectionStrings:Redis"]
            |> Option.ofObj
            |> Option.defaultValue ""

        builder.Services.AddSingleton<IGameRepository> (fun _ -> PostgresRepo (pgConn) :> IGameRepository)
        |> ignore

        builder.Services.AddSingleton<IMessageBus> (fun _ -> RedisBus (redisConn) :> IMessageBus)
        |> ignore

        builder.Services.AddHostedService<SignalRMoveBroadcaster> () |> ignore
        builder.Services.AddHostedService<MatchmakingBroadcaster> () |> ignore

        let app = builder.Build ()

        app.UseRouting () |> ignore

        let repo = app.Services.GetRequiredService<IGameRepository> ()
        let bus = app.Services.GetRequiredService<IMessageBus> ()

        let falcoEndpoints = [
            post "/api/matchmaking" (Handlers.matchmakingHandler bus)
            delete "/api/matchmaking" (Handlers.cancelMatchmakingHandler bus)
            post "/api/games/{id}/move" (Handlers.playMoveHandler repo bus)
            post "/api/games/{id}/create" (Handlers.createGameHandler repo bus)
            get "/api/games/{id}" (Handlers.getGameHandler repo)
        ]


        app.UseFalco (falcoEndpoints) |> ignore

        app.UseEndpoints (fun endpoints -> endpoints.MapHub<GameHub> ("/hubs/game") |> ignore)
        |> ignore

        app.Run ()
        0
