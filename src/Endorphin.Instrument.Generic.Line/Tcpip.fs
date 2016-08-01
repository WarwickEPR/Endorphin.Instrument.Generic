// Copyright (c) University of Warwick. All Rights Reserved. Licensed under the Apache License, Version 2.0. See LICENSE.txt in the project root for license information.

namespace Endorphin.Instrument.Generic.Line

open Endorphin.Core
open System
open System.Net.Sockets
open System.Threading

[<AutoOpen>]
module Tcpip =

    /// Serialises writing linemode commands to a Tcpip port and read emitted data
    /// into lines asynchronously.
    /// This is useful for devices which don't implement a VISA-style command-response
    /// e.g. if commands are not acknowledged but the device may stream line-mode data
    type TcpipInstrument(logname:string,lineHandler,hostname:string,port) =

        let logger = log4net.LogManager.GetLogger logname
        let cts = new CancellationTokenSource()

        let client =
            let client = new TcpClient()
            client.ReceiveBufferSize <- 512
            client.NoDelay <- true
            client

        let writeLine (client:TcpClient) line =
            logger.Debug <| sprintf "Sending line: %s" line
            let chunk = System.Text.Encoding.UTF8.GetBytes (line + "\r\n")
            client.GetStream().WriteAsync(chunk,0,chunk.Length) |> Async.AwaitTask
        
        // create line agent
        let lineAgent = new LineAgent( (writeLine client), lineHandler, logname )

        let bufferLen = 2 <<< 13 // 64k
        let buffer:byte[] = Array.zeroCreate bufferLen

        member x.Start() =
            // connect to server
            client.Connect(hostname,port)

            // start pumping data
            let rec readLoop = async {
                // Assume the client is connected until after a read
                // "Connected" only gives the state of the most recent connection
                do! Async.SwitchToNewThread()
                while not cts.IsCancellationRequested do
                    // This version of AsyncRead returns whilst the other blocks
                    try
                        let! read = client.GetStream().AsyncRead(buffer,0,bufferLen)
                        if read > 0 then
                            let stringChunk = System.Text.Encoding.UTF8.GetString buffer.[0..read-1]
                            logger.Debug <| sprintf "Read %d bytes" read
                            stringChunk |> lineAgent.Receive
                    with :?TimeoutException -> () }

            Async.Start (readLoop,cts.Token)

        interface IDisposable with member __.Dispose() = cts.Cancel(); client.Close()

        member __.WriteLine = lineAgent.WriteLine

    type ObservableTcpipInstrument(logname,hostname,port) =
        let notifier = new NotificationEvent<string>()
        let notify = Next >> notifier.Trigger
        let tcpipInstrument = new TcpipInstrument(logname,notify,hostname,port)
            
        member __.Lines() : IObservable<string> = notifier.Publish |> Observable.fromNotificationEvent
        member __.Complete() = Completed |> notifier.Trigger
        member __.Error = Error >> notifier.Trigger
        member __.Start = tcpipInstrument.Start
        member __.WriteLine = tcpipInstrument.WriteLine

        interface IDisposable
            with member x.Dispose() = x.Complete()
                                      (tcpipInstrument :> IDisposable).Dispose()
