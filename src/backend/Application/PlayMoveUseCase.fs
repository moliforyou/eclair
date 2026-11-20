namespace ChessPlatform.Application

open ChessPlatform.Domain

module PlayMoveUseCase =

    type PlayMoveError =
        | GameNotFound
        | NotYourTurn
        | DomainError of MoveError
        | ConcurrencyConflict
        | PersistenceError of string

    let execute
        (repo: IGameRepository)
        (bus: IMessageBus)
        (gameId: string)
        (userId: string)
        (request: MoveRequest)
        : Async<Result<GameState, PlayMoveError>>
        =
        async {
            let! gameOpt = repo.LoadGame gameId

            match gameOpt with
            | None -> return Error GameNotFound
            | Some record ->
                let expectedUserId =
                    match record.State.ActiveColor with
                    | White -> record.WhitePlayerId
                    | Black -> record.BlackPlayerId

                if userId <> expectedUserId then
                    return Error NotYourTurn
                else
                    match Game.playMove record.State request with
                    | Error domainErr -> return Error (DomainError domainErr)
                    | Ok newState ->
                        let playedMove = List.head newState.History

                        let newRecord = { record with State = newState }
                        let! saveResult = repo.SaveGame (newRecord, record.State.Version)

                        match saveResult with
                        | Error SaveGameError.ConcurrencyConflict -> return Error ConcurrencyConflict
                        | Error (SaveGameError.PersistenceFailure msg) -> return Error (PersistenceError msg)
                        | Ok () ->
                            do! bus.PublishMovePlayed (gameId, playedMove)
                            return Ok newState
        }
