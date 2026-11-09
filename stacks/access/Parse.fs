/// Small, reusable parsers for turning DTO fields into domain values. Each returns a Result with a
/// message; callers combine them with FsToolkit's Validation so every problem is reported at once.
module Platform.Access.Parse

open System
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open YamlDotNet.Core
open YamlDotNet.Serialization

let notEmpty (field: string) (s: string | null) : Result<string, string> =
    match s with
    | null -> Error $"'{field}' is missing"
    | s when String.IsNullOrWhiteSpace s -> Error $"'{field}' is empty"
    | s -> Ok s

let required (field: string) (x: 'T | null) : Result<'T, string> =
    match x with
    | Null -> Error $"'{field}' is missing"
    | NonNull x -> Ok x

let guid (field: string) (s: string) : Result<Guid, string> =
    match Guid.TryParse s with
    | true, g -> Ok g
    | _ -> Error $"'{field}': '{s}' is not an object ID"

let matches (pattern: Regex) (field: string) (s: string) : Result<string, string> =
    if pattern.IsMatch s then
        Ok s
    else
        Error $"'{field}': '{s}' is not valid"

/// Prefixes every error with where it happened, e.g. "groups.app-developers: unknown person 'carl'".
let within (context: string) (v: Validation<'a, string>) : Validation<'a, string> =
    v |> Validation.mapError (fun e -> $"{context}: {e}")

/// Deserializes YAML into a DTO. Unknown keys are errors, so a misspelled section can't be silently ignored.
let yaml<'dto> (text: string) : Validation<'dto, string> =
    try
        match box (DeserializerBuilder().Build().Deserialize<'dto> text) with
        | null -> Validation.error "the file is empty"
        | dto -> Ok(unbox dto)
    with :? YamlException as ex ->
        let reason =
            match ex.InnerException with
            | null -> ex.Message
            | inner -> inner.Message

        Validation.error $"not valid YAML at line {ex.Start.Line}: {reason}"
