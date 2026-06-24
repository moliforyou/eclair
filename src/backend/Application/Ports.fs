namespace ChessPlatform.Application

open ChessPlatform.Domain

type MoveDto = {
    From: Position
    To: Position
    Promotion: string option
}

module MoveExtensions =
    let toDto (move: Move) : MoveDto =
        let fromPos, toPos = Game.moveFromTo move
        let promo =
            match move with
            | Promotion (_, _, pt) -> Some (pt.ToString ())
            | PromotionCapture (_, _, _, pt) -> Some (pt.ToString ())
            | _ -> None
        { From = fromPos; To = toPos; Promotion = promo }

/// A wrapper around GameState to track user IDs, preserving Domain purity.
type GameRecord = {
    GameId: string
    State: GameState
    WhitePlayerId: string
    BlackPlayerId: string
}

type SaveGameError =
    | ConcurrencyConflict
    | PersistenceFailure of string

/// Port for database access.
type IGameRepository =
    /// Loads a game by ID.
    abstract member LoadGame: gameId: string -> Async<GameRecord option>

    /// Saves a game state, enforcing Optimistic Concurrency Control (OCC) using expectedVersion.
    abstract member SaveGame: record: GameRecord * expectedVersion: int -> Async<Result<unit, SaveGameError>>

    /// Creates a new game. Fails if the game already exists.
    abstract member CreateGame: record: GameRecord -> Async<Result<unit, SaveGameError>>

/// Port for publishing events to the message bus (Redis).
type IMessageBus =
    /// Publishes an event indicating a move was played.
    abstract member PublishMovePlayed: gameId: string * move: Move -> Async<unit>

    /// Adds a player to the matchmaking queue.
    abstract member EnqueueMatchmaking: userId: string * elo: int -> Async<unit>

    /// Subscribes to move played events. The handler receives the gameId.
    abstract member SubscribeMovePlayed: handler: (string -> Async<unit>) -> Async<System.IDisposable>

    /// Publishes an event indicating a match was found.
    abstract member PublishMatchFound: gameId: string * whitePlayerId: string * blackPlayerId: string -> Async<unit>

    /// Subscribes to matchmaking success events.
    abstract member SubscribeMatchFound: handler: (string * string * string -> Async<unit>) -> Async<System.IDisposable>

    /// Dequeues up to two players from the matchmaking queue.
    abstract member DequeueMatchmakingPair: unit -> Async<(string * int) list>
