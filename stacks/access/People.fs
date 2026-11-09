/// Parses access/people.yaml into a checked model. Everything downstream only sees `Access`,
/// so a typo in the YAML fails here instead of producing half an access setup.
module Platform.Access.People

open System
open System.IO
open FsToolkit.ErrorHandling
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

let private requiredGroups = [ "infra-developers"; "app-developers" ]

let private person (key: YamlNode, value: YamlNode) =
    validation {
        let! key = Yaml.scalar key
        let! fields = Yaml.mapping value |> Yaml.within key

        let! github =
            Yaml.field "github" fields |> Validation.bind Yaml.scalar |> Yaml.within key

        and! entra =
            Yaml.field "entra" fields
            |> Validation.bind Yaml.scalar
            |> Validation.bind Yaml.guid
            |> Yaml.within key

        return
            {
                Key = key
                GitHub = github
                EntraObjectId = entra
            }
    }

let private uniqueGitHub (people: Person list) : Validation<unit, string> =
    match
        people
        |> List.countBy (fun p -> p.GitHub.ToLowerInvariant())
        |> List.filter (fun (_, n) -> n > 1)
    with
    | [] -> Ok()
    | dups -> Error [ for g, _ in dups -> $"people: github handle '{g}' is used more than once" ]

let private members (people: Person list) (node: YamlNode) =
    validation {
        let! names = Yaml.sequence node

        let! found =
            names
            |> List.traverseValidationA (fun n ->
                Yaml.scalar n
                |> Validation.bind (fun name ->
                    people
                    |> List.tryFind (fun p -> p.Key = name)
                    |> Result.requireSome $"unknown person '{name}'"
                    |> Validation.ofResult))

        do! found |> Result.requireNotEmpty "needs at least one person"
        return List.distinct found
    }

/// A section like `groups:` or `repo-leads:`: names mapped to lists of people.
let private namedLists people section root =
    validation {
        let! entries = Yaml.field section root |> Validation.bind Yaml.mapping

        let! lists =
            entries
            |> List.traverseValidationA (fun (k, v) ->
                Yaml.scalar k
                |> Validation.bind (fun name ->
                    members people v
                    |> Yaml.within $"{section}.{name}"
                    |> Validation.map (fun list -> name, list)))

        return Map.ofList lists
    }

let private requireKeys section keys (map: Map<string, 'a>) =
    keys
    |> List.traverseValidationA (fun k ->
        if map.ContainsKey k then
            Ok()
        else
            Validation.error $"{section}: missing '{k}'")
    |> Validation.map (fun _ -> map)

let private emergencyAccess root =
    Yaml.field "emergency-access" root
    |> Validation.bind Yaml.sequence
    |> Validation.bind (List.traverseValidationA (Yaml.scalar >> Validation.bind Yaml.guid))
    |> Validation.bind (fun ids ->
        if List.length ids >= 2 then
            Ok ids
        else
            Validation.error "needs two accounts")
    |> Yaml.within "emergency-access"

let parse (yaml: string) : Validation<Access, string> =
    validation {
        let! root = Yaml.parseDocument yaml |> Validation.bind Yaml.mapping

        let! people =
            Yaml.field "people" root
            |> Validation.bind Yaml.mapping
            |> Validation.bind (List.traverseValidationA person)

        // The sections are independent (and!), so one run reports every problem in the file.
        let! _ = uniqueGitHub people

        and! groups =
            namedLists people "groups" root
            |> Validation.bind (requireKeys "groups" requiredGroups)

        and! repoLeads =
            namedLists people "repo-leads" root
            |> Validation.bind (requireKeys "repo-leads" [ "platform" ])

        and! emergency = emergencyAccess root

        return
            {
                People = people
                InfraDevelopers = groups["infra-developers"]
                AppDevelopers = groups["app-developers"]
                RepoLeads = repoLeads
                EmergencyAccess = emergency
            }
    }

let load path =
    match parse (File.ReadAllText path) with
    | Ok access -> access
    | Error errors -> failwith (String.Join("\n", $"{path} is invalid:" :: errors))

/// The CODEOWNERS file every repo must contain: everything is owned by the repo's leads.
let codeowners (leads: Person list) =
    let owners = leads |> List.map (fun p -> "@" + p.GitHub) |> String.concat " "

    $"# Generated from Ulberg/platform access/people.yaml (repo-leads). Change it there, not here.\n* {owners}\n"
