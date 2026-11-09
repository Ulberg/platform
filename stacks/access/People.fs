/// Parses access/people.yaml into a checked model. Everything downstream only sees `Access`,
/// so a typo in the YAML fails here instead of producing half an access setup.
module Platform.Access.People

open System
open System.IO
open YamlDotNet.RepresentationModel

type Person =
    {
        Key: string
        GitHub: string
        EntraObjectId: Guid
    }

type Access =
    {
        People: Person list
        InfraDevelopers: Person list
        AppDevelopers: Person list
        /// Repo name → its leads (code owners). Every repo has at least one.
        RepoLeads: Map<string, Person list>
        EmergencyAccess: Guid list
    }

    member this.PlatformLeads = this.RepoLeads["platform"]

type private ResultBuilder() =
    member _.Bind(r, f) = Result.bind f r
    member _.Return x = Ok x
    member _.ReturnFrom r = r
    member _.Zero() = Ok()

let private result = ResultBuilder()

let private orError error option =
    match option with
    | Some x -> Ok x
    | None -> Error error

let private requiredGroups = [ "infra-developers"; "app-developers" ]

let private scalar (node: YamlNode) =
    match node with
    | :? YamlScalarNode as s ->
        match s.Value with
        | null -> Error $"expected a value at {node.Start}"
        | v when String.IsNullOrWhiteSpace v -> Error $"expected a value at {node.Start}"
        | v -> Ok v
    | _ -> Error $"expected a value at {node.Start}"

let private mapping (node: YamlNode) =
    match node with
    | :? YamlMappingNode as m -> Ok [ for kv in m.Children -> (kv.Key, kv.Value) ]
    | _ -> Error $"expected a mapping at {node.Start}"

let private sequence (node: YamlNode) =
    match node with
    | :? YamlSequenceNode as s -> Ok(List.ofSeq s.Children)
    | _ -> Error $"expected a list at {node.Start}"

let private field name (fields: (YamlNode * YamlNode) list) =
    fields
    |> List.tryFind (fun (k, _) -> scalar k = Ok name)
    |> Option.map snd
    |> orError $"missing '{name}'"

let private guid context (text: string) =
    match Guid.TryParse text with
    | true, g -> Ok g
    | _ -> Error $"{context}: '{text}' is not an object ID"

/// Collects every error instead of stopping at the first one.
let private traverse (f: 'a -> Result<'b, string list>) (items: 'a list) =
    let results = items |> List.map f

    match
        results
        |> List.collect (function
            | Error e -> e
            | Ok _ -> [])
    with
    | [] -> Ok(results |> List.choose Result.toOption)
    | errors -> Error errors

let private one r = Result.mapError List.singleton r

let private person (key: YamlNode, value: YamlNode) =
    result {
        let! key = scalar key |> one
        let! fields = mapping value |> one

        let! github =
            field "github" fields
            |> Result.bind scalar
            |> Result.mapError (fun e -> [ $"{key}: {e}" ])

        let! entra =
            field "entra" fields
            |> Result.bind scalar
            |> Result.bind (guid key)
            |> Result.mapError (fun e -> [ $"{key}: {e}" ])

        return
            {
                Key = key
                GitHub = github
                EntraObjectId = entra
            }
    }

let private members (people: Person list) context (node: YamlNode) =
    result {
        let! names = sequence node |> Result.mapError (fun e -> [ $"{context}: {e}" ])

        let! found =
            names
            |> traverse (fun n ->
                scalar n
                |> Result.bind (fun name ->
                    people
                    |> List.tryFind (fun p -> p.Key = name)
                    |> orError $"{context}: unknown person '{name}'")
                |> one)

        if List.isEmpty found then
            return! Error [ $"{context}: needs at least one person" ]
        else
            return List.distinct found
    }

let private namedLists people section fields =
    result {
        let! entries = field section fields |> Result.bind mapping |> one

        return!
            entries
            |> traverse (fun (k, v) ->
                result {
                    let! name = scalar k |> one
                    let! list = members people $"{section}.{name}" v
                    return name, list
                })
            |> Result.map Map.ofList
    }

let parse (yaml: string) : Result<Access, string list> =
    result {
        let! root =
            try
                let stream = YamlStream()
                stream.Load(new StringReader(yaml))
                mapping stream.Documents[0].RootNode |> one
            with ex ->
                Error [ $"not valid YAML: {ex.Message}" ]

        let! people =
            field "people" root
            |> Result.bind mapping
            |> one
            |> Result.bind (traverse person)

        let duplicateGitHub =
            match
                people
                |> List.countBy (fun p -> p.GitHub.ToLowerInvariant())
                |> List.filter (fun (_, n) -> n > 1)
            with
            | [] -> Ok()
            | dups -> Error [ for g, _ in dups -> $"people: github handle '{g}' is used more than once" ]

        let groups =
            namedLists people "groups" root
            |> Result.bind (fun groups ->
                match requiredGroups |> List.filter (fun g -> not (groups.ContainsKey g)) with
                | [] -> Ok groups
                | missing -> Error [ for g in missing -> $"groups: missing '{g}'" ])

        let repoLeads =
            namedLists people "repo-leads" root
            |> Result.bind (fun leads ->
                if leads.ContainsKey "platform" then
                    Ok leads
                else
                    Error [ "repo-leads: missing 'platform'" ])

        let emergency =
            field "emergency-access" root
            |> Result.bind sequence
            |> one
            |> Result.bind (traverse (fun n -> scalar n |> Result.bind (guid "emergency-access") |> one))
            |> Result.bind (fun ids ->
                if List.length ids >= 2 then
                    Ok ids
                else
                    Error [ "emergency-access: needs two accounts" ])

        // The sections are checked independently, so one run reports every problem in the file.
        let errorsOf =
            function
            | Error e -> e
            | Ok _ -> []

        match duplicateGitHub, groups, repoLeads, emergency with
        | Ok(), Ok groups, Ok repoLeads, Ok emergency ->
            return
                {
                    People = people
                    InfraDevelopers = groups["infra-developers"]
                    AppDevelopers = groups["app-developers"]
                    RepoLeads = repoLeads
                    EmergencyAccess = emergency
                }
        | _ ->
            return!
                Error(
                    errorsOf duplicateGitHub
                    @ errorsOf groups
                    @ errorsOf repoLeads
                    @ errorsOf emergency
                )
    }

let load path =
    match parse (File.ReadAllText path) with
    | Ok access -> access
    | Error errors -> failwith (String.Join("\n", $"{path} is invalid:" :: errors))

/// The CODEOWNERS file every repo must contain: everything is owned by the repo's leads.
let codeowners (leads: Person list) =
    let owners = leads |> List.map (fun p -> "@" + p.GitHub) |> String.concat " "

    $"# Generated from Ulberg/platform access/people.yaml (repo-leads). Change it there, not here.\n* {owners}\n"
