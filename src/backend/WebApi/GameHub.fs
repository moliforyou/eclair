namespace ChessPlatform.WebApi

open Microsoft.AspNetCore.SignalR
open System.Threading.Tasks

type GameHub() =
    inherit Hub()

    member this.JoinGame(gameId: string) =
        this.Groups.AddToGroupAsync (this.Context.ConnectionId, gameId)

    member this.JoinLobby(userId: string) =
        this.Groups.AddToGroupAsync (this.Context.ConnectionId, "user_" + userId)

    // Echo used by clients to measure round-trip latency
    member this.Ping() = Task.CompletedTask
