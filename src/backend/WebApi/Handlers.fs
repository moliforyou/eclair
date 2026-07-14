namespace ChessPlatform.WebApi

open Falco
open Falco.Routing
open Falco.Markup
open ChessPlatform.Domain
open ChessPlatform.Application
open Microsoft.AspNetCore.Http

module Handlers =

    type MatchmakingRequest = { Elo: int }

    type MoveRequestDto = {
        FromRow: int
        FromCol: int
        ToRow: int
        ToCol: int
        Promotion: string
    }

    let matchmakingHandler (bus: IMessageBus) : HttpHandler =
        Request.mapJson (fun (req: MatchmakingRequest) ->
            fun ctx ->
                task {
                    let userId =
                        match ctx.Request.Headers.TryGetValue ("X-User-Id") with
                        | true, v -> v.ToString ()
                        | _ -> "anonymous"

                    do! bus.EnqueueMatchmaking (userId, req.Elo)
                    return! Response.withStatusCode 202 >> Response.ofJson {| Message = "Queued" |} <| ctx
                }
        )

    let playMoveHandler (repo: IGameRepository) (bus: IMessageBus) : HttpHandler =
        Request.mapJson (fun (reqDto: MoveRequestDto) ->
            fun ctx ->
                task {
                    let route = Request.getRoute ctx
                    let gameId = route.GetString "id"

                    let userId =
                        match ctx.Request.Headers.TryGetValue ("X-User-Id") with
                        | true, v -> v.ToString ()
                        | _ -> "anonymous"

                    let inBounds v = v >= 0 && v <= 7

                    let coordsValid =
                        [ reqDto.FromCol; reqDto.FromRow; reqDto.ToCol; reqDto.ToRow ]
                        |> List.forall inBounds

                    let promotionResult =
                        if System.String.IsNullOrEmpty reqDto.Promotion then
                            Ok None
                        else
                            match reqDto.Promotion with
                            | "Q" -> Ok (Some Queen)
                            | "R" -> Ok (Some Rook)
                            | "B" -> Ok (Some Bishop)
                            | "N" -> Ok (Some Knight)
                            | other ->
                                Error (sprintf "Invalid promotion piece '%s'. Expected one of: Q, R, B, N" other)

                    match coordsValid, promotionResult with
                    | false, _ ->
                        return!
                            Response.withStatusCode 400
                            >> Response.ofJson {|
                                Error = "Coordinates must be between 0 and 7"
                            |}
                            <| ctx
                    | true, Error msg ->
                        return! Response.withStatusCode 400 >> Response.ofJson {| Error = msg |} <| ctx
                    | true, Ok promotion ->
                        let req: MoveRequest = {
                            From = (reqDto.FromCol, reqDto.FromRow)
                            To = (reqDto.ToCol, reqDto.ToRow)
                            Promotion = promotion
                        }

                        let! result = PlayMoveUseCase.execute repo bus gameId userId req

                        match result with
                        | Ok state ->
                            return!
                                Response.withStatusCode 200 >> Response.ofJson {| Fen = FenParser.toFen state |}
                                <| ctx
                        | Error err ->
                            match err with
                            | PlayMoveUseCase.GameNotFound ->
                                return!
                                    Response.withStatusCode 404 >> Response.ofJson {| Error = "Game not found" |}
                                    <| ctx
                            | PlayMoveUseCase.NotYourTurn ->
                                return!
                                    Response.withStatusCode 400 >> Response.ofJson {| Error = "Not your turn" |}
                                    <| ctx
                            | PlayMoveUseCase.DomainError e ->
                                return!
                                    Response.withStatusCode 400 >> Response.ofJson {| Error = sprintf "%A" e |}
                                    <| ctx
                            | PlayMoveUseCase.ConcurrencyConflict ->
                                return!
                                    Response.withStatusCode 409
                                    >> Response.ofJson {| Error = "Concurrency conflict" |}
                                    <| ctx
                            | PlayMoveUseCase.PersistenceError msg ->
                                return! Response.withStatusCode 500 >> Response.ofJson {| Error = msg |} <| ctx
                }
        )

    type CreateGameRequestDto = {
        WhitePlayerId: string
        BlackPlayerId: string
    }

    let createGameHandler (repo: IGameRepository) (bus: IMessageBus) : HttpHandler =
        Request.mapJson (fun (reqDto: CreateGameRequestDto) ->
            fun ctx ->
                task {
                    let route = Request.getRoute ctx
                    let gameId = route.GetString "id"

                    let record = {
                        GameId = gameId
                        State = Game.newGame ()
                        WhitePlayerId = reqDto.WhitePlayerId
                        BlackPlayerId = reqDto.BlackPlayerId
                    }

                    let! result = repo.CreateGame record

                    match result with
                    | Ok _ ->
                        // Notify subscribers of game initialization
                        do! bus.PublishGameUpdated gameId

                        return!
                            Response.withStatusCode 201
                            >> Response.ofJson {|
                                Message = "Game created"
                                GameId = gameId
                            |}
                            <| ctx
                    | Error SaveGameError.ConcurrencyConflict ->
                        let! existingOpt = repo.LoadGame gameId

                        match existingOpt with
                        | Some existing ->
                            let resetRecord = {
                                record with
                                    State = {
                                        Game.newGame () with
                                            Version = existing.State.Version
                                    }
                                    WhitePlayerId = reqDto.WhitePlayerId
                                    BlackPlayerId = reqDto.BlackPlayerId
                            }

                            let! saveResult = repo.SaveGame (resetRecord, existing.State.Version)

                            match saveResult with
                            | Ok _ ->
                                do! bus.PublishGameUpdated gameId

                                return!
                                    Response.withStatusCode 200
                                    >> Response.ofJson {|
                                        Message = "Game reset"
                                        GameId = gameId
                                    |}
                                    <| ctx
                            | Error _ ->
                                return!
                                    Response.withStatusCode 500
                                    >> Response.ofJson {| Error = "Failed to reset game" |}
                                    <| ctx
                        | None ->
                            return!
                                Response.withStatusCode 500
                                >> Response.ofJson {|
                                    Error = "Failed to load game for reset"
                                |}
                                <| ctx
                    | Error (SaveGameError.PersistenceFailure msg) ->
                        return! Response.withStatusCode 500 >> Response.ofJson {| Error = msg |} <| ctx
                }
        )

    let getGameHandler (repo: IGameRepository) : HttpHandler =
        fun ctx ->
            task {
                let route = Request.getRoute ctx
                let gameId = route.GetString "id"

                let! gameOpt = repo.LoadGame gameId

                match gameOpt with
                | Some record ->
                    let fen = FenParser.toFen record.State

                    let turnChar =
                        match record.State.ActiveColor with
                        | White -> "w"
                        | Black -> "b"

                    let moves =
                        record.State.History
                        |> List.rev
                        |> List.map MoveExtensions.toDto
                        |> List.toArray

                    let status =
                        match record.State.Status with
                        | InProgress -> "InProgress"
                        | Check _ -> "Check"
                        | Checkmate w -> sprintf "Checkmate:%A" w
                        | Stalemate -> "Stalemate"
                        | Draw r -> sprintf "Draw:%s" r

                    return!
                        Response.withStatusCode 200
                        >> Response.ofJson {|
                            GameId = gameId
                            Fen = fen
                            Turn = turnChar
                            WhitePlayerId = record.WhitePlayerId
                            BlackPlayerId = record.BlackPlayerId
                            Moves = moves
                            Status = status
                        |}
                        <| ctx
                | None ->
                    return!
                        Response.withStatusCode 404 >> Response.ofJson {| Error = "Game not found" |}
                        <| ctx
            }
