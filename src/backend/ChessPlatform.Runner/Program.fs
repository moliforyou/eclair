namespace ChessPlatform.Runner

open System
open System.Threading.Tasks

module Program =

    [<EntryPoint>]
    let main args =
        printfn "Starting ChessPlatform Backend services (WebApi and BackgroundWorkers) concurrently..."

        let webApiTask = Task.Run(fun () ->
            try
                ChessPlatform.WebApi.Program.main args |> ignore
            with ex ->
                printfn "WebApi crashed: %A" ex
        )

        let workerTask = Task.Run(fun () ->
            try
                ChessPlatform.BackgroundWorkers.Program.main args |> ignore
            with ex ->
                printfn "BackgroundWorkers crashed: %A" ex
        )

        Task.WaitAll(webApiTask, workerTask)
        0
