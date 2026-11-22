open Expecto
open Expecto.Flip
open FsCheck
open ChessPlatform.Domain

let play (state: GameState) (fromFile, fromRank) (toFile, toRank) =
    Game.playMove state {
        From = (fromFile, fromRank)
        To = (toFile, toRank)
        Promotion = None
    }

let playPromo (state: GameState) (fromFile, fromRank) (toFile, toRank) promo =
    Game.playMove state {
        From = (fromFile, fromRank)
        To = (toFile, toRank)
        Promotion = Some promo
    }

let unwrap result =
    match result with
    | Ok v -> v
    | Error e -> failwith $"Expected Ok but got Error: {e}"

let expectError result =
    match result with
    | Ok _ -> failwith "Expected Error but got Ok"
    | Error e -> e

let fenTests =
    testList "FenParser" [
        test "parse starting position" {
            let result = FenParser.parse FenParser.startingFen
            let state = unwrap result
            Expect.equal "" state.ActiveColor White
            Expect.equal "" (Map.count state.Board) 32
            Expect.equal "" state.CastlingRights.WhiteKingSide true
            Expect.equal "" state.CastlingRights.BlackQueenSide true
            Expect.equal "" state.EnPassantTarget None
            Expect.equal "" state.HalfMoveClock 0
            Expect.equal "" state.FullMoveNumber 1
        }

        test "parse specific pieces" {
            let state = FenParser.parse FenParser.startingFen |> unwrap
            Expect.equal "" (Map.find (4, 0) state.Board) { Color = White; Type = King }
            Expect.equal "" (Map.find (3, 7) state.Board) { Color = Black; Type = Queen }
            Expect.equal "" (Map.find (0, 1) state.Board) { Color = White; Type = Pawn }
        }

        test "round-trip starting position" {
            let state = FenParser.parse FenParser.startingFen |> unwrap
            let fen = FenParser.toFen state
            Expect.equal "" fen FenParser.startingFen
        }

        test "parse mid-game FEN" {
            let fen = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1"
            let state = FenParser.parse fen |> unwrap
            Expect.equal "" state.ActiveColor Black
            Expect.equal "" state.EnPassantTarget (Some (4, 2))
            Expect.equal "" (Map.find (4, 3) state.Board) { Color = White; Type = Pawn }
        }

        test "reject invalid FEN - wrong field count" {
            let result = FenParser.parse "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w"
            Expect.isTrue "" (Result.isError result)
        }

        test "reject invalid FEN - bad active color" {
            let result =
                FenParser.parse "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR x KQkq - 0 1"

            Expect.isTrue "" (Result.isError result)
        }

        test "algebraic round-trip" {
            let pos = FenParser.algebraicToPosition "e4"
            Expect.equal "" pos (Some (4, 3))
            Expect.equal "" (FenParser.positionToAlgebraic (4, 3)) "e4"
        }
    ]

let pawnTests =
    testList "Pawn" [
        test "single push" {
            let state = Game.newGame ()
            let result = play state (4, 1) (4, 2) |> unwrap
            Expect.equal "" (Map.find (4, 2) result.Board) { Color = White; Type = Pawn }
            Expect.isTrue "" (Game.isSquareEmpty result.Board (4, 1))
        }

        test "double push" {
            let state = Game.newGame ()
            let result = play state (4, 1) (4, 3) |> unwrap
            Expect.equal "" (Map.find (4, 3) result.Board) { Color = White; Type = Pawn }
            Expect.equal "" result.EnPassantTarget (Some (4, 2))
        }

        test "capture" {
            let state =
                Game.newGame () |> play <| (4, 1) <| (4, 3) |> unwrap |> play
                <| (3, 6)
                <| (3, 4)
                |> unwrap

            let result = play state (4, 3) (3, 4) |> unwrap
            Expect.equal "" (Map.find (3, 4) result.Board) { Color = White; Type = Pawn }
        }

        test "en passant" {
            let state =
                Game.newGame () |> play <| (4, 1) <| (4, 3) |> unwrap |> play
                <| (0, 6)
                <| (0, 5)
                |> unwrap
                |> play
                <| (4, 3)
                <| (4, 4)
                |> unwrap
                |> play
                <| (3, 6)
                <| (3, 4)
                |> unwrap

            let result = play state (4, 4) (3, 5) |> unwrap
            Expect.equal "" (Map.find (3, 5) result.Board) { Color = White; Type = Pawn }
            Expect.isTrue "" (Game.isSquareEmpty result.Board (3, 4))
        }

        test "promotion" {
            let fen = "8/P7/8/8/8/8/8/4K2k w - - 0 1"
            let state = FenParser.parse fen |> unwrap
            let result = playPromo state (0, 6) (0, 7) Queen |> unwrap
            Expect.equal "" (Map.find (0, 7) result.Board) { Color = White; Type = Queen }
        }

        test "promotion required - reject move without promotion type" {
            let fen = "8/P7/8/8/8/8/8/4K2k w - - 0 1"
            let state = FenParser.parse fen |> unwrap
            let result = play state (0, 6) (0, 7)
            Expect.isTrue "" (Result.isError result)
        }
    ]

let knightTests =
    testList "Knight" [
        test "L-shape move" {
            let state = Game.newGame ()
            let result = play state (1, 0) (2, 2) |> unwrap
            Expect.equal "" (Map.find (2, 2) result.Board) { Color = White; Type = Knight }
        }

        test "knight capture" {
            let fen = "8/8/3p4/8/4N3/8/8/4K2k w - - 0 1"
            let state = FenParser.parse fen |> unwrap
            let result = play state (4, 3) (3, 5) |> unwrap
            Expect.equal "" (Map.find (3, 5) result.Board) { Color = White; Type = Knight }
        }
    ]

let bishopTests =
    testList "Bishop" [
        test "diagonal move" {
            let fen = "8/8/8/8/3B4/8/8/4K2k w - - 0 1"
            let state = FenParser.parse fen |> unwrap
            let result = play state (3, 3) (6, 6) |> unwrap
            Expect.equal "" (Map.find (6, 6) result.Board) { Color = White; Type = Bishop }
        }

        test "blocked by own piece" {
            let fen = "8/8/8/4P3/3B4/8/8/4K2k w - - 0 1"
            let state = FenParser.parse fen |> unwrap
            let result = play state (3, 3) (5, 5)
            Expect.isTrue "" (Result.isError result)
        }
    ]

let rookTests =
    testList "Rook" [
        test "straight move" {
            let fen = "8/8/8/8/3R4/8/8/4K2k w - - 0 1"
            let state = FenParser.parse fen |> unwrap
            let result = play state (3, 3) (3, 7) |> unwrap
            Expect.equal "" (Map.find (3, 7) result.Board) { Color = White; Type = Rook }
        }
    ]

let queenTests =
    testList "Queen" [
        test "diagonal and straight" {
            let fen = "8/8/8/8/3Q4/8/8/4K2k w - - 0 1"
            let state = FenParser.parse fen |> unwrap
            let r1 = play state (3, 3) (6, 6) |> unwrap
            Expect.equal "" (Map.find (6, 6) r1.Board) { Color = White; Type = Queen }
            let r2 = play state (3, 3) (3, 7) |> unwrap
            Expect.equal "" (Map.find (3, 7) r2.Board) { Color = White; Type = Queen }
        }
    ]

let kingTests =
    testList "King & Castling" [
        test "single step" {
            let fen = "8/8/8/8/8/8/8/4K2k w - - 0 1"
            let state = FenParser.parse fen |> unwrap
            let result = play state (4, 0) (5, 1) |> unwrap
            Expect.equal "" (Map.find (5, 1) result.Board) { Color = White; Type = King }
        }

        test "kingside castling" {
            let fen = "r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1"
            let state = FenParser.parse fen |> unwrap
            let result = play state (4, 0) (6, 0) |> unwrap
            Expect.equal "" (Map.find (6, 0) result.Board) { Color = White; Type = King }
            Expect.equal "" (Map.find (5, 0) result.Board) { Color = White; Type = Rook }
        }

        test "queenside castling" {
            let fen = "r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1"
            let state = FenParser.parse fen |> unwrap
            let result = play state (4, 0) (2, 0) |> unwrap
            Expect.equal "" (Map.find (2, 0) result.Board) { Color = White; Type = King }
            Expect.equal "" (Map.find (3, 0) result.Board) { Color = White; Type = Rook }
        }

        test "castling revoked after king move" {
            let fen = "r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1"
            let state = FenParser.parse fen |> unwrap

            let result =
                play state (4, 0) (5, 0) |> unwrap |> play <| (0, 6) <| (0, 5) |> unwrap

            Expect.isFalse "" result.CastlingRights.WhiteKingSide
            Expect.isFalse "" result.CastlingRights.WhiteQueenSide
        }

        test "cannot castle through check" {
            let fen = "4k3/8/8/8/4r3/8/8/R3K2R w KQ - 0 1"
            let state = FenParser.parse fen |> unwrap
            let resultKS = play state (4, 0) (6, 0)
            let resultQS = play state (4, 0) (2, 0)
            Expect.isTrue "" (Result.isError resultKS)
            Expect.isTrue "" (Result.isError resultQS)
        }
    ]

let statusTests =
    testList "Game Status" [
        test "Scholar's Mate" {
            let state =
                Game.newGame () |> play <| (4, 1) <| (4, 3) |> unwrap |> play
                <| (4, 6)
                <| (4, 4)
                |> unwrap
                |> play
                <| (5, 0)
                <| (2, 3)
                |> unwrap
                |> play
                <| (1, 7)
                <| (2, 5)
                |> unwrap
                |> play
                <| (3, 0)
                <| (7, 4)
                |> unwrap
                |> play
                <| (6, 7)
                <| (5, 5)
                |> unwrap
                |> play
                <| (7, 4)
                <| (5, 6)
                |> unwrap

            Expect.equal "" state.Status (Checkmate White)
        }

        test "check detection" {
            let fen = "4k3/8/8/8/8/8/8/4K2R w - - 0 1"
            let state = FenParser.parse fen |> unwrap
            let result = play state (7, 0) (7, 7) |> unwrap

            match result.Status with
            | Check Black -> ()
            | other -> failwith $"Expected Check Black but got {other}"
        }

        test "stalemate" {
            let fen = "k7/8/1Q6/8/8/8/8/2K5 b - - 0 1"
            let state = FenParser.parse fen |> unwrap
            let legalMoves = Game.allLegalMoves state
            Expect.isTrue "" (List.isEmpty legalMoves)
            Expect.isFalse "" (Game.isKingInCheck state.Board Black)
            let fen2 = "k7/8/8/8/8/8/1Q6/2K5 w - - 0 1"
            let state2 = FenParser.parse fen2 |> unwrap
            let result = play state2 (1, 1) (1, 5) |> unwrap
            Expect.equal "" result.Status Stalemate
        }

        test "detectGameStatus on checkmate FEN" {
            let mateFen = "r1bqkbnr/pppp1Qpp/2n5/4p3/2B1P3/8/PPPP1PPP/RNB1K1NR b KQkq - 1 4"
            let state = FenParser.parse mateFen |> unwrap
            Expect.equal "Initial parsed status should be InProgress" state.Status InProgress
            let recalculated = Game.detectGameStatus state
            Expect.equal "Recalculated status should be Checkmate" recalculated (Checkmate White)
        }
    ]

let errorTests =
    testList "Error Cases" [
        test "wrong color" {
            let state = Game.newGame ()
            let result = play state (4, 6) (4, 5)
            Expect.equal "" (expectError result) WrongColor
        }

        test "no piece at source" {
            let state = Game.newGame ()
            let result = play state (4, 4) (4, 5)
            Expect.equal "" (expectError result) NoPieceAtSource
        }

        test "game already over" {
            let mateState = FenParser.parse "k7/2Q5/1K6/8/8/8/8/8 b - - 0 1" |> unwrap

            let mateState = {
                mateState with
                    Status = Checkmate White
            }

            let result = play mateState (0, 7) (1, 7)
            Expect.equal "" (expectError result) GameAlreadyOver
        }

        test "move leaves king in check" {
            let fen = "4k3/8/8/8/4r3/8/4N3/4K3 w - - 0 1"
            let state = FenParser.parse fen |> unwrap
            let result = play state (4, 1) (3, 3)
            Expect.isTrue "" (Result.isError result)
        }
    ]

let propertyTests =
    testList "Properties" [
        test "piece count never exceeds 32" {
            let state = Game.newGame ()
            Expect.isTrue "" (Map.count state.Board <= 32)

            let state2 =
                state |> play <| (4, 1) <| (4, 3) |> unwrap |> play <| (4, 6) <| (4, 4)
                |> unwrap

            Expect.isTrue "" (Map.count state2.Board <= 32)
        }

        test "exactly one king per color always" {
            let state = Game.newGame ()

            let whiteKings =
                state.Board
                |> Map.filter (fun _ p -> p.Color = White && p.Type = King)
                |> Map.count

            let blackKings =
                state.Board
                |> Map.filter (fun _ p -> p.Color = Black && p.Type = King)
                |> Map.count

            Expect.equal "" whiteKings 1
            Expect.equal "" blackKings 1

            let mated =
                Game.newGame () |> play <| (4, 1) <| (4, 3) |> unwrap |> play
                <| (4, 6)
                <| (4, 4)
                |> unwrap
                |> play
                <| (5, 0)
                <| (2, 3)
                |> unwrap
                |> play
                <| (1, 7)
                <| (2, 5)
                |> unwrap
                |> play
                <| (3, 0)
                <| (7, 4)
                |> unwrap
                |> play
                <| (6, 7)
                <| (5, 5)
                |> unwrap
                |> play
                <| (7, 4)
                <| (5, 6)
                |> unwrap

            let wk =
                mated.Board
                |> Map.filter (fun _ p -> p.Color = White && p.Type = King)
                |> Map.count

            let bk =
                mated.Board
                |> Map.filter (fun _ p -> p.Color = Black && p.Type = King)
                |> Map.count

            Expect.equal "" wk 1
            Expect.equal "" bk 1
        }

        test "active color alternates after each move" {
            let s0 = Game.newGame ()
            Expect.equal "" s0.ActiveColor White
            let s1 = play s0 (4, 1) (4, 3) |> unwrap
            Expect.equal "" s1.ActiveColor Black
            let s2 = play s1 (4, 6) (4, 4) |> unwrap
            Expect.equal "" s2.ActiveColor White
        }

        test "FEN round-trip preserves board" {
            let state = Game.newGame ()
            let fen = FenParser.toFen state
            let parsed = FenParser.parse fen |> unwrap
            Expect.equal "" parsed.Board state.Board
            Expect.equal "" parsed.ActiveColor state.ActiveColor
            Expect.equal "" parsed.CastlingRights state.CastlingRights
        }
    ]

[<Tests>]
let allTests =
    testList "ChessPlatform.Domain" [
        fenTests
        pawnTests
        knightTests
        bishopTests
        rookTests
        queenTests
        kingTests
        statusTests
        errorTests
        propertyTests
    ]

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args allTests
