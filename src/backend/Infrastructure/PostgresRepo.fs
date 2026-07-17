namespace ChessPlatform.Infrastructure

open ChessPlatform.Domain
open ChessPlatform.Application
open Npgsql
open Npgsql.FSharp
open System.Text.Json

/// FEN cannot encode move history, so it is persisted separately as a JSON
/// array (chronological order) of from/to coordinates plus optional promotion.
module HistoryJson =

    [<CLIMutable>]
    type StoredMove = {
        F: int array
        T: int array
        P: string
    }

    let ofState (state: GameState) : string =
        state.History
        |> List.rev
        |> List.map (fun move ->
            let (fc, fr), (tc, tr) = Game.moveFromTo move

            let promo =
                match move with
                | Promotion (_, _, pt) -> pt.ToString ()
                | PromotionCapture (_, _, _, pt) -> pt.ToString ()
                | _ -> ""

            {
                F = [| fc; fr |]
                T = [| tc; tr |]
                P = promo
            }
        )
        |> List.toArray
        |> JsonSerializer.Serialize

    /// Rebuilds a display-sufficient history (newest first). Captures, castling
    /// and en passant round-trip as Normal moves carrying the same from/to,
    /// which is all the MoveSummaryDto projection uses.
    let toHistory (json: string) : Move list =
        try
            match JsonSerializer.Deserialize<StoredMove array> (json) with
            | null -> []
            | stored ->
                stored
                |> Array.toList
                |> List.map (fun sm ->
                    let fromPos = (sm.F.[0], sm.F.[1])
                    let toPos = (sm.T.[0], sm.T.[1])

                    match sm.P with
                    | "Queen" -> Promotion (fromPos, toPos, Queen)
                    | "Rook" -> Promotion (fromPos, toPos, Rook)
                    | "Bishop" -> Promotion (fromPos, toPos, Bishop)
                    | "Knight" -> Promotion (fromPos, toPos, Knight)
                    | _ -> Normal (fromPos, toPos)
                )
                |> List.rev
        with _ -> []

type PostgresRepo(connectionString: string) =

    // Simple schema initialization
    do
        try
            connectionString
            |> Sql.connect
            |> Sql.query
                """
                CREATE TABLE IF NOT EXISTS Games (
                    GameId VARCHAR(50) PRIMARY KEY,
                    StateFen TEXT NOT NULL,
                    WhitePlayerId VARCHAR(50) NOT NULL,
                    BlackPlayerId VARCHAR(50) NOT NULL,
                    Version INTEGER NOT NULL,
                    HistoryJson TEXT NOT NULL DEFAULT '[]'
                )
                """
            |> Sql.executeNonQuery
            |> ignore

            connectionString
            |> Sql.connect
            |> Sql.query "ALTER TABLE Games ADD COLUMN IF NOT EXISTS HistoryJson TEXT NOT NULL DEFAULT '[]'"
            |> Sql.executeNonQuery
            |> ignore
        with ex ->
            printfn "Warning: DB initialization encountered an exception: %s" ex.Message

    interface IGameRepository with
        member this.LoadGame(gameId: string) =
            async {
                let result =
                    connectionString
                    |> Sql.connect
                    |> Sql.query
                        "SELECT StateFen, WhitePlayerId, BlackPlayerId, Version, HistoryJson FROM Games WHERE GameId = @id"
                    |> Sql.parameters [ "id", Sql.string gameId ]
                    |> Sql.execute (fun read -> {|
                        StateFen = read.string "statefen"
                        WhitePlayerId = read.string "whiteplayerid"
                        BlackPlayerId = read.string "blackplayerid"
                        Version = read.int "version"
                        HistoryJson = read.string "historyjson"
                    |})

                match result with
                | [] -> return None
                | [ row ] ->
                    match FenParser.parse row.StateFen with
                    | Ok state ->
                        let stateWithVersion = { state with Version = row.Version }
                        let finalStatus = Game.detectGameStatus stateWithVersion

                        let stateWithStatus = {
                            stateWithVersion with
                                Status = finalStatus
                                History = HistoryJson.toHistory row.HistoryJson
                        }

                        return
                            Some {
                                GameId = gameId
                                State = stateWithStatus
                                WhitePlayerId = row.WhitePlayerId
                                BlackPlayerId = row.BlackPlayerId
                            }
                    | Error err ->
                        eprintfn
                            "Error: stored FEN for game %s failed to parse (%A); treating game as not found"
                            gameId
                            err

                        return None
                | _ -> return None
            }

        member this.SaveGame(record: GameRecord, expectedVersion: int) =
            async {
                let fen = FenParser.toFen record.State

                try
                    let affected =
                        connectionString
                        |> Sql.connect
                        |> Sql.query
                            "UPDATE Games SET StateFen = @fen, HistoryJson = @history, Version = @newVersion WHERE GameId = @id AND Version = @expectedVersion"
                        |> Sql.parameters [
                            "id", Sql.string record.GameId
                            "fen", Sql.string fen
                            "history", Sql.string (HistoryJson.ofState record.State)
                            "newVersion", Sql.int (expectedVersion + 1)
                            "expectedVersion", Sql.int expectedVersion
                        ]
                        |> Sql.executeNonQuery

                    if affected = 1 then
                        return Ok ()
                    else
                        return Error SaveGameError.ConcurrencyConflict
                with ex ->
                    return Error (SaveGameError.PersistenceFailure ex.Message)
            }

        member this.CreateGame(record: GameRecord) =
            async {
                let fen = FenParser.toFen record.State

                try
                    let affected =
                        connectionString
                        |> Sql.connect
                        |> Sql.query
                            "INSERT INTO Games (GameId, StateFen, WhitePlayerId, BlackPlayerId, Version, HistoryJson) VALUES (@id, @fen, @w, @b, @v, @history)"
                        |> Sql.parameters [
                            "id", Sql.string record.GameId
                            "fen", Sql.string fen
                            "w", Sql.string record.WhitePlayerId
                            "b", Sql.string record.BlackPlayerId
                            "v", Sql.int record.State.Version
                            "history", Sql.string (HistoryJson.ofState record.State)
                        ]
                        |> Sql.executeNonQuery

                    if affected = 1 then
                        return Ok ()
                    else
                        return Error SaveGameError.ConcurrencyConflict
                with
                | :? PostgresException as ex when ex.SqlState = "23505" -> // unique_violation
                    return Error SaveGameError.ConcurrencyConflict
                | ex -> return Error (SaveGameError.PersistenceFailure ex.Message)
            }
