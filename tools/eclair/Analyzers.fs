module Analyzers

open System
open System.IO
open System.Text.Json
open System.Xml.Linq

let eclairProject = "tools/eclair/Eclair.fsproj"
let versionsFile = "Directory.Packages.props"

let private elements name (xml: string) =
    XDocument.Parse(xml).Descendants ()
    |> Seq.filter (fun element -> element.Name.LocalName = name)

let private attribute name (element: XElement) =
    element.Attribute (XName.Get name) |> Option.ofObj |> Option.map _.Value

/// The analyzer packages: PackageReference items in Eclair.fsproj marked FSharpAnalyzer="true".
let packages projectXml =
    elements "PackageReference" projectXml
    |> Seq.filter (fun element -> attribute "FSharpAnalyzer" element = Some "true")
    |> Seq.choose (attribute "Include")
    |> List.ofSeq

let versions propsXml =
    elements "PackageVersion" propsXml
    |> Seq.choose (fun element ->
        match attribute "Include" element, attribute "Version" element with
        | Some name, Some version -> Some (name, version)
        | _ -> None
    )
    |> Map.ofSeq

type Problem =
    | NoVersion of package: string
    | NotRestored of package: string * path: string

let packagesRoot () =
    match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
    | null
    | "" -> Path.Combine (Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")
    | path -> path

/// Where the tool should look for one package's analyzer assemblies.
let path root (versions: Map<string, string>) (package: string) =
    match Map.tryFind package versions with
    | None -> Error (NoVersion package)
    | Some version ->
        let dir = Path.Combine (root, package.ToLowerInvariant (), version.ToLowerInvariant ())
        let fs = Path.Combine (dir, "analyzers", "dotnet", "fs")

        if Directory.Exists fs then Ok fs
        elif Directory.Exists dir then Ok dir
        else Error (NotRestored (package, dir))

let describe problem =
    match problem with
    | NoVersion package ->
        $"analyze: {package} has no PackageVersion in {versionsFile}. Add one next to the other analyzers."
    | NotRestored (package, dir) ->
        $"analyze: {package} is not restored at {dir}. Run ./eclair build first, which restores it."

type Finding = {
    Rule: string
    Level: string
    Message: string
    Location: string
}

let private property (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value -> Some value
    | false, _ -> None

let private text name element =
    property name element
    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
    |> Option.bind (fun value -> Option.ofObj (value.GetString ()))

let private number name element =
    property name element
    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Number)
    |> Option.map (fun value -> value.GetInt32 ())

let private items name element =
    match property name element with
    | Some value when value.ValueKind = JsonValueKind.Array -> [ for item in value.EnumerateArray () -> item ]
    | _ -> []

let private location element =
    match items "locations" element with
    | [] -> "(no location)"
    | first :: _ ->
        let physical = property "physicalLocation" first
        let uri = physical |> Option.bind (property "artifactLocation") |> Option.bind (text "uri")
        let region = physical |> Option.bind (property "region")
        let line = region |> Option.bind (number "startLine")
        let column = region |> Option.bind (number "startColumn")

        match uri, line, column with
        | None, _, _ -> "(no location)"
        | Some uri, None, _ -> uri
        | Some uri, Some line, None -> $"{uri}({line})"
        | Some uri, Some line, Some column -> $"{uri}({line},{column})"

let private finding rule element = {
    Rule = text "ruleId" element |> Option.defaultValue rule
    Level = text "level" element |> Option.defaultValue "error"
    Message = property "message" element |> Option.bind (text "text") |> Option.defaultValue ""
    Location = location element
}

/// Every result in a SARIF report, plus every notification the tool raised, such as a type error.
let findings (sarif: string) =
    use document = JsonDocument.Parse sarif

    [
        for run in items "runs" document.RootElement do
            for invocation in items "invocations" run do
                for notification in items "toolExecutionNotifications" invocation do
                    finding "tool" notification
            for result in items "results" run do
                finding "analyzer" result
    ]

let format finding =
    $"{finding.Location}: {finding.Level} {finding.Rule}: {finding.Message}"
