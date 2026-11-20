namespace ChessPlatform.Domain

type Color =
    | White
    | Black

type PieceType =
    | King
    | Queen
    | Rook
    | Bishop
    | Knight
    | Pawn

type Piece = { Color: Color; Type: PieceType }

type Position = int * int

type Board = Map<Position, Piece>

type CastlingSide =
    | KingSide
    | QueenSide

type Move =
    | Normal of From: Position * To: Position
    | Capture of From: Position * To: Position * Captured: Piece
    | Castle of Color: Color * Side: CastlingSide
    | EnPassant of From: Position * To: Position * CapturedPawnPos: Position
    | Promotion of From: Position * To: Position * PromoteTo: PieceType
    | PromotionCapture of From: Position * To: Position * Captured: Piece * PromoteTo: PieceType

type CastlingRights = {
    WhiteKingSide: bool
    WhiteQueenSide: bool
    BlackKingSide: bool
    BlackQueenSide: bool
}

type GameStatus =
    | InProgress
    | Check of Color
    | Checkmate of Winner: Color
    | Stalemate
    | Draw of Reason: string

type GameState = {
    Board: Board
    ActiveColor: Color
    CastlingRights: CastlingRights
    EnPassantTarget: Position option
    HalfMoveClock: int
    FullMoveNumber: int
    History: Move list
    Status: GameStatus
    Version: int
}

type MoveError =
    | NoPieceAtSource
    | WrongColor
    | InvalidMove of string
    | MoveLeavesKingInCheck
    | GameAlreadyOver

type MoveRequest = {
    From: Position
    To: Position
    Promotion: PieceType option
}
