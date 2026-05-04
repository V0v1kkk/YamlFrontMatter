module SkillFrontMatter.Core.FrontMatterTextReader

open System
open System.IO

type FrontMatterEnd =
    | ClosedByDelimiter
    | PhysicalEndOfFile

type FrontMatterTextReader private (inner: TextReader) as this =
    inherit TextReader()

    let mutable currentLine: string = null
    let mutable currentOffset = 0
    let mutable endReason: FrontMatterEnd option = None

    let isFinished () = endReason.IsSome

    let isDelimiter (line: string) =
        not (isNull line)
        && line.AsSpan().Trim().SequenceEqual("---".AsSpan())

    let ensureCurrentLine () =
        if isFinished () then
            false
        elif not (isNull currentLine) && currentOffset < currentLine.Length then
            true
        else
            let line = inner.ReadLine()
            if isNull line then
                endReason <- Some PhysicalEndOfFile
                false
            elif isDelimiter line then
                endReason <- Some ClosedByDelimiter
                false
            else
                currentLine <- line + "\n"
                currentOffset <- 0
                true

    static member TryCreate(inner: TextReader) =
        let firstLine = inner.ReadLine()
        if not (isNull firstLine)
           && firstLine.AsSpan().Trim().SequenceEqual("---".AsSpan()) then
            Some(new FrontMatterTextReader(inner))
        else
            None

    member _.EndReason = endReason

    member _.DrainToEnd() =
        let buffer = Array.zeroCreate<char> 4096
        while this.Read(buffer, 0, buffer.Length) > 0 do ()
        endReason

    override _.Read() =
        if ensureCurrentLine () then
            let ch = currentLine.[currentOffset]
            currentOffset <- currentOffset + 1
            int ch
        else
            -1

    override _.Read(buffer: char[], index: int, count: int) =
        if isNull buffer then nullArg (nameof buffer)
        if index < 0 || index > buffer.Length then
            invalidArg (nameof index) "Index is outside the buffer."
        if count < 0 || count > buffer.Length - index then
            invalidArg (nameof count) "Count is outside the buffer."

        let mutable written = 0
        let mutable stop = false
        while not stop && written < count && ensureCurrentLine () do
            let source = currentLine.AsSpan(currentOffset)
            let toCopy = min source.Length (count - written)
            source.Slice(0, toCopy).CopyTo(buffer.AsSpan(index + written))
            currentOffset <- currentOffset + toCopy
            written <- written + toCopy
            if written >= count then stop <- true
        written
