namespace ChessPlatform.Application

module MatchmakingUseCase =

    let enqueue (bus: IMessageBus) (userId: string) (elo: int) : Async<unit> =
        async { do! bus.EnqueueMatchmaking (userId, elo) }

    let dequeue (bus: IMessageBus) (userId: string) : Async<unit> =
        async { do! bus.RemoveFromMatchmakingQueue userId }
