module DomainRules

open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols

/// Folders whose code must stay pure: no mutable state and no exceptions.
let pureFolders = [ "src/backend/Domain/" ]

/// Functions that throw, by the module that declares them.
let private throwing = set [
    "Microsoft.FSharp.Core.Operators", "failwith"
    "Microsoft.FSharp.Core.Operators", "raise"
    "Microsoft.FSharp.Core.Operators", "reraise"
    "Microsoft.FSharp.Core.Operators", "invalidArg"
    "Microsoft.FSharp.Core.Operators", "invalidOp"
    "Microsoft.FSharp.Core.Operators", "nullArg"
    "Microsoft.FSharp.Core.ExtraTopLevelOperators", "failwithf"
]

let isPure (fileName: string) =
    let path = fileName.Replace ('\\', '/')
    pureFolders |> List.exists (fun folder -> path.Contains ("/" + folder))

let private warning code text range = {
    Type = "Domain purity"
    Message = text
    Code = code
    Severity = Severity.Warning
    Range = range
    Fixes = []
}

let private throws (value: FSharpMemberOrFunctionOrValue) =
    value.DeclaringEntity
    |> Option.bind _.TryFullName
    |> Option.exists (fun entity -> throwing.Contains (entity, value.DisplayName))

let check (uses: FSharpSymbolUse seq) = [
    for symbolUse in uses do
        match symbolUse.Symbol with
        | :? FSharpMemberOrFunctionOrValue as value when symbolUse.IsFromDefinition && value.IsMutable ->
            warning
                "ECLAIR-001"
                $"'{value.DisplayName}' is mutable. Domain code stays pure: use recursion, a fold or a new value."
                symbolUse.Range
        | :? FSharpField as field when symbolUse.IsFromDefinition && field.IsMutable ->
            warning
                "ECLAIR-001"
                $"The field '{field.DisplayName}' is mutable. Domain types stay immutable: copy and update instead."
                symbolUse.Range
        | :? FSharpMemberOrFunctionOrValue as value when symbolUse.IsFromUse && throws value ->
            warning
                "ECLAIR-002"
                $"'{value.DisplayName}' throws. Domain code returns Result or Option instead."
                symbolUse.Range
        | _ -> ()
]

[<CliAnalyzer("DomainPurityAnalyzer", "Domain code has no mutable state and throws no exceptions.")>]
let domainPurity: Analyzer<CliContext> =
    fun context -> async {
        if isPure context.FileName then
            return check (context.CheckFileResults.GetAllUsesOfAllSymbolsInFile ())
        else
            return []
    }
