module DoctorTests

open System
open Expecto
open Doctor

let builtAt = DateTime (2026, 11, 23, 12, 0, 0, DateTimeKind.Utc)

let runner =
    "dotnet /repo/src/backend/ChessPlatform.Runner/bin/Debug/net10.0/ChessPlatform.Runner.dll"

let healthy = {
    RunnerDir = "/repo/src/backend/ChessPlatform.Runner/"
    DockerUp = true
    Services = Map [ "postgres", "healthy"; "redis", "healthy" ]
    Port5000 = Listening (4242, runner)
    RunnerBuiltAt = Some builtAt
    SourcesChangedAt = builtAt.AddMinutes -5.0
}

let statusOf name facts =
    (classify facts |> List.find (fun check -> check.Name = name)).Status

let failure name facts =
    match statusOf name facts with
    | Fail (problem, fix) -> problem, fix
    | other -> failtestf $"{name}: expected a failure, got %A{other}"

[<Tests>]
let tests =
    testList "doctor" [
        test "a healthy machine passes five checks, in order" {
            let checks = classify healthy

            Expect.equal
                (checks |> List.map _.Name)
                [ "docker"; "postgres"; "redis"; "port5000"; "build" ]
                "check names"

            Expect.all checks (fun check -> check.Status = Pass) "every check passes"
        }

        test "Docker down fails docker and blocks postgres and redis" {
            let facts = {
                healthy with
                    DockerUp = false
                    Services = Map.empty
            }

            let _, fix = failure "docker" facts

            Expect.stringContains fix "Start Docker" "says to start Docker"
            Expect.equal (statusOf "postgres" facts) (Blocked "docker") "postgres"
            Expect.equal (statusOf "redis" facts) (Blocked "docker") "redis"
        }

        test "an unhealthy redis fails and points at its logs" {
            let facts = {
                healthy with
                    Services = Map [ "postgres", "healthy"; "redis", "unhealthy" ]
            }

            let problem, fix = failure "redis" facts

            Expect.stringContains problem "redis is unhealthy" "names the state"
            Expect.stringContains fix "docker compose logs redis" "says where to look"
            Expect.equal (statusOf "postgres" facts) Pass "postgres"
        }

        test "a stopped postgres names ./eclair env up" {
            let facts = {
                healthy with
                    Services = Map [ "redis", "healthy" ]
            }

            let problem, fix = failure "postgres" facts

            Expect.stringContains problem "postgres is not running" "names the service"
            Expect.stringContains fix "./eclair env up" "says what to run"
        }

        test "a free port 5000 names ./eclair run" {
            let problem, fix = failure "port5000" { healthy with Port5000 = Free }

            Expect.stringContains problem "nothing listens on port 5000" "names the problem"
            Expect.stringContains fix "./eclair run" "says what to run"
        }

        test "a foreign owner of port 5000 is named by pid and command" {
            let facts = {
                healthy with
                    Port5000 = Listening (777, "python3 -m http.server 5000")
            }

            let problem, fix = failure "port5000" facts

            Expect.stringContains problem "pid 777 (python3 -m http.server 5000)" "names the owner"
            Expect.stringContains fix "kill 777" "says how to free the port"
        }

        test "a Runner from another checkout does not count as ours" {
            let facts = {
                healthy with
                    Port5000 =
                        Listening (
                            5151,
                            "dotnet /repo-p4/src/backend/ChessPlatform.Runner/bin/Debug/net10.0/ChessPlatform.Runner.dll"
                        )
            }

            let problem, fix = failure "port5000" facts
            Expect.stringContains problem "another checkout" "problem"
            Expect.stringContains fix "kill 5151" "fix"
        }

        test "the AirPlay Receiver on port 5000 is called out" {
            let facts = {
                healthy with
                    Port5000 =
                        Listening (512, "/System/Library/CoreServices/ControlCenter.app/Contents/MacOS/ControlCenter")
            }

            let problem, fix = failure "port5000" facts

            Expect.stringContains problem "AirPlay Receiver" "names AirPlay"
            Expect.stringContains fix "Turn off AirPlay Receiver" "says how to free the port"
        }

        test "a build older than its sources names ./eclair run" {
            let facts = {
                healthy with
                    Port5000 = Free
                    SourcesChangedAt = builtAt.AddSeconds 1.0
            }

            let problem, fix = failure "build" facts

            Expect.stringContains problem "older than its sources" "names the problem"
            Expect.stringContains fix "./eclair run" "says what to run"
        }

        test "a running Runner older than its sources says to restart it" {
            let _, fix =
                failure "build" {
                    healthy with
                        SourcesChangedAt = builtAt.AddSeconds 1.0
                }

            Expect.stringContains fix "kill 4242" "names the Runner to stop"
            Expect.stringContains fix "./eclair run" "says what to run"
        }

        test "no build fails and names ./eclair run" {
            let problem, fix = failure "build" { healthy with RunnerBuiltAt = None }

            Expect.stringContains problem "not built" "names the problem"
            Expect.stringContains fix "./eclair run" "says what to run"
        }
    ]

[<Tests>]
let reportTests =
    testList "doctor report" [
        test "--json gives each check's status, problem and fix, and exits 1 on a failure" {
            use out = new System.IO.StringWriter ()

            let exitCode =
                Program.doctorReport
                    { Root = "."; Out = out; Err = out }
                    true
                    "env doctor"
                    (classify {
                        healthy with
                            DockerUp = false
                            Services = Map.empty
                            Port5000 = Free
                    })

            use summary = System.Text.Json.JsonDocument.Parse (string out)

            let checks = [
                for check in summary.RootElement.GetProperty("checks").EnumerateArray () ->
                    check.GetProperty("name").GetString (), check.GetProperty("status").GetString ()
            ]

            let port5000 =
                summary.RootElement.GetProperty("checks").EnumerateArray ()
                |> Seq.find (fun check -> check.GetProperty("name").GetString () = "port5000")

            Expect.equal exitCode 1 "exit code"
            Expect.equal (summary.RootElement.GetProperty("exitCode").GetInt32 ()) 1 "exit code in JSON"

            Expect.equal
                checks
                [
                    "docker", "fail"
                    "postgres", "blocked"
                    "redis", "blocked"
                    "port5000", "fail"
                    "build", "pass"
                ]
                "statuses"

            Expect.stringContains (port5000.GetProperty("fix").GetString ()) "./eclair run" "port5000 fix"
        }

        test "a healthy machine exits 0" {
            use out = new System.IO.StringWriter ()

            let exitCode =
                Program.doctorReport { Root = "."; Out = out; Err = out } false "env doctor" (classify healthy)

            Expect.equal exitCode 0 (string out)
        }
    ]
