namespace ChessPlatform.Domain

module Game =

    let oppositeColor color =
        match color with
        | White -> Black
        | Black -> White

    let isOnBoard ((file, rank): Position) =
        file >= 0 && file <= 7 && rank >= 0 && rank <= 7

    let isSquareEmpty (board: Board) (pos: Position) = not (Map.containsKey pos board)

    let pieceAt (board: Board) (pos: Position) = Map.tryFind pos board

    let isOccupiedByColor (board: Board) (color: Color) (pos: Position) =
        match pieceAt board pos with
        | Some p -> p.Color = color
        | None -> false

    let isOccupiedByEnemy (board: Board) (color: Color) (pos: Position) =
        isOccupiedByColor board (oppositeColor color) pos

    let pawnDirection color =
        match color with
        | White -> 1
        | Black -> -1

    let pawnStartRank color =
        match color with
        | White -> 1
        | Black -> 6

    let promotionRank color =
        match color with
        | White -> 7
        | Black -> 0

    let findKing (board: Board) (color: Color) : Position option =
        board
        |> Map.tryFindKey (fun _ piece -> piece.Color = color && piece.Type = King)

    let private slidingMoves (board: Board) (color: Color) (from: Position) (directions: (int * int) list) : Move list = [
        for (df, dr) in directions do
            let mutable file, rank = from
            let mutable blocked = false

            while not blocked do
                file <- file + df
                rank <- rank + dr
                let target = (file, rank)

                if not (isOnBoard target) then
                    blocked <- true
                else
                    match pieceAt board target with
                    | None -> yield Normal (from, target)
                    | Some piece when piece.Color <> color ->
                        yield Capture (from, target, piece)
                        blocked <- true
                    | Some _ -> blocked <- true
    ]

    let private pawnMoves (board: Board) (color: Color) (from: Position) (epTarget: Position option) : Move list =
        let (file, rank) = from
        let dir = pawnDirection color
        let oneForward = (file, rank + dir)
        let twoForward = (file, rank + 2 * dir)
        let promoRank = promotionRank color
        let startRank = pawnStartRank color

        let promotionTypes = [ Queen; Rook; Bishop; Knight ]

        [
            if isOnBoard oneForward && isSquareEmpty board oneForward then
                if snd oneForward = promoRank then
                    for pt in promotionTypes do
                        yield Promotion (from, oneForward, pt)
                else
                    yield Normal (from, oneForward)

                    if rank = startRank && isSquareEmpty board twoForward then
                        yield Normal (from, twoForward)

            for df in [ -1; 1 ] do
                let captureTarget = (file + df, rank + dir)

                if isOnBoard captureTarget then
                    match pieceAt board captureTarget with
                    | Some piece when piece.Color <> color ->
                        if snd captureTarget = promoRank then
                            for pt in promotionTypes do
                                yield PromotionCapture (from, captureTarget, piece, pt)
                        else
                            yield Capture (from, captureTarget, piece)
                    | _ -> ()

                    match epTarget with
                    | Some ep when ep = captureTarget ->
                        let capturedPawnPos = (file + df, rank)
                        yield EnPassant (from, captureTarget, capturedPawnPos)
                    | _ -> ()
        ]

    let private knightMoves (board: Board) (color: Color) (from: Position) : Move list =
        let (file, rank) = from

        let offsets = [ (-2, -1); (-2, 1); (-1, -2); (-1, 2); (1, -2); (1, 2); (2, -1); (2, 1) ]

        [
            for (df, dr) in offsets do
                let target = (file + df, rank + dr)

                if isOnBoard target then
                    match pieceAt board target with
                    | None -> yield Normal (from, target)
                    | Some piece when piece.Color <> color -> yield Capture (from, target, piece)
                    | _ -> ()
        ]

    let private kingBasicMoves (board: Board) (color: Color) (from: Position) : Move list =
        let (file, rank) = from

        let offsets = [ (-1, -1); (-1, 0); (-1, 1); (0, -1); (0, 1); (1, -1); (1, 0); (1, 1) ]

        [
            for (df, dr) in offsets do
                let target = (file + df, rank + dr)

                if isOnBoard target then
                    match pieceAt board target with
                    | None -> yield Normal (from, target)
                    | Some piece when piece.Color <> color -> yield Capture (from, target, piece)
                    | _ -> ()
        ]

    let isSquareAttackedBy (board: Board) (attackerColor: Color) (targetSquare: Position) : bool =
        let (tf, tr) = targetSquare

        let pawnDir = pawnDirection attackerColor

        let pawnAttack =
            [ (tf - 1, tr - pawnDir); (tf + 1, tr - pawnDir) ]
            |> List.exists (fun pos ->
                isOnBoard pos
                && (pieceAt board pos = Some { Color = attackerColor; Type = Pawn })
            )

        if pawnAttack then
            true
        else

            let knightOffsets = [ (-2, -1); (-2, 1); (-1, -2); (-1, 2); (1, -2); (1, 2); (2, -1); (2, 1) ]

            let knightAttack =
                knightOffsets
                |> List.exists (fun (df, dr) ->
                    let pos = (tf + df, tr + dr)

                    isOnBoard pos
                    && (pieceAt board pos = Some { Color = attackerColor; Type = Knight })
                )

            if knightAttack then
                true
            else

                let kingOffsets = [ (-1, -1); (-1, 0); (-1, 1); (0, -1); (0, 1); (1, -1); (1, 0); (1, 1) ]

                let kingAttack =
                    kingOffsets
                    |> List.exists (fun (df, dr) ->
                        let pos = (tf + df, tr + dr)

                        isOnBoard pos
                        && (pieceAt board pos = Some { Color = attackerColor; Type = King })
                    )

                if kingAttack then
                    true
                else

                    let rookDirs = [ (1, 0); (-1, 0); (0, 1); (0, -1) ]

                    let rookQueenAttack =
                        rookDirs
                        |> List.exists (fun (df, dr) ->
                            let rec scan f r =
                                let nf, nr = f + df, r + dr

                                if not (isOnBoard (nf, nr)) then
                                    false
                                else
                                    match pieceAt board (nf, nr) with
                                    | None -> scan nf nr
                                    | Some p -> p.Color = attackerColor && (p.Type = Rook || p.Type = Queen)

                            scan tf tr
                        )

                    if rookQueenAttack then
                        true
                    else

                        let bishopDirs = [ (1, 1); (1, -1); (-1, 1); (-1, -1) ]

                        bishopDirs
                        |> List.exists (fun (df, dr) ->
                            let rec scan f r =
                                let nf, nr = f + df, r + dr

                                if not (isOnBoard (nf, nr)) then
                                    false
                                else
                                    match pieceAt board (nf, nr) with
                                    | None -> scan nf nr
                                    | Some p -> p.Color = attackerColor && (p.Type = Bishop || p.Type = Queen)

                            scan tf tr
                        )

    let isKingInCheck (board: Board) (color: Color) : bool =
        match findKing board color with
        | Some kingPos -> isSquareAttackedBy board (oppositeColor color) kingPos
        | None -> false

    let private castlingMoves (state: GameState) : Move list =
        let color = state.ActiveColor
        let board = state.Board
        let rank = if color = White then 0 else 7
        let enemy = oppositeColor color

        if isKingInCheck board color then
            []
        else
            [
                if
                    (match color with
                     | White -> state.CastlingRights.WhiteKingSide
                     | Black -> state.CastlingRights.BlackKingSide)
                then
                    let f5 = (5, rank)
                    let f6 = (6, rank)

                    if
                        isSquareEmpty board f5
                        && isSquareEmpty board f6
                        && not (isSquareAttackedBy board enemy (4, rank))
                        && not (isSquareAttackedBy board enemy f5)
                        && not (isSquareAttackedBy board enemy f6)
                    then
                        yield Castle (color, KingSide)

                if
                    (match color with
                     | White -> state.CastlingRights.WhiteQueenSide
                     | Black -> state.CastlingRights.BlackQueenSide)
                then
                    let f3 = (3, rank)
                    let f2 = (2, rank)
                    let f1 = (1, rank)

                    if
                        isSquareEmpty board f3
                        && isSquareEmpty board f2
                        && isSquareEmpty board f1
                        && not (isSquareAttackedBy board enemy (4, rank))
                        && not (isSquareAttackedBy board enemy f3)
                        && not (isSquareAttackedBy board enemy f2)
                    then
                        yield Castle (color, QueenSide)
            ]

    let private pseudoLegalMovesForPiece (state: GameState) (pos: Position) (piece: Piece) : Move list =
        let board = state.Board
        let color = piece.Color

        match piece.Type with
        | Pawn -> pawnMoves board color pos state.EnPassantTarget
        | Knight -> knightMoves board color pos
        | Bishop ->
            let dirs = [ (1, 1); (1, -1); (-1, 1); (-1, -1) ]
            slidingMoves board color pos dirs
        | Rook ->
            let dirs = [ (1, 0); (-1, 0); (0, 1); (0, -1) ]
            slidingMoves board color pos dirs
        | Queen ->
            let dirs = [ (1, 0); (-1, 0); (0, 1); (0, -1); (1, 1); (1, -1); (-1, 1); (-1, -1) ]

            slidingMoves board color pos dirs
        | King -> kingBasicMoves board color pos

    let moveFromTo (move: Move) : Position * Position =
        match move with
        | Normal (f, t) -> (f, t)
        | Capture (f, t, _) -> (f, t)
        | Castle (color, side) ->
            let rank = if color = White then 0 else 7

            match side with
            | KingSide -> ((4, rank), (6, rank))
            | QueenSide -> ((4, rank), (2, rank))
        | EnPassant (f, t, _) -> (f, t)
        | Promotion (f, t, _) -> (f, t)
        | PromotionCapture (f, t, _, _) -> (f, t)

    let applyMoveToBoard (board: Board) (move: Move) : Board =
        match move with
        | Normal (from, to') ->
            let piece = Map.find from board
            board |> Map.remove from |> Map.add to' piece

        | Capture (from, to', _) ->
            let piece = Map.find from board
            board |> Map.remove from |> Map.remove to' |> Map.add to' piece

        | Castle (color, side) ->
            let rank = if color = White then 0 else 7
            let king = Map.find (4, rank) board

            match side with
            | KingSide ->
                let rook = Map.find (7, rank) board

                board
                |> Map.remove (4, rank)
                |> Map.remove (7, rank)
                |> Map.add (6, rank) king
                |> Map.add (5, rank) rook
            | QueenSide ->
                let rook = Map.find (0, rank) board

                board
                |> Map.remove (4, rank)
                |> Map.remove (0, rank)
                |> Map.add (2, rank) king
                |> Map.add (3, rank) rook

        | EnPassant (from, to', capturedPawnPos) ->
            let piece = Map.find from board

            board |> Map.remove from |> Map.remove capturedPawnPos |> Map.add to' piece

        | Promotion (from, to', promoteTo) ->
            let piece = Map.find from board
            let promoted = { piece with Type = promoteTo }
            board |> Map.remove from |> Map.add to' promoted

        | PromotionCapture (from, to', _, promoteTo) ->
            let piece = Map.find from board
            let promoted = { piece with Type = promoteTo }

            board |> Map.remove from |> Map.remove to' |> Map.add to' promoted

    let private filterLegalMoves (board: Board) (color: Color) (moves: Move list) : Move list =
        moves
        |> List.filter (fun move ->
            let newBoard = applyMoveToBoard board move
            not (isKingInCheck newBoard color)
        )

    let allLegalMoves (state: GameState) : Move list =
        let color = state.ActiveColor
        let board = state.Board

        let pieceMoves =
            board
            |> Map.toList
            |> List.collect (fun (pos, piece) ->
                if piece.Color = color then
                    pseudoLegalMovesForPiece state pos piece
                else
                    []
            )

        let castles = castlingMoves state
        let allPseudo = pieceMoves @ castles
        filterLegalMoves board color allPseudo

    let private updateCastlingRights (rights: CastlingRights) (move: Move) (board: Board) : CastlingRights =
        let (from, to') = moveFromTo move

        let rights =
            match pieceAt board from with
            | Some { Color = White; Type = King } -> {
                rights with
                    WhiteKingSide = false
                    WhiteQueenSide = false
              }
            | Some { Color = Black; Type = King } -> {
                rights with
                    BlackKingSide = false
                    BlackQueenSide = false
              }
            | _ -> rights

        let rights =
            match from with
            | (0, 0) -> { rights with WhiteQueenSide = false }
            | (7, 0) -> { rights with WhiteKingSide = false }
            | (0, 7) -> { rights with BlackQueenSide = false }
            | (7, 7) -> { rights with BlackKingSide = false }
            | _ -> rights

        let rights =
            match to' with
            | (0, 0) -> { rights with WhiteQueenSide = false }
            | (7, 0) -> { rights with WhiteKingSide = false }
            | (0, 7) -> { rights with BlackQueenSide = false }
            | (7, 7) -> { rights with BlackKingSide = false }
            | _ -> rights

        rights

    let detectGameStatus (state: GameState) : GameStatus =
        let opponent = state.ActiveColor
        let legalMoves = allLegalMoves state

        if List.isEmpty legalMoves then
            if isKingInCheck state.Board opponent then
                Checkmate (oppositeColor opponent)
            else
                Stalemate
        else if isKingInCheck state.Board opponent then
            Check opponent
        else if state.HalfMoveClock >= 100 then
            Draw "Fifty-move rule"
        else
            InProgress

    let resolveMove (state: GameState) (request: MoveRequest) : Result<Move, MoveError> =
        match state.Status with
        | Checkmate _
        | Stalemate
        | Draw _ -> Error GameAlreadyOver
        | _ ->

            match pieceAt state.Board request.From with
            | None -> Error NoPieceAtSource
            | Some piece ->

                if piece.Color <> state.ActiveColor then
                    Error WrongColor
                else

                    let legalMoves = allLegalMoves state

                    let matchingMove =
                        legalMoves
                        |> List.tryFind (fun move ->
                            let (from, to') = moveFromTo move

                            from = request.From
                            && to' = request.To
                            && (
                                match request.Promotion, move with
                                | Some pt, Promotion (_, _, mpt) -> pt = mpt
                                | Some pt, PromotionCapture (_, _, _, mpt) -> pt = mpt
                                | None, Promotion _ -> false
                                | None, PromotionCapture _ -> false
                                | _ -> true
                            )
                        )

                    match matchingMove with
                    | None -> Error (InvalidMove "No legal move matches the request")
                    | Some move -> Ok move

    let applyMove (state: GameState) (move: Move) : Result<GameState, MoveError> =
        match state.Status with
        | Checkmate _
        | Stalemate
        | Draw _ -> Error GameAlreadyOver
        | _ ->

            let (from, _to) = moveFromTo move

            match pieceAt state.Board from with
            | None -> Error NoPieceAtSource
            | Some piece ->

                if piece.Color <> state.ActiveColor then
                    Error WrongColor
                else

                    let newBoard = applyMoveToBoard state.Board move

                    if isKingInCheck newBoard state.ActiveColor then
                        Error MoveLeavesKingInCheck
                    else

                        let newCastling = updateCastlingRights state.CastlingRights move state.Board

                        let newEpTarget =
                            match move with
                            | Normal (fromPos, toPos) ->
                                let piece = Map.find fromPos state.Board

                                if piece.Type = Pawn && abs (snd toPos - snd fromPos) = 2 then
                                    Some (fst fromPos, (snd fromPos + snd toPos) / 2)
                                else
                                    None
                            | _ -> None

                        let isCapture =
                            match move with
                            | Capture _
                            | EnPassant _
                            | PromotionCapture _ -> true
                            | _ -> false

                        let isPawnMove =
                            match pieceAt state.Board from with
                            | Some p -> p.Type = Pawn
                            | None -> false

                        let newHalfMoveClock =
                            if isPawnMove || isCapture then
                                0
                            else
                                state.HalfMoveClock + 1

                        let newFullMoveNumber =
                            if state.ActiveColor = Black then
                                state.FullMoveNumber + 1
                            else
                                state.FullMoveNumber

                        let newState = {
                            Board = newBoard
                            ActiveColor = oppositeColor state.ActiveColor
                            CastlingRights = newCastling
                            EnPassantTarget = newEpTarget
                            HalfMoveClock = newHalfMoveClock
                            FullMoveNumber = newFullMoveNumber
                            History = move :: state.History
                            Status = InProgress
                            Version = state.Version + 1
                        }

                        let status = detectGameStatus newState
                        Ok { newState with Status = status }

    let playMove (state: GameState) (request: MoveRequest) : Result<GameState, MoveError> =
        resolveMove state request |> Result.bind (applyMove state)

    let newGame () : GameState =
        match FenParser.parse FenParser.startingFen with
        | Ok state -> state
        | Error msg -> failwith $"Bug: could not parse starting FEN: {msg}"
