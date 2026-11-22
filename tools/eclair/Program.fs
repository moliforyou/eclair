module Program

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text.Json
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
    let paths = analyzers |> List.map (Analyzers.path root)

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
        let steps =
            runPlan env json (argv |> List.filter ((<>) "--json") |> String.concat " ")

        let build =
            step env "Fix the errors above, then rerun." "dotnet build ChessPlatform.slnx"

        let formatCheck =
            step env "Run ./eclair format, then stage the changes." "dotnet fantomas --check src tests tools"

        let format =
            step env "Fix the syntax error Fantomas names, then rerun." "dotnet fantomas src tests tools"

        let runner = "dotnet run --project src/backend/ChessPlatform.Runner"

        let runnerNeeds =
            "The Runner needs Postgres and Redis (./eclair env up) and a free port 5000."

        match command with
        | Layers projects -> layers env json projects
        | Build -> steps [ [ build ] ]
        | Test scope -> steps [ [ build ]; suites env scope ]
        | Format false -> steps [ [ format ] ]
        | Format true -> steps [ [ formatCheck ] ]
        | Lint -> steps [ [ formatCheck ]; [ layersStep env ] ]
        | Analyze -> steps [ [ build ]; [ analyzeStep env ] ]
        | EnvUp -> steps [ [ step env "Start Docker, then rerun." "docker compose up -d --wait" ] ]
        | EnvDown -> steps [ [ step env "Start Docker, then rerun." "docker compose down" ] ]
        | Run -> steps [ [ step env runnerNeeds runner ] ]
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
