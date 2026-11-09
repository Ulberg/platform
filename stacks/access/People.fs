/// access/people.yaml: the file as written (DTO) → the checked access model (domain).
/// Parse, don't validate: everything downstream only sees `Access`, so a typo in the YAML fails
/// here instead of producing half an access setup.
module Platform.Access.People

open System
open System.IO
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open FsToolkit.ErrorHandling.Operator.Validation
open System.Text.Json.Serialization

// ---------------------------------------------------------------------------------------------
// Domain

type PersonKey = private PersonKey of string

module PersonKey =
    let parse s =
        Parse.notEmpty "person" s |> Result.map PersonKey

    let value (PersonKey key) = key

type GitHubHandle = private GitHubHandle of string

module GitHubHandle =
    // GitHub's rules: 1–39 letters, digits or single hyphens, not starting or ending with a hyphen.
    let private pattern = Regex "^[A-Za-z0-9](-?[A-Za-z0-9]){0,38}$"

    let parse s =
        Parse.notEmpty "github" s
        |> Result.bind (Parse.matches pattern "github")
        |> Result.map GitHubHandle

    let value (GitHubHandle handle) = handle

    /// GitHub handles are case-insensitive.
    let same (GitHubHandle a) (GitHubHandle b) =
        String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

type EntraObjectId = private EntraObjectId of Guid

module EntraObjectId =
    let parse field s =
        Parse.notEmpty field s
        |> Result.bind (Parse.guid field)
        |> Result.map EntraObjectId

    let string (EntraObjectId id) = id.ToString()

type Email = private Email of string

module Email =
    // Deliberately loose: one @, no spaces, a dot in the domain. Entra rejects what it can't deliver to.
    let private pattern = Regex @"^[^@\s]+@[^@\s]+\.[^@\s]+$"

    let value (Email e) = e

    /// A comma-separated list from a secret. Errors name the position, never the value, so a bad entry
    /// doesn't leak the others into logs.
    let parseList (name: string) (raw: string option) : Result<Email list, string list> =
        raw
        |> Option.map (fun s ->
            s.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> List.ofArray)
        |> Option.defaultValue []
        |> List.mapi (fun i e ->
            if pattern.IsMatch e then
                Ok(Email e)
            else
                Error $"{name}: entry {i + 1} is not an email address")
        |> List.sequenceResultA
        |> Result.bind (fun emails ->
            if List.isEmpty emails then
                Error [ $"{name}: needs at least one address" ]
            else
                Ok emails)

type RepoName = private RepoName of string

module RepoName =
    let platform = RepoName "platform"

    let parse s =
        Parse.notEmpty "repo" s |> Result.map RepoName

    let value (RepoName name) = name

type Person =
    {
        key: PersonKey
        github: GitHubHandle
        entra: EntraObjectId
    }

module Person =
    let create key github entra =
        {
            key = key
            github = github
            entra = entra
        }

    let key p = PersonKey.value p.key

type Access =
    {
        people: Person list
        infraDevelopers: Person list
        appDevelopers: Person list
        /// Code owners of each repo. Every repo has at least one, and `platform` is always present.
        repoLeads: Map<RepoName, Person list>
        platformLeads: Person list
        emergencyAccess: EntraObjectId list
    }

module Access =
    let create people infraDevelopers appDevelopers repoLeads emergencyAccess =
        {
            people = people
            infraDevelopers = infraDevelopers
            appDevelopers = appDevelopers
            repoLeads = repoLeads
            platformLeads = Map.find RepoName.platform repoLeads
            emergencyAccess = emergencyAccess
        }

// ---------------------------------------------------------------------------------------------
// DTO: the file exactly as written. Anything can be missing, so every field is an option.

type PersonDto =
    {
        github: string option
        entra: string option
    }

type PeopleFileDto =
    {
        people: Map<string, PersonDto option> option
        groups: Map<string, string list option> option
        [<JsonPropertyName("repo-leads")>]
        repoLeads: Map<string, string list option> option
        [<JsonPropertyName("emergency-access")>]
        emergencyAccess: string list option
    }

// ---------------------------------------------------------------------------------------------
// DTO → domain

let private infraDevelopers = "infra-developers"
let private appDevelopers = "app-developers"

let private person (key: string, dto: PersonDto option) : Validation<Person, string> =
    match dto with
    | None -> Validation.error $"people.{key}: needs 'github' and 'entra'"
    | Some dto ->
        Person.create <!^> PersonKey.parse key
        <*^> (Parse.required "github" dto.github |> Result.bind GitHubHandle.parse)
        <*^> (Parse.required "entra" dto.entra |> Result.bind (EntraObjectId.parse "entra"))
        |> Parse.within $"people.{key}"

let private uniqueGitHub (people: Person list) : Validation<unit, string> =
    match
        people
        |> List.countBy (fun p -> (GitHubHandle.value p.github).ToLowerInvariant())
        |> List.filter (snd >> (<) 1)
    with
    | [] -> Ok()
    | dups ->
        Error
            [
                for handle, _ in dups -> $"people: github handle '{handle}' is used more than once"
            ]

/// Names → people. Every name must be defined under `people`, and the list can't be empty.
let private resolve (people: Person list) (context: string) (names: string list option) =
    let find name =
        people
        |> List.tryFind (fun p -> Person.key p = name)
        |> Result.requireSome $"unknown person '{name}'"
        |> Validation.ofResult

    names
    |> Option.defaultValue []
    |> List.traverseValidationA find
    |> Validation.bind (fun found ->
        found
        |> Result.requireNotEmpty "needs at least one person"
        |> Validation.ofResult
        |> Validation.map (fun () -> List.distinct found))
    |> Parse.within context

let private groups people (dto: Map<string, string list option>) =
    let group name =
        match dto |> Map.tryFind name with
        | Some members -> resolve people $"groups.{name}" members
        | None -> Validation.error $"groups: missing '{name}'"

    let unknown =
        dto
        |> Map.keys
        |> Seq.filter (fun name -> name <> infraDevelopers && name <> appDevelopers)
        |> Seq.map (fun name -> $"groups: unknown group '{name}'")
        |> List.ofSeq

    validation {
        let! infra = group infraDevelopers
        and! app = group appDevelopers
        and! () = if List.isEmpty unknown then Ok() else Error unknown
        return infra, app
    }

let private repoLeads people (dto: Map<string, string list option>) =
    let repo (name, leads) =
        (fun repo leads -> repo, leads) <!^> RepoName.parse name
        <*> resolve people $"repo-leads.{name}" leads

    dto
    |> Map.toList
    |> List.traverseValidationA repo
    |> Validation.map Map.ofList
    |> Validation.bind (fun leads ->
        if leads.ContainsKey RepoName.platform then
            Ok leads
        else
            Validation.error "repo-leads: missing 'platform'")

let private emergencyAccess (ids: string list) =
    ids
    |> List.traverseValidationA (EntraObjectId.parse "emergency-access" >> Validation.ofResult)
    |> Validation.bind (fun ids ->
        if List.length ids >= 2 then
            Ok ids
        else
            Validation.error "emergency-access: needs two accounts")

let fromDto (dto: PeopleFileDto) : Validation<Access, string> =
    let section name value parse =
        Parse.required name value |> Validation.ofResult |> Validation.bind parse

    validation {
        let! people =
            section "people" dto.people (Map.toList >> List.traverseValidationA person)

        // The sections are independent (and!), so one run reports every problem in the file.
        let! () = uniqueGitHub people
        and! infra, app = section "groups" dto.groups (groups people)
        and! leads = section "repo-leads" dto.repoLeads (repoLeads people)
        and! emergency = section "emergency-access" dto.emergencyAccess emergencyAccess
        return Access.create people infra app leads emergency
    }

let parse (yaml: string) : Validation<Access, string> =
    Parse.yaml<PeopleFileDto> yaml |> Validation.bind fromDto
