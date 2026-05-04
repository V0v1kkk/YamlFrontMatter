module FSharpYamlFrontmatterParsing.SkillFrontMatter

open FSharpYamlFrontmatterParsing.FrontMatterTextReader

open System
open System.Collections.Generic
open System.IO
open System.Text
open YamlDotNet.Serialization

type SkillFrontMatter =
    { Path: string
      Metadata: IReadOnlyDictionary<string, obj> }

type SkillReadError =
    | MissingClosingDelimiter of path: string
    | YamlParseFailed of path: string * error: exn
    | FileReadFailed of path: string * error: exn

module SkillFrontMatter =

    let private openSkillFile path =
        let options = FileStreamOptions()
        options.Mode <- FileMode.Open
        options.Access <- FileAccess.Read
        options.Share <- FileShare.ReadWrite ||| FileShare.Delete
        options.BufferSize <- 16 * 1024
        options.Options <- FileOptions.None

        new FileStream(path, options)

    let private toReadOnlyDictionary (value: Dictionary<string, obj> | null) =
        if isNull (box value) then
            Dictionary<string, obj>() :> IReadOnlyDictionary<string, obj>
        else
            value :> IReadOnlyDictionary<string, obj>

    let tryReadOne
        (deserializer: IDeserializer)
        (path: string)
        : Result<SkillFrontMatter option, SkillReadError> =

        try
            use stream = openSkillFile path

            use reader =
                new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks = true,
                    bufferSize = 16 * 1024,
                    leaveOpen = false)

            match FrontMatterTextReader.TryCreate(reader) with
            | None ->
                Ok None

            | Some yamlReader ->
                use yamlReader = yamlReader

                try
                    let metadata =
                        deserializer.Deserialize<Dictionary<string, obj>>(yamlReader)
                        |> toReadOnlyDictionary

                    let endReason = yamlReader.DrainToEnd()

                    match endReason with
                    | Some ClosedByDelimiter ->
                        Ok
                            (Some
                                { Path = path
                                  Metadata = metadata })

                    | Some PhysicalEndOfFile
                    | None ->
                        Error(MissingClosingDelimiter path)

                with ex ->
                    // Даже если YAML-парсер упал, можно дочитать адаптер,
                    // чтобы понять: это реально YAML-ошибка или просто нет closing ---.
                    let endReason = yamlReader.DrainToEnd()

                    match endReason with
                    | Some PhysicalEndOfFile
                    | None ->
                        Error(MissingClosingDelimiter path)

                    | Some ClosedByDelimiter ->
                        Error(YamlParseFailed(path, ex))

        with ex ->
            Error(FileReadFailed(path, ex))