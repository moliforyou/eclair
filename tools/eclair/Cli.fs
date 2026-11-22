module Cli

type TestScope =
    | UnitSuites
    | InfraSuites
    | AllSuites

type Command =
    | Build
    | Test of TestScope
    | Format of check: bool
    | Lint
    | Analyze
    | Layers of projects: string list
    | EnvUp
    | EnvDown
    | Run
    | Setup

[<NoEquality; NoComparison>]
type Entry = {
    Name: string
    Usage: string
    Summary: string
    Details: string
    Options: (string * string) list
    Example: string
    Parse: string list -> Command option
}

let private noArgs command args =
    match args with
    | [] -> Some command
    | _ -> None

let entries = [
    {
        Name = "build"
        Usage = "build"
        Summary = "Build the solution."
        Details = "Runs dotnet build ChessPlatform.slnx. Warnings are errors."
        Options = []
        Example = "./eclair build"
        Parse = noArgs Build
    }
    {
        Name = "test"
        Usage = "test [--unit|--infra]"
        Summary = "Build once, then run the test suites in parallel."
        Details =
            "Builds the solution, then runs each tests/*/*.fsproj with dotnet test --no-build. Each suite prints its output as one block when it finishes. With no flag, runs the unit and infra suites."
        Options = [
            "--unit", "Run only the unit suites: every suite except tests/Infra.Tests."
            "--infra", "Run only tests/Infra.Tests. It needs Docker running (Testcontainers)."
        ]
        Example = "./eclair test --unit"
        Parse =
            fun args ->
                match List.distinct (List.sort args) with
                | []
                | [ "--infra"; "--unit" ] -> Some (Test AllSuites)
                | [ "--unit" ] -> Some (Test UnitSuites)
                | [ "--infra" ] -> Some (Test InfraSuites)
                | _ -> None
    }
    {
        Name = "format"
        Usage = "format [--check]"
        Summary = "Format the F# in src, tests and tools with Fantomas."
        Details = "Runs dotnet fantomas src tests tools."
        Options = [ "--check", "Change nothing. Exit 1 if any file needs formatting." ]
        Example = "./eclair format --check"
        Parse =
            function
            | [] -> Some (Format false)
            | [ "--check" ] -> Some (Format true)
            | _ -> None
    }
    {
        Name = "lint"
        Usage = "lint"
        Summary = "Check formatting and layer rules without changing files."
        Details = "Runs format --check, then layers."
        Options = []
        Example = "./eclair lint"
        Parse = noArgs Lint
    }
    {
        Name = "analyze"
        Usage = "analyze"
        Summary = "Build, then run the F# analyzers over every project."
        Details =
            $"Runs fsharp-analyzers on every .fsproj under src, tools and tests, with the FSharpAnalyzer packages listed in {Analyzers.versionsFile}. Any finding fails the run, whatever its level, and so does a project that does not type check."
        Options = []
        Example = "./eclair analyze"
        Parse = noArgs Analyze
    }
    {
        Name = "layers"
        Usage = "layers [fsproj...]"
        Summary = "Check project references and packages against the layer rules."
        Details =
            $"With no arguments, checks every .fsproj under src and tools. Projects under tests are exempt. The rules live in {LayerRules.ruleFile}. Exits 1 if any project breaks them."
        Options = [ "fsproj...", "Check only these project files." ]
        Example = "./eclair layers src/backend/Domain/ChessPlatform.Domain.fsproj"
        Parse =
            fun args ->
                if args |> List.exists _.StartsWith('-') then
                    None
                else
                    Some (Layers args)
    }
    {
        Name = "env"
        Usage = "env up|down"
        Summary = "Start or stop Postgres and Redis."
        Details =
            "up runs docker compose up -d --wait, which returns once both are healthy. down runs docker compose down."
        Options = []
        Example = "./eclair env up"
        Parse =
            function
            | [ "up" ] -> Some EnvUp
            | [ "down" ] -> Some EnvDown
            | _ -> None
    }
    {
        Name = "run"
        Usage = "run"
        Summary = "Run the backend (WebApi and BackgroundWorkers) on :5000 in the foreground."
        Details =
            "Runs dotnet run --project src/backend/ChessPlatform.Runner. It needs Postgres and Redis, so run ./eclair env up first. Stop it with Ctrl-C."
        Options = []
        Example = "./eclair run"
        Parse = noArgs Run
    }
    {
        Name = "setup"
        Usage = "setup"
        Summary = "Restore the dotnet tools and turn on the repo's git hooks."
        Details = "Runs dotnet tool restore, then git config core.hooksPath .githooks. Run it once after cloning."
        Options = []
        Example = "./eclair setup"
        Parse = noArgs Setup
    }
]

let globalOptions = [
    "--json", "End with one JSON summary line on stdout. All other output goes to stderr."
    "--help, -h", "Show help for eclair or for one command."
]

[<NoEquality; NoComparison>]
type Request =
    | Execute of Command * json: bool
    | Help of Entry option
    | Misuse of message: string

let private isHelp arg = arg = "--help" || arg = "-h"

let parse (argv: string list) =
    let args = argv |> List.filter (fun arg -> arg <> "--json" && not (isHelp arg))

    let find name =
        entries |> List.tryFind (fun entry -> entry.Name = name)

    let unknown name =
        Misuse $"unknown command \"{name}\". Run ./eclair --help to list the commands."

    match args with
    | []
    | [ "help" ] -> Help None
    | [ "help"; name ] -> find name |> Option.map (Some >> Help) |> Option.defaultValue (unknown name)
    | name :: rest ->
        match find name with
        | None -> unknown name
        | Some entry when List.exists isHelp argv -> Help (Some entry)
        | Some entry ->
            match entry.Parse rest with
            | Some command -> Execute (command, List.contains "--json" argv)
            | None ->
                Misuse
                    $"""{name} does not take "{String.concat " " rest}". Run ./eclair {name} --help to see its usage."""

let private table rows =
    let width = rows |> List.map (fst >> String.length) |> List.max

    rows
    |> List.map (fun (name: string, text) -> $"  {name.PadRight width}  {text}")

let overview () =
    [
        "eclair: one command for building, testing and running eclair."
        ""
        "Usage: ./eclair <command> [options]"
        ""
        "Commands:"
        yield! table (entries |> List.map (fun entry -> entry.Name, entry.Summary))
        ""
        "Global options:"
        yield! table globalOptions
        ""
        "Example: ./eclair test --unit"
        "Run ./eclair <command> --help for a command's options and an example."
    ]
    |> String.concat "\n"

let detail entry =
    [
        $"Usage: ./eclair {entry.Usage}"
        ""
        entry.Summary
        entry.Details
        ""
        "Options:"
        yield! table (entry.Options @ globalOptions)
        ""
        $"Example: {entry.Example}"
    ]
    |> String.concat "\n"
