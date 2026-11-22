module Tests

open System.IO
open System.Text.Json
open Expecto

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, "..", ".."))

let layers args =
    use out = new StringWriter ()
    use err = new StringWriter ()

    let exitCode =
        Program.run
            {
                Root = repoRoot
                Out = out
                Err = err
            }
            ("layers" :: args)

    exitCode, string out, string err

let fixture (references: string list) (packages: string list) =
    let items tag values =
        values
        |> List.map (fun value -> $"""<{tag} Include="{value}" />""")
        |> String.concat "\n"

    $"""<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
{items "ProjectReference" references}
  </ItemGroup>
  <ItemGroup>
{items "PackageReference" packages}
  </ItemGroup>
</Project>"""

let checkFixture fileName references packages flags =
    let dir = Directory.CreateTempSubdirectory "eclair-layers-"

    try
        let path = Path.Combine (dir.FullName, fileName)
        File.WriteAllText (path, fixture references packages)
        layers (flags @ [ path ])
    finally
        dir.Delete true

let domain = @"..\Domain\ChessPlatform.Domain.fsproj"
let application = @"..\Application\ChessPlatform.Application.fsproj"

[<Tests>]
let tests =
    testList "layers" [
        test "an allowed reference passes" {
            let exitCode, out, err =
                checkFixture "ChessPlatform.Application.fsproj" [ domain ] [] []

            Expect.equal exitCode 0 err
            Expect.stringContains out "1 project follows the layer rules" "reports the project"
        }

        test "a forbidden reference fails and names the rule" {
            let exitCode, _, err =
                checkFixture "ChessPlatform.Domain.fsproj" [ application ] [] []

            Expect.equal exitCode 1 "exit code"
            Expect.stringContains err "Domain may not reference Application" "names the reference"
            Expect.stringContains err "The Domain rule allows no project references" "names the rule"
            Expect.stringContains err "Remove the reference" "says what to do"
        }

        test "an unknown project fails and points at the rule table" {
            let exitCode, _, err = checkFixture "ChessPlatform.Mystery.fsproj" [] [] []

            Expect.equal exitCode 1 "exit code"
            Expect.stringContains err "no layer is named Mystery" "names the project"

            Expect.stringContains
                err
                "Add a Mystery case and its rule to the table in tools/eclair/LayerRules.fs"
                "says what to do"
        }

        test "a package-free layer with a package fails" {
            let exitCode, _, err =
                checkFixture "ChessPlatform.Domain.fsproj" [] [ "Newtonsoft.Json" ] []

            Expect.equal exitCode 1 "exit code"
            Expect.stringContains err "Domain may not use the package Newtonsoft.Json" "names the package"
            Expect.stringContains err "The Domain rule allows no packages" "names the rule"
        }

        test "Contracts may use FSharp.SystemTextJson" {
            let exitCode, _, err =
                checkFixture "ChessPlatform.Contracts.fsproj" [ domain ] [ "FSharp.SystemTextJson" ] []

            Expect.equal exitCode 0 err
        }

        test "Contracts may not use another package" {
            let exitCode, _, err =
                checkFixture "ChessPlatform.Contracts.fsproj" [ domain ] [ "Newtonsoft.Json" ] []

            Expect.equal exitCode 1 "exit code"
            Expect.stringContains err "Contracts may not use the package Newtonsoft.Json" "names the package"
            Expect.stringContains err "The Contracts rule allows only FSharp.SystemTextJson" "names the rule"
        }

        test "--json prints each violation" {
            let exitCode, out, _ =
                checkFixture "ChessPlatform.Domain.fsproj" [ application ] [ "Newtonsoft.Json" ] [ "--json" ]

            use summary = JsonDocument.Parse out

            let kinds = [
                for violation in summary.RootElement.GetProperty("violations").EnumerateArray () ->
                    violation.GetProperty("kind").GetString ()
            ]

            Expect.equal exitCode 1 "exit code"
            Expect.equal (summary.RootElement.GetProperty("exitCode").GetInt32 ()) 1 "exit code in JSON"
            Expect.equal kinds [ "ForbiddenReference"; "DisallowedPackage" ] "violation kinds"
        }

        test "every project in the repo follows the rules" {
            let exitCode, _, err = layers []

            Expect.equal exitCode 0 err
        }
    ]

let sarif =
    """{
  "runs": [
    {
      "invocations": [
        {
          "executionSuccessful": false,
          "toolExecutionNotifications": [
            { "level": "error", "message": { "text": "The value or constructor 'x' is not defined." } }
          ]
        }
      ],
      "results": [
        {
          "ruleId": "IONIDE-004",
          "level": "warning",
          "message": { "text": "Use a postfix generic type." },
          "locations": [
            {
              "physicalLocation": {
                "artifactLocation": { "uri": "src/backend/Domain/Game.fs" },
                "region": { "startLine": 12, "startColumn": 5 }
              }
            }
          ]
        }
      ]
    }
  ]
}"""

[<Tests>]
let analyzerTests =
    testList "analyze" [
        test "the declared analyzers parse with exact versions" {
            let declared =
                Analyzers.declared (File.ReadAllText (Path.Combine (repoRoot, Analyzers.versionsFile)))

            Expect.isNonEmpty declared "Directory.Packages.props lists at least one analyzer"

            for analyzer in declared do
                Expect.isFalse (analyzer.Version.Contains '[') $"{analyzer.Package} has its brackets removed"
        }

        test "a SARIF report yields its results and the tool's notifications" {
            let found = Analyzers.findings sarif |> List.map Analyzers.format

            Expect.equal
                found
                [
                    "(no location): error tool: The value or constructor 'x' is not defined."
                    "src/backend/Domain/Game.fs(12,5): warning IONIDE-004: Use a postfix generic type."
                ]
                "findings"
        }

        test "a clean report has no findings" {
            let found =
                Analyzers.findings
                    """{ "runs": [ { "invocations": [ { "executionSuccessful": true } ], "results": [] } ] }"""

            Expect.isEmpty found "no findings"
        }
    ]

[<EntryPoint>]
let main args =
    runTestsWithCLIArgs [] args (testList "eclair" [ tests; analyzerTests ])
