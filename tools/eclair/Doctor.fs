module Doctor

open System

type PortOwner =
    | Free
    | Listening of pid: int * command: string

type Facts = {
    DockerUp: bool
    Services: Map<string, string>
    Port5000: PortOwner
    RunnerBuiltAt: DateTime option
    SourcesChangedAt: DateTime
}

type Status =
    | Pass
    | Fail of problem: string * fix: string
    | Blocked of by: string

type Check = { Name: string; Status: Status }

let isRunner (command: string) = command.Contains "ChessPlatform.Runner"

let private docker up =
    if up then
        Pass
    else
        Fail ("Docker is not running", "Start Docker, then run ./eclair env up.")

let private service facts name =
    if not facts.DockerUp then
        Blocked "docker"
    else
        match Map.tryFind name facts.Services with
        | None -> Fail ($"{name} is not running", "Run ./eclair env up.")
        | Some "healthy" -> Pass
        | Some "starting" -> Fail ($"{name} is still starting", "Run ./eclair env up, which waits until it is healthy.")
        | Some health ->
            Fail (
                $"{name} is {health}",
                $"Read docker compose logs {name}, then run ./eclair env down and ./eclair env up."
            )

let port owner =
    match owner with
    | Free -> Fail ("nothing listens on port 5000", "Run ./eclair run.")
    | Listening (_, command) when isRunner command -> Pass
    | Listening (pid, command) when command.Contains "ControlCenter" ->
        Fail (
            $"port 5000 is taken by pid {pid} ({command}), which is the macOS AirPlay Receiver",
            "Turn off AirPlay Receiver in System Settings > General > AirDrop & Handoff, then run ./eclair run."
        )
    | Listening (pid, command) ->
        Fail (
            $"port 5000 is taken by pid {pid} ({command}), which is not the Runner",
            $"Stop that process (kill {pid}), then run ./eclair run."
        )

let private build facts =
    match facts.RunnerBuiltAt, facts.Port5000 with
    | None, _ -> Fail ("the Runner is not built", "Run ./eclair run, which builds it and starts it.")
    | Some built, Listening (pid, command) when built < facts.SourcesChangedAt && isRunner command ->
        Fail (
            "the running Runner is older than its sources",
            $"Restart it: kill {pid}, then run ./eclair run, which rebuilds it."
        )
    | Some built, _ when built < facts.SourcesChangedAt ->
        Fail ("the Runner build is older than its sources", "Run ./eclair run, which rebuilds it and starts it.")
    | Some _, _ -> Pass

let classify facts = [
    {
        Name = "docker"
        Status = docker facts.DockerUp
    }
    {
        Name = "postgres"
        Status = service facts "postgres"
    }
    {
        Name = "redis"
        Status = service facts "redis"
    }
    {
        Name = "port5000"
        Status = port facts.Port5000
    }
    { Name = "build"; Status = build facts }
]
