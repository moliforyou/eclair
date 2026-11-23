module CliTests

open Expecto
open Cli

let parsed args =
    match parse args with
    | Execute (command, json) -> Some (command, json)
    | Help _
    | Misuse _ -> None

let parses args expected =
    test (String.concat " " args) { Expect.equal (parsed args) expected "parsed command" }

[<Tests>]
let tests =
    testList "parse" [
        parses [ "env"; "up" ] (Some (EnvUp, false))
        parses [ "env"; "down" ] (Some (EnvDown (false, false), false))
        parses [ "env"; "down"; "--volumes" ] (Some (EnvDown (true, false), false))
        parses [ "env"; "down"; "--dry-run"; "--volumes" ] (Some (EnvDown (true, true), false))
        parses [ "env"; "down"; "--force" ] None
        parses [ "env"; "doctor" ] (Some (EnvDoctor, false))
        parses [ "env"; "doctor"; "--json" ] (Some (EnvDoctor, true))
        parses [ "env"; "doctor"; "--volumes" ] None
        parses [ "run" ] (Some (Run false, false))
        parses [ "run"; "--release" ] (Some (Run true, false))
        parses [ "run"; "--watch" ] None
        parses [ "db"; "reset" ] (Some (DbReset false, false))
        parses [ "db"; "reset"; "--dry-run" ] (Some (DbReset true, false))
        parses [ "db"; "seed" ] None
    ]
