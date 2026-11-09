/// Small, reusable parsers for turning DTO fields into domain values. Each returns a Result with a
/// message; callers combine them with FsToolkit's Validation so every problem is reported at once.
/// See docs/fsharp-style.md.
module Platform.Access.Parse

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open YamlDotNet.Core
open YamlDotNet.Serialization

let required (field: string) (x: 'a option) : Result<'a, string> =
    x |> Result.requireSome $"'{field}' is missing"

let notEmpty (field: string) (s: string) : Result<string, string> =
    if String.IsNullOrWhiteSpace s then
        Error $"'{field}' is empty"
    else
        Ok s

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

let private json =
    JsonSerializerOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

/// Reads YAML into a DTO: YAML → plain data → JSON → DTO. System.Text.Json maps F# records, options,
/// lists and maps natively and rejects unknown keys, so a misspelled section is an error, not ignored.
let yaml<'dto> (text: string) : Validation<'dto, string> =
    try
        let data = DeserializerBuilder().Build().Deserialize<obj> text
        let asJson = SerializerBuilder().JsonCompatible().Build().Serialize data

        // System.Text.Json returns null for an empty document. Option.ofObj turns that into None right at
        // the library boundary, so no null reaches our code.
        JsonSerializer.Deserialize(asJson, typeof<'dto>, json)
        |> Option.ofObj
        |> Option.map unbox<'dto>
        |> Result.requireSome "the file is empty"
        |> Validation.ofResult
    with
    | :? YamlException as ex -> Validation.error $"not valid YAML at line {ex.Start.Line}: {ex.Message}"
    | :? JsonException as ex -> Validation.error $"unexpected content: {ex.Message}"
