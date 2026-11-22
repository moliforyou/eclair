module LayerRules

open System
open System.IO
open System.Xml.Linq
open Microsoft.FSharp.Reflection

type Layer =
    | Domain
    | Application
    | Contracts
    | Infrastructure
    | WebApi
    | BackgroundWorkers
    | Runner
    | Client
    | Eclair
    | DomainAnalyzers

type PackagePolicy =
    | AnyPackage
    | Only of string list

type Rule = {
    Allowed: Layer list
    Packages: PackagePolicy
}

let private allows layers packages = {
    Allowed = layers
    Packages = packages
}

let rule layer =
    match layer with
    | Domain -> allows [] (Only [])
    | Application -> allows [ Domain ] (Only [])
    | Contracts -> allows [ Domain ] (Only [ "FSharp.SystemTextJson" ])
    | Infrastructure -> allows [ Domain; Application ] AnyPackage
    | WebApi -> allows [ Domain; Application; Infrastructure; Contracts; Client ] AnyPackage
    | BackgroundWorkers -> allows [ Domain; Application; Infrastructure ] AnyPackage
    | Runner -> allows [ WebApi; BackgroundWorkers ] AnyPackage
    | Client -> allows [ Domain; Contracts ] AnyPackage
    | Eclair -> allows [] AnyPackage
    | DomainAnalyzers -> allows [] AnyPackage

let ruleFile = $"tools/eclair/{__SOURCE_FILE__}"

let private layersByName =
    FSharpType.GetUnionCases typeof<Layer>
    |> Array.map (fun case -> case.Name, nonNull (FSharpValue.MakeUnion (case, [||])) :?> Layer)
    |> Map.ofArray

let projectName (path: string) =
    let name = nonNull (Path.GetFileNameWithoutExtension (path.Replace ('\\', '/')))
    let prefix = "ChessPlatform."

    if name.StartsWith (prefix, StringComparison.Ordinal) then
        name.Substring prefix.Length
    else
        name

let layerOf path =
    Map.tryFind (projectName path) layersByName

type Project = {
    Path: string
    References: string list
    Packages: string list
}

let parseProject (path: string) (xml: string) =
    let doc = XDocument.Parse xml

    let includes item =
        doc.Descendants ()
        |> Seq.filter (fun element -> element.Name.LocalName = item)
        |> Seq.choose (fun element -> element.Attribute (XName.Get "Include") |> Option.ofObj)
        |> Seq.map _.Value
        |> List.ofSeq

    {
        Path = path
        References = includes "ProjectReference"
        Packages = includes "PackageReference"
    }

type Violation =
    | UnknownProject of path: string
    | ForbiddenReference of layer: Layer * target: string * path: string
    | DisallowedPackage of layer: Layer * package: string * path: string

let private isAllowed layer reference =
    layerOf reference
    |> Option.exists (fun target -> List.contains target (rule layer).Allowed)

let private isPermitted layer (package: string) =
    match (rule layer).Packages with
    | AnyPackage -> true
    | Only permitted ->
        permitted
        |> List.exists (fun p -> p.Equals (package, StringComparison.OrdinalIgnoreCase))

let check project =
    match layerOf project.Path with
    | None -> [ UnknownProject project.Path ]
    | Some layer -> [
        for reference in project.References do
            if not (isAllowed layer reference) then
                ForbiddenReference (layer, projectName reference, project.Path)
        for package in project.Packages do
            if not (isPermitted layer package) then
                DisallowedPackage (layer, package, project.Path)
      ]

let private joined (names: string list) =
    match List.rev names with
    | [] -> ""
    | [ only ] -> only
    | last :: rest -> String.concat ", " (List.rev rest) + " and " + last

let private allowedReferences layer =
    match (rule layer).Allowed with
    | [] -> "no project references"
    | allowed -> "references only to " + joined (List.map string allowed)

let private allowedPackages layer =
    match (rule layer).Packages with
    | AnyPackage -> "any package"
    | Only [] -> "no packages"
    | Only permitted -> "only " + joined permitted

let message violation =
    match violation with
    | UnknownProject path ->
        let name = projectName path
        $"{path}: no layer is named {name}. Add a {name} case and its rule to the table in {ruleFile}."
    | ForbiddenReference (layer, target, path) ->
        $"{path}: {layer} may not reference {target}. The {layer} rule allows {allowedReferences layer}. Remove the reference, or change the rule in {ruleFile}."
    | DisallowedPackage (layer, package, path) ->
        $"{path}: {layer} may not use the package {package}. The {layer} rule allows {allowedPackages layer}. Remove the package, or change the rule in {ruleFile}."
