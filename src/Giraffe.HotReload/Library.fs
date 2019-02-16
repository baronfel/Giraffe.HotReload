namespace Giraffe.HotReload

module rec LiveUpdate =
  open System.Reflection
  open System
  open Giraffe
  open GiraffeViewEngine
  open Microsoft.AspNetCore.Http
  open Microsoft.Extensions.Logging
  open FSharp.Compiler.PortaCode.CodeModel
  open FSharp.Compiler.PortaCode.Interpreter
  open FSharp.Control.Tasks.V2.ContextInsensitive

  let welcomePage: XmlNode =
    html [ ] [
      body [ ] [
        p [ ] [ str "Welcome to LiveUpdate web api. The valid API is:" ]
        p [ ] [ str "- PUT "; a [ _href "/update" ] [ str "/update" ] ]
      ]
    ]

  type UpdateResponse =
    { message: string }

  let rec tryFindMemberByName name (decls: DDecl[]) =
    decls |> Array.tryPick (function
        | DDeclEntity (_, ds) -> tryFindMemberByName name ds
        | DDeclMember (membDef, body) ->
          if membDef.Name = name then Some (membDef, body) else None
        | _ -> None)


  let handleUpdate (middleware: HotReloadGiraffeMiddleware) : HttpHandler = fun next ctx -> task {
    let logger = ctx.GetLogger<HotReloadGiraffeMiddleware>()
    let error message = setStatusCode 400 >=> json { message = message }
    let interpreter = EvalContext(System.Reflection.Assembly.Load)

    let tryFindWebAppInFile (file: DFile) =
      tryFindMemberByName "webApp" file.Code

    let tryFindWebApp (files: DFile []) = files |> Array.choose tryFindWebAppInFile |> Array.tryHead

    let tryGetAsValue (parent: ResolvedEntity) (def: DMemberDef) =
      try
        let memberRef = interpreter.GetExprDeclResult(parent, def.Name)

        match getVal memberRef with
        | :? HttpHandler as handler -> Ok handler
        | _ -> Error "Not an httphandler"
      with
      | e -> Error "couldn't resolve the name as a value"

    let tryGetAsFunction (parent: ResolvedEntity) (def: DMemberDef) =
      match interpreter.EvalMethodLambda(emptyEnv, false, def.GenericParameters, def.Parameters, def) with
      | MethodLambdaValue.MethodLambdaValue (f) -> ()
      match interpreter.ResolveMethod(def.Ref) with
      | RMethod minfo ->
        let meth = minfo :?> MethodInfo
        let methodParams = meth.GetParameters() |> Array.map (fun p -> p.ParameterType)
        Ok (meth, methodParams)
      | UMember (Value v) ->
        match v with
        | :? MethodLambdaValue as l ->

          let parameters = types |> Array.map (fun t -> ctx.RequestServices.GetService(t))

        | other ->
          Error (sprintf "Got a value of type %A" (v.GetType()))
      | a -> Error (sprintf "Not a method info, instead was a %A" (a.GetType()))

    let makeParametersAndInvokeMethod (paramTypes: Type []) (methodInfo: MethodInfo): HttpHandler =
      let injectedParameters = paramTypes |> Array.map (fun t -> ctx.RequestServices.GetService(t))
      methodInfo.Invoke(null, injectedParameters) :?> HttpHandler

    let tryEvalMember (def: DMemberDef, bodyExpr: DExpr) =
      let parent = interpreter.ResolveEntity(def.EnclosingEntity)

      let handler =
        match tryGetAsValue parent def with
        | Ok handler -> Ok handler
        | Error valueMessage ->
          match tryGetAsFunction parent def with
          | Ok (methodInfo, paramTypes) ->
            Ok <| makeParametersAndInvokeMethod paramTypes methodInfo
          | Error fnMessage ->
            Error (valueMessage, fnMessage)

      match handler with
      | Ok handler ->
          logger.LogInformation("updating Giraffe handler with new member {mamberName} from {enclosingType}", def.Name, (def.EnclosingEntity.GetType().FullName))
          middleware.Update handler
          Ok "The handler has been changed"
      | Error (valueMessage, fnMessage) ->
        logger.LogError(valueMessage)
        logger.LogError(fnMessage)
        Error (sprintf "%s\n%s" valueMessage fnMessage)

    let updateFiles (files: DFile[]) =
      lock interpreter (fun () ->
        files |> Array.iter (fun file -> interpreter.AddDecls file.Code)
        files |> Array.iter (fun file -> interpreter.EvalDecls (envEmpty, file.Code))
      )
      match files with
      | [|  |] ->
        logger.LogError("No files found in request")
        error "Must send some files"
      | files ->
        match tryFindWebApp files with
        | Some webAppMember ->
          match tryEvalMember webAppMember with
          | Ok result ->
            setStatusCode 200 >=> json { message = result }
          | Error errMsg -> error errMsg
        | None ->
          logger.LogError("Couldn't find a member called `webApp` with signature `Giraffe.HttpHandler`")
          error "Couldn't find a member called `webApp` with signature `Giraffe.HttpHandler`"

    return! bindJson updateFiles next ctx
  }

  let updater middleware: HttpHandler =
    route "/update" >=> choose [
      GET >=> htmlView welcomePage
      PUT >=> handleUpdate middleware
    ]

  /// A middleware that delegates to GiraffeMiddlware, but updates the middleware when the HttpHandler is updated
  type HotReloadGiraffeMiddleware(next: RequestDelegate,
                                  handler: HttpHandler,
                                  sockets: ResizeArray<System.Net.WebSockets.WebSocket>,
                                  loggerFactory: ILoggerFactory) as self =
        let logger = loggerFactory.CreateLogger<HotReloadGiraffeMiddleware>()
        let merge handler = choose [LiveUpdate.updater self; handler ]
        let refreshCommand = "refresh" |> System.Text.Encoding.UTF8.GetBytes |> ArraySegment
        let mutable innerMiddleware = Middleware.GiraffeMiddleware(next, merge handler, loggerFactory)

        member __.Invoke (ctx: HttpContext) = innerMiddleware.Invoke ctx

        /// Replace the current giraffe pipeline with a new one based on the provided `HttpHandler`
        member __.Update(newHandler) =
          innerMiddleware <- Middleware.GiraffeMiddleware(next, merge newHandler, loggerFactory)
          sockets
          |> Seq.mapi (fun i socket -> task {
              logger.LogInformation("Notifying websocket {id}", i)
              do! socket.SendAsync(refreshCommand, Net.WebSockets.WebSocketMessageType.Text, true, Threading.CancellationToken.None)
            }
          )
          |> System.Threading.Tasks.Task.WhenAll
          |> ignore


[<AutoOpen>]
module Extensions =
  open System
  open Giraffe
  open Microsoft.Extensions.Logging
  open Microsoft.AspNetCore.Http
  open Microsoft.AspNetCore.Builder
  open FSharp.Control.Tasks.V2.ContextInsensitive
  open System.Threading.Tasks

  type IApplicationBuilder with
    member this.UseGiraffeWithHotReload(handler: HttpHandler) =
      let loggerFactory  = this.ApplicationServices.GetService(typeof<ILoggerFactory>) :?> ILoggerFactory
      let logger = loggerFactory.CreateLogger("Giraffe.HotReload.Websockets")
      // keep a list of websockets so that we can mutably append to it and share it with the middleware
      let sockets = ResizeArray<_>()

      let rec listenToSocket (socket: System.Net.WebSockets.WebSocket) = async {
        let buffer = ArraySegment<byte>(Array.zeroCreate 4086)
        try
          let! ct = Async.CancellationToken
          let! result = socket.ReceiveAsync(buffer, ct) |> Async.AwaitTask
          if result.MessageType = Net.WebSockets.WebSocketMessageType.Close
          then
            logger.LogInformation("Websocket closing")
            sockets.Remove socket |> ignore
          else
            logger.LogInformation("Websocket message: {message}", System.Text.Encoding.UTF8.GetString buffer.Array)
            return! listenToSocket socket
        with
        | e ->
          logger.LogError ("Websocket Error: {error}", e)
          sockets.Remove socket |> ignore
      }

      let registerSocket (ctx: HttpContext) (next: System.Func<Task>) =
        task {
          if ctx.Request.Path = PathString.op_Implicit"/ws" then
            if ctx.WebSockets.IsWebSocketRequest
            then
              let! socket = ctx.WebSockets.AcceptWebSocketAsync()
              sockets.Add socket
              do! listenToSocket socket
            else
              ctx.Response.StatusCode <- 400
              return ()

          do! next.Invoke()


        } :> Task

      // add in websocket support so that we can send a 'refresh' ping
      this.UseWebSockets() |> ignore
      // accept websockets and register them
      this.Use(System.Func<_, _, _> registerSocket) |> ignore
      // invoke our liveupdate middleware
      this.UseMiddleware<LiveUpdate.HotReloadGiraffeMiddleware>(handler, sockets) |> ignore

