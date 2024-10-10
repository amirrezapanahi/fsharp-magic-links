module Storage

open System
open System.IO
open System.Text.Json

type StorageResult<'T> =
    | Success of 'T
    | Failure of string

let private filePath = Path.Combine(Directory.GetCurrentDirectory(), "store.json")

let private loadStorage () : Map<string, JsonElement> =
    if File.Exists(filePath) then
        try
            let json = File.ReadAllText(filePath)
            JsonSerializer.Deserialize<Map<string, JsonElement>>(json)
        with _ ->
            Map.empty
    else
        Map.empty

let private saveStorage (storage: Map<string, JsonElement>) =
    let json = JsonSerializer.Serialize(storage)
    File.WriteAllText(filePath, json)

let storeItem<'T> (key: string) (value: 'T) : StorageResult<unit> =
    try
        let storage = loadStorage ()
        let jsonElement = JsonSerializer.SerializeToElement(value)
        let newStorage = storage.Add(key, jsonElement)
        saveStorage newStorage
        Success()
    with ex ->
        Failure ex.Message

let retrieveItem<'T> (key: string) : StorageResult<'T option> =
    try
        let storage = loadStorage ()

        match storage.TryFind(key) with
        | Some value ->
            if value.GetRawText() = "{}" then
                Success None
            else
                let deserializedValue = JsonSerializer.Deserialize<'T>(value.GetRawText())
                Success(Some deserializedValue)
        | None -> Success None
    with ex ->
        Failure ex.Message

let removeItem (key: string) : StorageResult<unit> =
    try
        let storage = loadStorage ()
        let newStorage = storage.Remove(key)
        saveStorage newStorage
        Success()
    with ex ->
        Failure ex.Message

let getAllKeys () : StorageResult<string list> =
    try
        let storage = loadStorage ()
        let keys = storage.Keys |> Seq.toList
        Success keys
    with ex ->
        Failure ex.Message
