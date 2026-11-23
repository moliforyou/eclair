module Program

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Xml
open Cli
open LayerRules

[<NoComparison>]
type Env = {
    Root: string
    Out: TextWriter
    Err: TextWriter
}

[<NoComparison>]
type Output =
    | Inherit
    | Into of TextWriter

[<NoEquality; NoComparison>]
type Step = {
    Name: string
    Next: string
    Run: Output -> int
}

[<NoEquality; NoComparison>]
type StepResult = {
    Step: Step
    ExitCode: int
    Elapsed: TimeSpan
}

let private forward (writer: TextWriter) (line: string | null) =
    match line with
    | null -> ()
    | line -> lock writer (fun () -> writer.WriteLine line)

let private runProcess env program (args: string list) output =
    let info =
        ProcessStartInfo (program, WorkingDirectory = env.Root, UseShellExecute = false)

    args |> List.iter info.ArgumentList.Add

    match output with
    | Inherit -> ()
    | Into _ ->
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true

    try
        use child = nonNull (Process.Start info)

        match output with
        | Inherit -> ()
        | Into writer ->
            child.OutputDataReceived.Add (fun e -> forward writer e.Data)
            child.ErrorDataReceived.Add (fun e -> forward writer e.Data)
            child.BeginOutputReadLine ()
            child.BeginErrorReadLine ()

        child.WaitForExit ()
        child.ExitCode
    with :? Win32Exception as error ->
        env.Err.WriteLine $"eclair: could not start {program}: {error.Message}. Install it and put it on PATH."
        127

let private capture env program (args: string list) =
    let info =
        ProcessStartInfo (
            program,
            WorkingDirectory = env.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    args |> List.iter info.ArgumentList.Add

    try
        use child = nonNull (Process.Start info)
        let err = child.StandardError.ReadToEndAsync ()
        let out = child.StandardOutput.ReadToEnd ()
        child.WaitForExit ()
        child.ExitCode, out, err.Result
    with :? Win32Exception as error ->
        127, "", $"could not start {program}: {error.Message}. Install it and put it on PATH."

let private step env next (commandLine: string) =
    let words = commandLine.Split ' '

    {
        Name = commandLine
        Next = next
        Run = runProcess env words[0] (List.ofArray words[1..])
    }

let private timed step output =
    let clock = Stopwatch.StartNew ()
    let exitCode = step.Run output

    {
        Step = step
        ExitCode = exitCode
        Elapsed = clock.Elapsed
    }

let private runStage env json stage =
    match stage with
    | [] -> []
    | [ step ] -> [ timed step (if json then Into env.Err else Inherit) ]
    | steps ->
        let log = if json then env.Err else env.Out
        log.WriteLine $"""Running in parallel: {steps |> List.map _.Name |> String.concat ", "}"""

        steps
        |> List.map (fun step ->
            async {
                use buffer = new StringWriter ()
                let result = timed step (Into buffer)

                lock
                    log
                    (fun () ->
                        log.WriteLine $"--- {step.Name}: exit {result.ExitCode} in {result.Elapsed.TotalSeconds:F1} s"
                        log.Write (string buffer)
                    )

                return result
            }
        )
        |> Async.Parallel
        |> Async.RunSynchronously
        |> List.ofArray

let private runPlan env json command plan =
    let results =
        plan
        |> List.fold
            (fun finished stage ->
                if finished |> List.exists (fun result -> result.ExitCode <> 0) then
                    finished
                else
                    finished @ runStage env json stage
            )
            []

    let failures = results |> List.filter (fun result -> result.ExitCode <> 0)

    for failure in failures do
        env.Err.WriteLine
            $"eclair {command}: {failure.Step.Name} failed with exit code {failure.ExitCode}. {failure.Step.Next}"

    let exitCode =
        failures |> List.tryHead |> Option.map _.ExitCode |> Option.defaultValue 0

    if json then
        env.Out.WriteLine (
            JsonSerializer.Serialize {|
                command = command
                steps = [
                    for result in results ->
                        {|
                            name = result.Step.Name
                            exitCode = result.ExitCode
                            durationMs = int64 result.Elapsed.TotalMilliseconds
                        |}
                ]
                exitCode = exitCode
            |}
        )

    exitCode

let private display env (path: string) =
    let relative = Path.GetRelativePath (env.Root, path)
    if relative.StartsWith ".." then path else relative

let rec private findProjects dir = [
    yield! Directory.GetFiles (dir, "*.fsproj") |> Array.sort

    for sub in Directory.GetDirectories dir |> Array.sort do
        match Path.GetFileName sub with
        | "bin"
        | "obj" -> ()
        | _ -> yield! findProjects sub
]

let private projectsToCheck env args =
    match args with
    | [] ->
        Ok (
            findProjects (Path.Combine (env.Root, "src"))
            @ findProjects (Path.Combine (env.Root, "tools"))
        )
    | paths ->
        let fullPaths = paths |> List.map Path.GetFullPath

        match
            fullPaths
            |> List.tryFind (fun path -> not (path.EndsWith ".fsproj" && File.Exists path))
        with
        | Some path ->
            Error
                $"layers: {path} is not an existing .fsproj file. Pass project files, or no arguments to check every project."
        | None ->
            Ok (
                fullPaths
                |> List.filter (fun path -> not ((display env path).StartsWith "tests/"))
            )

let private readProject env path =
    try
        Ok (parseProject (display env path) (File.ReadAllText path))
    with :? XmlException as error ->
        Error $"layers: {display env path} is not valid XML ({error.Message}). Fix the project file."

let private kind violation =
    match violation with
    | UnknownProject _ -> "UnknownProject"
    | ForbiddenReference _ -> "ForbiddenReference"
    | DisallowedPackage _ -> "DisallowedPackage"

let private readProjects env paths =
    let results = paths |> List.map (readProject env)

    match
        results
        |> List.tryPick (
            function
            | Error problem -> Some problem
            | Ok _ -> None
        )
    with
    | Some problem -> Error problem
    | None -> Ok (results |> List.choose Result.toOption)

let layers env json args =
    match projectsToCheck env args |> Result.bind (readProjects env) with
    | Error problem ->
        env.Err.WriteLine problem
        2
    | Ok projects ->
        let violations = projects |> List.collect check
        let exitCode = if violations.IsEmpty then 0 else 1

        if json then
            env.Out.WriteLine (
                JsonSerializer.Serialize {|
                    command = "layers"
                    violations = [ for v in violations -> {| kind = kind v; message = message v |} ]
                    exitCode = exitCode
                |}
            )
        else
            for violation in violations do
                env.Err.WriteLine $"layers: {message violation}"

            if violations.IsEmpty then
                let projectsFollow =
                    if projects.Length = 1 then
                        "project follows"
                    else
                        "projects follow"

                env.Out.WriteLine $"layers: {projects.Length} {projectsFollow} the layer rules."

        exitCode

let private analyze env output =
    let log =
        match output with
        | Inherit -> env.Err
        | Into writer -> writer

    let read relative =
        File.ReadAllText (Path.Combine (env.Root, relative))

    let analyzers = Analyzers.declared (read Analyzers.versionsFile)
    let root = Analyzers.packagesRoot ()

    let local =
        let dir = Path.Combine (env.Root, Analyzers.localAnalyzers)

        if Directory.Exists dir then
            Ok dir
        else
            Error (Analyzers.NotBuilt dir)

    let paths = (analyzers |> List.map (Analyzers.path root)) @ [ local ]

    let problem =
        if analyzers.IsEmpty then
            Some Analyzers.NoAnalyzers
        else
            paths
            |> List.tryPick (
                function
                | Error problem -> Some problem
                | Ok _ -> None
            )

    match problem with
    | Some problem ->
        log.WriteLine (Analyzers.describe problem)
        2
    | None ->
        let projects =
            [ "src"; "tools"; "tests" ]
            |> List.collect (fun dir -> findProjects (Path.Combine (env.Root, dir)))
            |> List.map (display env)

        let report =
            Path.Combine (Path.GetTempPath (), $"eclair-analyze-{Environment.ProcessId}.sarif")

        let args = [
            "fsharp-analyzers"
            "--project"
            yield! projects
            "--analyzers-path"
            yield! List.choose Result.toOption paths
            "--code-root"
            env.Root
            "--report"
            report
            "--fail-on-type-errors"
        ]

        let exitCode = runProcess env "dotnet" args output

        if not (File.Exists report) then
            log.WriteLine
                "analyze: fsharp-analyzers wrote no report, so nothing was checked. Its output above says why."

            if exitCode = 0 then 1 else exitCode
        else
            let blocking, notes =
                Analyzers.findings (File.ReadAllText report) |> List.partition Analyzers.blocks

            File.Delete report

            for finding in blocking do
                log.WriteLine (Analyzers.format finding)

            if exitCode <> 0 then
                exitCode
            elif not blocking.IsEmpty then
                1
            else
                log.WriteLine
                    $"analyze: {projects.Length} projects, no warnings or errors. {notes.Length} hints are not enforced."

                0

let private infraSuites = set [ "Infra.Tests" ]

let private suites env scope = [
    for dir in Directory.GetDirectories (Path.Combine (env.Root, "tests")) |> Array.sort do
        let isInfra = infraSuites.Contains (nonNull (Path.GetFileName dir))

        let next =
            if isInfra then
                "It needs Docker running (Testcontainers). Start Docker, or run ./eclair test --unit to skip it."
            else
                "Fix the failing tests above, then rerun ./eclair test --unit."

        let wanted =
            match scope with
            | AllSuites -> true
            | UnitSuites -> not isInfra
            | InfraSuites -> isInfra

        if wanted then
            for project in Directory.GetFiles (dir, "*.fsproj") do
                {
                    step env next $"dotnet test {display env project} --no-build" with
                        Name = display env dir
                }
]

let private layersStep env = {
    Name = "layers"
    Next = "Each layers message above says what to change."
    Run =
        function
        | Inherit -> layers env false []
        | Into writer -> layers { env with Out = writer; Err = writer } false []
}

let private analyzeStep env = {
    Name = "fsharp-analyzers"
    Next = "Fix each finding above. The analyzer packages are listed in Directory.Packages.props."
    Run = analyze env
}

let private runnerDll env configuration =
    Path.Combine (env.Root, "src/backend/ChessPlatform.Runner/bin", configuration, "net10.0/ChessPlatform.Runner.dll")

let private serviceHealth (row: JsonElement) =
    let text (name: string) =
        match row.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> nonNull (value.GetString ())
        | _ -> ""

    let health = text "Health"
    text "Service", (if health.Length = 0 then text "State" else health)

let private composeServices env =
    match capture env "docker" [ "compose"; "ps"; "--format"; "json" ] with
    | 0, out, _ when out.TrimStart().StartsWith '[' ->
        use rows = JsonDocument.Parse out
        rows.RootElement.EnumerateArray () |> Seq.map serviceHealth |> Map.ofSeq
    | 0, out, _ ->
        out.Split ('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Seq.map (fun line ->
            use row = JsonDocument.Parse line
            serviceHealth row.RootElement
        )
        |> Map.ofSeq
    | _ -> Map.empty

let private portOwner env =
    let _, out, _ = capture env "lsof" [ "-nP"; "-iTCP:5000"; "-sTCP:LISTEN"; "-Fp" ]

    let listeners =
        out.Split '\n'
        |> Seq.filter _.StartsWith('p')
        |> Seq.choose (fun line ->
            match Int32.TryParse line[1..] with
            | true, pid -> Some pid
            | _ -> None
        )
        |> Seq.distinct
        |> Seq.map (fun pid ->
            let _, command, _ = capture env "ps" [ "-o"; "command="; "-p"; string pid ]
            pid, command.Trim ()
        )
        |> List.ofSeq

    listeners
    |> List.tryFind (snd >> Doctor.isRunner)
    |> Option.orElse (List.tryHead listeners)
    |> Option.map Doctor.Listening
    |> Option.defaultValue Doctor.Free

let rec private newestSource dir =
    let files =
        Directory.GetFiles dir
        |> Seq.filter (fun file -> file.EndsWith ".fs" || file.EndsWith ".fsi" || file.EndsWith ".fsproj")
        |> Seq.map File.GetLastWriteTimeUtc

    let subdirs =
        Directory.GetDirectories dir
        |> Seq.filter (fun sub -> Path.GetFileName sub <> "bin" && Path.GetFileName sub <> "obj")
        |> Seq.map newestSource

    Seq.append files subdirs |> Seq.fold max DateTime.MinValue

let private startedAt pid =
    try
        Some ((Process.GetProcessById pid).StartTime.ToUniversalTime ())
    with
    | :? ArgumentException
    | :? InvalidOperationException -> None

let private facts env : Doctor.Facts =
    let dockerUp =
        let exitCode, _, _ =
            capture env "docker" [ "info"; "--format"; "{{.ServerVersion}}" ]

        exitCode = 0

    let port5000 = portOwner env

    let built =
        [ "Debug"; "Release" ]
        |> List.map (runnerDll env)
        |> List.filter File.Exists
        |> List.map File.GetLastWriteTimeUtc
        |> List.sortDescending
        |> List.tryHead

    let runnerStartedAt =
        match port5000 with
        | Doctor.Listening (pid, command) when Doctor.isRunner command -> startedAt pid
        | _ -> None

    {
        DockerUp = dockerUp
        Services = if dockerUp then composeServices env else Map.empty
        Port5000 = port5000
        RunnerBuiltAt = Option.map2 min built runnerStartedAt |> Option.orElse built
        SourcesChangedAt = newestSource (Path.Combine (env.Root, "src/backend"))
    }

let doctorReport env json command (checks: Doctor.Check list) =
    let failed =
        checks
        |> List.exists (fun check ->
            match check.Status with
            | Doctor.Fail _ -> true
            | _ -> false
        )

    let exitCode = if failed then 1 else 0

    let describe (check: Doctor.Check) =
        match check.Status with
        | Doctor.Pass -> "pass", None, None
        | Doctor.Fail (problem, fix) -> "fail", Some problem, Some fix
        | Doctor.Blocked by -> "blocked", Some $"not checked, because the {by} check failed", None

    if json then
        env.Out.WriteLine (
            JsonSerializer.Serialize {|
                command = command
                checks = [
                    for check in checks ->
                        let status, problem, fix = describe check

                        {|
                            name = check.Name
                            status = status
                            problem = problem
                            fix = fix
                        |}
                ]
                exitCode = exitCode
            |}
        )
    else
        for check in checks do
            match describe check with
            | status, Some problem, Some fix -> env.Out.WriteLine $"{status, -8}{check.Name, -10}{problem}. {fix}"
            | status, Some problem, None -> env.Out.WriteLine $"{status, -8}{check.Name, -10}{problem}."
            | status, _, _ -> env.Out.WriteLine $"{status, -8}{check.Name}"

    exitCode

type private Readiness =
    | Ready
    | Exited
    | Answered of status: int
    | TimedOut

let private runnerLog = ".eclair/runner.log"
let private runnerPid = ".eclair/runner.pid"

let private alive pid =
    try
        use child = Process.GetProcessById pid
        not child.HasExited
    with :? ArgumentException ->
        false

let private startDetached env dll =
    Directory.CreateDirectory (Path.Combine (env.Root, ".eclair")) |> ignore

    let exitCode, out, err =
        capture env "/bin/sh" [
            "-c"
            $"set -m; \"$@\" >{runnerLog} 2>&1 </dev/null & echo $!"
            "sh"
            "dotnet"
            dll
        ]

    match exitCode, Int32.TryParse (out.Trim ()) with
    | 0, (true, pid) ->
        File.WriteAllText (Path.Combine (env.Root, runnerPid), string pid)
        Ok pid
    | _ -> Error err

let private waitUntilReady pid =
    use http = new HttpClient (Timeout = TimeSpan.FromSeconds 2.0)
    let clock = Stopwatch.StartNew ()

    let answer () =
        try
            use response =
                http.GetAsync("http://localhost:5000/api/games/__probe").GetAwaiter().GetResult ()

            Some (int response.StatusCode)
        with
        | :? HttpRequestException
        | :? OperationCanceledException -> None

    let rec poll () =
        match answer () with
        | Some 404 -> Ready
        | Some status -> Answered status
        | None when not (alive pid) -> Exited
        | None when clock.Elapsed > TimeSpan.FromSeconds 30.0 -> TimedOut
        | None ->
            Thread.Sleep 25
            poll ()

    poll ()

let private stop env pid =
    try
        use child = Process.GetProcessById pid
        child.Kill ()
        child.WaitForExit ()
    with
    | :? ArgumentException
    | :? InvalidOperationException -> ()

    File.Delete (Path.Combine (env.Root, runnerPid))

let private logTail env =
    let path = Path.Combine (env.Root, runnerLog)

    if File.Exists path then
        let lines = File.ReadAllLines path
        lines[max 0 (lines.Length - 30) ..] |> List.ofArray
    else
        []

let private startRunner env json (command: string) release =
    let clock = Stopwatch.StartNew ()

    let finish (outcome: Result<int, string * string>) =
        let failure =
            match outcome with
            | Ok _ -> None
            | Error failure -> Some failure

        if json then
            env.Out.WriteLine (
                JsonSerializer.Serialize {|
                    command = command
                    pid = Result.toOption outcome
                    log = runnerLog
                    problem = Option.map fst failure
                    fix = Option.map snd failure
                    durationMs = int64 clock.Elapsed.TotalMilliseconds
                    exitCode = if failure.IsSome then 1 else 0
                |}
            )

        match failure with
        | Some (problem, fix) ->
            env.Err.WriteLine $"run: {problem}. {fix}"
            1
        | None -> 0

    let say (message: string) =
        if not json then
            env.Out.WriteLine $"run: {message}"

    let failWithLog problem fix =
        for line in logTail env do
            (if json then env.Err else env.Out).WriteLine line

        finish (Error (problem, $"{fix} The end of {runnerLog} is above."))

    let owner = portOwner env

    match owner, Doctor.port owner with
    | Doctor.Listening (pid, _), Doctor.Pass ->
        say
            $"the Runner is already running on http://localhost:5000 (pid {pid}). To restart it, kill {pid}, then run ./eclair run."

        finish (Ok pid)
    | Doctor.Listening _, Doctor.Fail (problem, fix) -> finish (Error (problem, fix))
    | _ ->
        let configuration = if release then "Release" else "Debug"

        let built =
            runProcess
                env
                "dotnet"
                [
                    "build"
                    "src/backend/ChessPlatform.Runner"
                    "-c"
                    configuration
                    "-nologo"
                    "-v"
                    "q"
                ]
                (if json then Into env.Err else Inherit)

        if built <> 0 then
            finish (Error ("the Runner does not build", "Fix the errors above, then rerun."))
        else
            match startDetached env (runnerDll env configuration) with
            | Error problem -> finish (Error ($"could not start the Runner: {problem.Trim ()}", "Rerun."))
            | Ok pid ->
                match waitUntilReady pid with
                | Ready ->
                    say
                        $"the Runner is up on http://localhost:5000 after {clock.Elapsed.TotalSeconds:F1} s (pid {pid}). Its log is {runnerLog}. Stop it with kill {pid}."

                    finish (Ok pid)
                | Exited ->
                    File.Delete (Path.Combine (env.Root, runnerPid))

                    failWithLog
                        "the Runner exited before it served port 5000"
                        "It needs Postgres and Redis, so run ./eclair env doctor to see what is missing."
                | Answered status ->
                    stop env pid

                    failWithLog
                        $"the Runner answered {status} instead of 404 on /api/games/__probe, so eclair stopped it"
                        "Run ./eclair env doctor to check Postgres and Redis."
                | TimedOut ->
                    stop env pid

                    failWithLog
                        "the Runner did not serve port 5000 within 30 s, so eclair stopped it"
                        "Run ./eclair env doctor."

let run env argv =
    match parse argv with
    | Help None ->
        env.Out.WriteLine (overview ())
        0
    | Help (Some entry) ->
        env.Out.WriteLine (detail entry)
        0
    | Misuse problem ->
        env.Err.WriteLine $"eclair: {problem}"
        2
    | Execute (command, json) ->
        let name = argv |> List.filter ((<>) "--json") |> String.concat " "
        let steps = runPlan env json name

        let build =
            step env "Fix the errors above, then rerun." "dotnet build ChessPlatform.slnx"

        let formatCheck =
            step env "Run ./eclair format, then stage the changes." "dotnet fantomas --check src tests tools"

        let format =
            step env "Fix the syntax error Fantomas names, then rerun." "dotnet fantomas src tests tools"

        match command with
        | Layers projects -> layers env json projects
        | Build -> steps [ [ build ] ]
        | Test scope -> steps [ [ build ]; suites env scope ]
        | Format false -> steps [ [ format ] ]
        | Format true -> steps [ [ formatCheck ] ]
        | Lint -> steps [ [ formatCheck ]; [ layersStep env ] ]
        | Analyze -> steps [ [ build ]; [ analyzeStep env ] ]
        | EnvUp -> steps [ [ step env "Start Docker, then rerun." "docker compose up -d --wait" ] ]
        | EnvDown (volumes, dryRun) ->
            let flags =
                (if dryRun then " --dry-run" else "")
                + " down"
                + (if volumes then " -v" else "")

            let exitCode =
                steps [ [ step env "Start Docker, then rerun." $"docker compose{flags}" ] ]

            if dryRun && exitCode = 0 then
                env.Err.WriteLine "env down: dry run, so nothing was stopped or removed. Drop --dry-run to do it."

            exitCode
        | EnvDoctor -> doctorReport env json name (Doctor.classify (facts env))
        | Run release -> startRunner env json name release
        | Setup ->
            steps [
                [ step env "Check network access to NuGet, then rerun." "dotnet tool restore" ]
                [
                    step env "Run ./eclair setup inside the git checkout." "git config core.hooksPath .githooks"
                ]
            ]

let rec private findRoot (dir: DirectoryInfo | null) =
    match dir with
    | null -> None
    | dir when File.Exists (Path.Combine (dir.FullName, "ChessPlatform.slnx")) -> Some dir.FullName
    | dir -> findRoot dir.Parent

[<EntryPoint>]
let main argv =
    // The terminal sends Ctrl-C to the child too; eclair stays alive to report its exit.
    Console.CancelKeyPress.Add (fun e -> e.Cancel <- true)

    match findRoot (DirectoryInfo AppContext.BaseDirectory) with
    | None ->
        eprintfn $"eclair: no ChessPlatform.slnx above {AppContext.BaseDirectory}. Run eclair from its repo checkout."
        2
    | Some root ->
        run
            {
                Root = root
                Out = Console.Out
                Err = Console.Error
            }
            (List.ofArray argv)
