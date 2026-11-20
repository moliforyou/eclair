namespace ChessPlatform.Application

open ChessPlatform.Domain

type MoveSummaryDto = {
    From: Position
    To: Position
    Promotion: string option
}

module MoveExtensions =
    let toDto (move: Move) : MoveSummaryDto =
        let fromPos, toPos = Game.moveFromTo move

        let promo =
            match move with
            | Promotion (_, _, pt) -> Some (pt.ToString ())
            | PromotionCapture (_, _, _, pt) -> Some (pt.ToString ())
            | _ -> None

        {
            From = fromPos
            To = toPos
            Promotion = promo
        }
        : MoveSummaryDto

type GameRecord = {
    GameId: string
    State: GameState
    WhitePlayerId: string
    BlackPlayerId: string
}

type SaveGameError =
    | ConcurrencyConflict
    | PersistenceFailure of string

type IGameRepository =
    abstract member LoadGame: gameId: string -> Async<GameRecord option>

    abstract member SaveGame: record: GameRecord * expectedVersion: int -> Async<Result<unit, SaveGameError>>

    abstract member CreateGame: record: GameRecord -> Async<Result<unit, SaveGameError>>

type IMessageBus =
    abstract member PublishMovePlayed: gameId: string * move: Move -> Async<unit>

    abstract member PublishGameUpdated: gameId: string -> Async<unit>

    abstract member EnqueueMatchmaking: userId: string * elo: int -> Async<unit>

    abstract member SubscribeMovePlayed: handler: (string -> Async<unit>) -> Async<System.IDisposable>

    abstract member PublishMatchFound: gameId: string * whitePlayerId: string * blackPlayerId: string -> Async<unit>

    abstract member SubscribeMatchFound: handler: (string * string * string -> Async<unit>) -> Async<System.IDisposable>

    abstract member DequeueMatchmakingPair: unit -> Async<(string * int) list>

    abstract member RemoveFromMatchmakingQueue: userId: string -> Async<unit>
