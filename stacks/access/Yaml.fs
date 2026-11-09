/// Small parsers over YamlDotNet's node model. Each returns a Validation, so callers can collect
/// every problem in a file instead of stopping at the first.
module Platform.Access.Yaml

open System
open System.IO
open FsToolkit.ErrorHandling
open YamlDotNet.RepresentationModel

type Fields = (YamlNode * YamlNode) list

let parseDocument (text: string) : Validation<YamlNode, string> =
    try
        let stream = YamlStream()
        stream.Load(new StringReader(text))
        Ok stream.Documents[0].RootNode
    with ex ->
        Validation.error $"not valid YAML: {ex.Message}"

let scalar (node: YamlNode) : Validation<string, string> =
    match node with
    | :? YamlScalarNode as s ->
        match s.Value with
        | null -> Validation.error $"expected a value at {node.Start}"
        | v when String.IsNullOrWhiteSpace v -> Validation.error $"expected a value at {node.Start}"
        | v -> Ok v
    | _ -> Validation.error $"expected a value at {node.Start}"

let mapping (node: YamlNode) : Validation<Fields, string> =
    match node with
    | :? YamlMappingNode as m -> Ok [ for kv in m.Children -> kv.Key, kv.Value ]
    | _ -> Validation.error $"expected a mapping at {node.Start}"

let sequence (node: YamlNode) : Validation<YamlNode list, string> =
    match node with
    | :? YamlSequenceNode as s -> Ok(List.ofSeq s.Children)
    | _ -> Validation.error $"expected a list at {node.Start}"

let field (name: string) (fields: Fields) : Validation<YamlNode, string> =
    fields
    |> List.tryFind (fun (k, _) -> scalar k = Ok name)
    |> Option.map snd
    |> Result.requireSome $"missing '{name}'"
    |> Validation.ofResult

let guid (text: string) : Validation<Guid, string> =
    match Guid.TryParse text with
    | true, g -> Ok g
    | _ -> Validation.error $"'{text}' is not an object ID"

/// Prefixes every error with where it happened, e.g. "groups.app-developers: unknown person 'carl'".
let within (context: string) (v: Validation<'a, string>) : Validation<'a, string> =
    v |> Validation.mapError (fun e -> $"{context}: {e}")
