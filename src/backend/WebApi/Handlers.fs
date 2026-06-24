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

                    let req: MoveRequest = {
                        From = (reqDto.FromCol, reqDto.FromRow)
                        To = (reqDto.ToCol, reqDto.ToRow)
                        Promotion =
                            match reqDto.Promotion with
                            | "Q" -> Some Queen
                            | "R" -> Some Rook
                            | "B" -> Some Bishop
                            | "N" -> Some Knight
                            | _ -> None
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
                        // Publish a dummy move event to notify subscribers of game initialization
                        do! bus.PublishMovePlayed (gameId, Normal ((0, 0), (0, 0)))
                        return! Response.withStatusCode 201 >> Response.ofJson {| Message = "Game created"; GameId = gameId |} <| ctx
                    | Error SaveGameError.ConcurrencyConflict ->
                        let! existingOpt = repo.LoadGame gameId
                        match existingOpt with
                        | Some existing ->
                            let resetRecord = {
                                record with
                                    State = { Game.newGame () with Version = existing.State.Version }
                                    WhitePlayerId = reqDto.WhitePlayerId
                                    BlackPlayerId = reqDto.BlackPlayerId
                            }
                            let! saveResult = repo.SaveGame (resetRecord, existing.State.Version)
                            match saveResult with
                            | Ok _ ->
                                do! bus.PublishMovePlayed (gameId, Normal ((0, 0), (0, 0)))
                                return! Response.withStatusCode 200 >> Response.ofJson {| Message = "Game reset"; GameId = gameId |} <| ctx
                            | Error _ ->
                                return! Response.withStatusCode 500 >> Response.ofJson {| Error = "Failed to reset game" |} <| ctx
                        | None ->
                            return! Response.withStatusCode 500 >> Response.ofJson {| Error = "Failed to load game for reset" |} <| ctx
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
                    return! Response.withStatusCode 200 >> Response.ofJson {| GameId = gameId; Fen = fen; Turn = turnChar; WhitePlayerId = record.WhitePlayerId; BlackPlayerId = record.BlackPlayerId; Moves = moves |} <| ctx
                | None ->
                    return! Response.withStatusCode 404 >> Response.ofJson {| Error = "Game not found" |} <| ctx
            }
