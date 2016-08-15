// Copyright (c) University of Warwick. All Rights Reserved. Licensed under the Apache License, Version 2.0. See LICENSE.txt in the project root for license information.

namespace Endorphin.IO

type private Message =
| Receive of string
| Send of string
| QueryNextLine of query : string * AsyncReplyChannel<string>
| QueryUntil of query: string * condition: (string -> bool) * AsyncReplyChannel<string list>

type ReplyConsumer = string list -> string -> (string list * string) option


/// Agent to serialise writing linemode command and read emitted data into
/// lines asynchronously. StreamBuffer doesn't
/// This is useful for devices which don't implement a VISA-style command-response
/// e.g. if commands are not acknowledged but the device may stream line-mode data
type LineAgent(writeLine,handleLine,logname:string) =
    let logger = log4net.LogManager.GetLogger logname

    // Just wait for the next single line and return that
    let nextLineReply (rc:AsyncReplyChannel<string>) lines remainder =
        match lines with
        | next :: rest ->
            rc.Reply next
            Some (rest,remainder)
        | _ -> None

    // Waits until a line matches the supplied condition
    // If the required line is the start of the latest incomplete line, such as a prompt,
    // then consume that line immediately and return the preceding lines
    let consumeUntilReply (rc:AsyncReplyChannel<string list>) expected (lines: string list) remainder =
        match List.tryFindIndex expected lines with
        | None ->
            if expected remainder then
                rc.Reply lines
                Some ([],"")
            else
                None
        | Some i ->
            let (reply,rest) = List.splitAt i lines
            rc.Reply reply
            Some (rest.Tail,remainder)

    let extractLine (data:string) =
        match data.IndexOfAny([| '\r'; '\n' |]) with
        | -1 ->
            (None,data)
        | i ->
            let line = data.[0..i-1]
            let remainder =
                if data.Length <= i+1 then
                    "" // 
                else
                    if data.Chars(i+1) = '\n' then
                        data.Substring (i+2)
                    else
                        data.Substring (i+1)
            (Some line,remainder)
        
    let extractLines data =
        let rec extractLines' lines (data:string) =
            let line, remainder' = extractLine data
            match line with
            | None -> (lines,remainder')
            | Some line ->
                match remainder' with
                | "" -> (line :: lines,"")
                | _  -> extractLines' (line :: lines) remainder'
        let lines, remainder = extractLines' [] data
        (List.rev lines, remainder)

    let messageHandler (mbox:MailboxProcessor<Message>) =
        let rec loop (remainder:string) (receivedLines:string list) (pendingQueries:ReplyConsumer list) = async {
            do! Async.SwitchToThreadPool()
            let! msg = mbox.Receive()
            match msg with
            | Receive newData ->
                // Received data will not be aligned with newlines.
                // Combine data already received with the new data and
                // emit any completed lines.
                // Save any incomplete line fragment to combine with the next block
                    
                let newLines, remainder' = extractLines (remainder + newData)
                newLines |> List.iter (sprintf "Received line: %s" >> logger.Debug)
                newLines |> List.iter handleLine

                let rec handleConsumers lines remainder (consumers : ReplyConsumer list) =
                    match consumers with
                    | consumer :: rest ->
                        match consumer lines remainder' with
                        | Some (lines',remainder') -> // Satisfied this one, try to satisfy the rest with the remaining lines
                            handleConsumers lines' remainder' rest
                        | None -> // Did not find enough to satisfy consumer
                            (lines,remainder',consumers)
                    | [] ->
                        (lines,remainder,[])
                    

                if pendingQueries.IsEmpty then
                    return! loop remainder' [] [] // Forget pending lines as no-one is waiting for them
                else
                    let lines = receivedLines @ newLines
                    let consumerQ = List.rev pendingQueries
                    let (remainingLines',remainder'',consumerQ') = handleConsumers lines remainder' consumerQ
                    return! loop remainder'' remainingLines' (List.rev consumerQ')

            | Send line ->
                do! writeLine line
                return! loop remainder receivedLines pendingQueries
            | QueryNextLine (line,replyChannel) ->
                Send line |> mbox.Post // send string
                let replyConsumer = nextLineReply replyChannel
                return! loop remainder receivedLines ( replyConsumer :: pendingQueries) // reply with next line
            | QueryUntil (query,condition,replyChannel) ->
                Send query |> mbox.Post
                let replyConsumer = consumeUntilReply replyChannel condition
                return! loop remainder receivedLines (replyConsumer :: pendingQueries)
        }
        loop "" [] []

    let agent = MailboxProcessor.Start messageHandler

    /// Write a line to the serial port
    member __.WriteLine = Send >> agent.Post
    member __.Receive a = a |> Receive |> agent.Post
    member __.QueryLineAsync line = (fun rc -> QueryNextLine (line,rc)) |> agent.PostAndAsyncReply
    member __.QueryLine line = (fun rc -> QueryNextLine (line,rc)) |> agent.PostAndReply
    member __.QueryUntilAsync condition query = (fun rc -> QueryUntil (query,condition,rc)) |> agent.PostAndAsyncReply
    member __.QueryUntil condition query = (fun rc -> QueryUntil (query,condition,rc)) |> agent.PostAndReply
    member __.Logger = logger
    