namespace ChessPlatform.Application

module MatchmakingUseCase =

    /// Orchestrates adding a player to the matchmaking queue.
    let enqueue (bus: IMessageBus) (userId: string) (elo: int) : Async<unit> =
        async { do! bus.EnqueueMatchmaking (userId, elo) }

    /// Orchestrates removing a player from the matchmaking queue.
    let dequeue (bus: IMessageBus) (userId: string) : Async<unit> =
        async { do! bus.RemoveFromMatchmakingQueue userId }
