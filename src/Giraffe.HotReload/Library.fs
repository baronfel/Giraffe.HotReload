namespace Giraffe.HotReload
module rec LiveUpdate =
  open System
  open System.Reflection
  open System.Threading.Tasks
  open Giraffe
  open GiraffeViewEngine
  open Microsoft.AspNetCore.Http
  open Microsoft.Extensions.Logging
  open FSharp.Compiler.PortaCode.CodeModel
  open FSharp.Compiler.PortaCode.Interpreter
  open FSharp.Control.Tasks.V2.ContextInsensitive
  open Microsoft.AspNetCore.Hosting
  open System.Net.WebSockets
  open System.IO
  open Microsoft.Extensions.Primitives
  open Microsoft.Extensions.FileProviders

  type Settings = {
    /// The route where the hot reload tool should post.
    UpdateRoute : string
    /// The route for the websocket that will refresh the browser.
    WebsocketRefreshRoute : string
    /// The name of the Giraffe HttpHandler member that will be searched for
    WebAppMemberName : string
    /// Static file providers for anything not under webroot
    StaticFileProviders : IFileProvider list
  }
    with
      static member Default = {
        UpdateRoute = "/update"
        WebsocketRefreshRoute = "/ws"
        WebAppMemberName = "webApp"
        StaticFileProviders = []
      }

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
        | DDeclMember (membDef, body, _range) -> if membDef.Name = name then Some (membDef, body) else None
        | _ -> None)


  let handleUpdate (middleware: HotReloadGiraffeMiddleware) memberName : HttpHandler =
    let error message = setStatusCode 400 >=> json { message = message }
    let interpreter = EvalContext(System.Reflection.Assembly.Load)

    let tryFindWebAppInFile (fileName, file) = tryFindMemberByName memberName file.Code

    let tryFindWebApp (files: (string * DFile) []) = files |> Array.filter (fun (fileName, file) -> box file.Code <> null) |> Array.choose tryFindWebAppInFile |> Array.tryHead

    let getTypeForRef (logger: ILogger) (ref: DEntityRef) =
        let (DEntityRef refName) = ref
        match interpreter.ResolveEntity(ref) with
        | REntity t ->
          logger.LogTrace("type {refType} was resolved to type {realType}", refName, t.FullName)
          t
        | _ -> failwith "boom"

    let rec resolveType logger (t: DType) =
      match t with
      | DNamedType (ref, [||]) -> getTypeForRef logger ref
      | DNamedType (ref, typeArgs) ->
          let baseTy = getTypeForRef logger ref
          let tyArgs = typeArgs |> Array.map (resolveType logger)
          baseTy.MakeGenericType(tyArgs)
      | DFunctionType (inTy, outTy) -> typedefof<FSharpFunc<_,_>>.MakeGenericType([| resolveType logger inTy; resolveType logger outTy |])
      | DArrayType (len, elemTy) -> typedefof<_ []>.MakeGenericType([| resolveType logger elemTy |])
      | DByRefType _
      | DVariableType _
      | DTupleType _ -> typeof<unit>

    let methodLambdaValueToHandler (logger: ILogger) (resolver: Type -> obj) (def: DMemberDef) (mlv: MethodLambdaValue) =
      let resolvedReturnType = resolveType logger def.ReturnType
      if resolvedReturnType <> typeof<HttpHandler>
      then
        logger.LogWarning ("Not of correct final return type. Expected HttpHandler, got {type}", resolvedReturnType.FullName)
        Error ("bad return type")
      else
        let typeParameters =
            // TODO: resolve def.GenericParameters
            def.GenericParameters
        let parameters =
          logger.LogInformation("deriving parameters for lambda")
          let parameterTypes = def.Parameters |> Array.map (fun t -> match t.Type with | DNamedType (typeRef, types) -> getTypeForRef logger typeRef | _ -> typeof<unit> )
          logger.LogInformation ("have {count} parameter types", parameterTypes.Length)
          let parameterObjects = parameterTypes |> Array.map (fun t ->
            let v = resolver t
            logger.LogInformation("resolve instance of {type}", t.FullName)
            v
          )
          logger.LogInformation ("have {count} parameter instances", parameterObjects.Length)
          parameterObjects

        let (MethodLambdaValue lambda) = mlv
        let h = lambda ([||], parameters) :?> HttpHandler
        logger.LogInformation ("handler created")
        Ok h

    let tryResolveAsValue (logger: ILogger) (resolver: Type -> obj) (def: DMemberDef, bodyExpr: DExpr) =
      try
        let entity = interpreter.ResolveEntity(def.EnclosingEntity)
        let (def, memberValue) = interpreter.GetExprDeclResult(entity, def.Name)

        match getVal memberValue with
        | :? HttpHandler as handler ->
          logger.LogInformation("updating Giraffe handler with new member {memberName} from {enclosingType}", def.Name, (def.EnclosingEntity.GetType().FullName))
          Ok handler
        | :? MethodLambdaValue as mlv ->
          methodLambdaValueToHandler logger resolver def mlv
        | other ->
          logger.LogWarning("The handler was of the wrong type. Expected an `HttpHandler` but got an {finalType}", (sprintf "%A" (other.GetType())))
          Error (sprintf "The handler was of the wrong type. Expected an `HttpHandler` but got an `%A`" (other.GetType()))
      with
      | e ->
        logger.LogWarning ("Error while doing the value: {err}", e)
        Error (sprintf "Error while doing the value: %s\n\t%s" e.Message e.StackTrace)

    let tryResolveAsFunction (logger: ILogger) resolver (def: DMemberDef, bodyExpr: DExpr) : Result<HttpHandler, string> =
      try

        let entity = interpreter.ResolveEntity(def.EnclosingEntity)
        let meth = interpreter.ResolveMethod(def.Ref)

        match meth with
        | RMethod m ->
          let meth = m :?> MethodInfo
          if meth.ReturnType <> typeof<HttpHandler>
          then
            logger.LogWarning ("method must have a final return type of HttpHandler, was {retTy}", meth.ReturnType.FullName)
            Error (sprintf "method must have a final return type of HttpHandler, was %s" meth.ReturnType.FullName)
          else
            let parameterTypes = meth.GetParameters() |> Array.map (fun p -> p.ParameterType)
            let parameterInstances = parameterTypes |> Array.map resolver
            meth.Invoke(null, parameterInstances) :?> HttpHandler |> Ok
        | UMethod (def, Value value) when value.GetType() = typeof<MethodLambdaValue> ->
          methodLambdaValueToHandler logger resolver def (value :?> MethodLambdaValue)
        | UMethod (def, Value value) ->
          logger.LogInformation ("value type was {ty}", (value.GetType()))
          Error "UMethod"
        | n ->
          logger.LogWarning ("method was some other type {ty}", (n.GetType()))
          Error "method was of the wrong type"
      with
      | e ->
        logger.LogWarning ("Error while doing the function: {err}", e)
        Error (sprintf "Error while doing the function: %s\n\t%s" e.Message e.StackTrace)

    let tryEvalMember (resolver: Type -> obj) (logger: ILogger) (def: DMemberDef, bodyExpr: DExpr) =
      let entity = interpreter.ResolveEntity(def.EnclosingEntity)

      let found = [tryResolveAsValue logger resolver
                   tryResolveAsFunction logger resolver ]
                  |> List.tryPick (fun f -> match f (def, bodyExpr) with | Ok handler -> Some handler | Error msg -> None)

      match found with
      | Some handler ->
        middleware.Update handler
        Ok "handler updated!"
      | None ->
        Error "No handler could be found"

    let updateFiles resolver (logger: ILogger) (files: (string * DFile)[]) =
      lock interpreter (fun () ->
        files |> Array.iter (fun (fileName, file) -> if file.Code <> null then interpreter.AddDecls file.Code)
        files |> Array.iter (fun (fileName, file) -> if file.Code <> null then interpreter.EvalDecls (envEmpty, file.Code))
      )
      match files with
      | null
      | [|  |] ->
        logger.LogError("No files found in request")
        error "Must send some files"
      | files ->
        match tryFindWebApp files with
        | Some webAppMember ->
          match tryEvalMember resolver logger webAppMember with
          | Ok result ->
            setStatusCode 200 >=> json { message = result }
          | Error errMsg ->
            logger.LogError errMsg
            error errMsg
        | None ->
          let errMsg = sprintf """Couldn't find a member called `%s` that was either
  a) a `Giraffe.HttpHandler`, or
  b) a function of N parameters that returns a `Giraffe.HttpHandler` where each parameter can be uniquely resolved from the dependency injection container.""" memberName
          logger.LogError errMsg
          error errMsg

    fun next ctx -> task {
      let logger = ctx.GetLogger<HotReloadGiraffeMiddleware>()
      let resolver ty = ctx.RequestServices.GetService(ty)
      return! bindJson (updateFiles resolver logger) next ctx
    }

  let updater (settings : Settings) middleware: HttpHandler =
    route settings.UpdateRoute >=> choose [
      GET >=> htmlView welcomePage
      PUT >=> handleUpdate middleware settings.WebAppMemberName
    ]

  /// A middleware that delegates to GiraffeMiddlware, but updates the middleware when the HttpHandler is updated
  type HotReloadGiraffeMiddleware(next: RequestDelegate,
                                  handler: HttpHandler,
                                  hostEnv : IHostingEnvironment,
                                  sockets: ResizeArray<System.Net.WebSockets.WebSocket>,
                                  settings : Settings,
                                  loggerFactory: ILoggerFactory) as self =
        let logger = loggerFactory.CreateLogger<HotReloadGiraffeMiddleware>()
        let merge handler = choose [LiveUpdate.updater settings self; handler ]
        let refreshCommand = "refresh" |> System.Text.Encoding.UTF8.GetBytes |> ArraySegment
        let sendRefreshCommand (socket : WebSocket) = socket.SendAsync(refreshCommand, Net.WebSockets.WebSocketMessageType.Text, true, Threading.CancellationToken.None)
        let notifyAllSockets sockets =
          sockets
          |> Seq.mapi (fun i socket -> task {
              logger.LogInformation("Notifying websocket {id}", i)
              do! sendRefreshCommand socket
            }
          )
          |> System.Threading.Tasks.Task.WhenAll
          |> ignore

        let handleFileChange () =
          logger.LogDebug("Static content file changed. Sending refresh commands")
          notifyAllSockets sockets

        let addWatchHandlers (fileProvider : IFileProvider) =
          let getWatchToken _ = fileProvider.Watch("**.*")
          ChangeToken.OnChange(Func<_>(getWatchToken), Action(handleFileChange))
          |> ignore

        let filewatchers = hostEnv.WebRootFileProvider :: settings.StaticFileProviders

        do filewatchers |> List.iter(addWatchHandlers)

        let mutable innerMiddleware = Middleware.GiraffeMiddleware(next, merge handler, loggerFactory)

        member __.Invoke (ctx: HttpContext) = innerMiddleware.Invoke ctx

        /// Replace the current giraffe pipeline with a new one based on the provided `HttpHandler`
        member __.Update(newHandler) =
          innerMiddleware <- Middleware.GiraffeMiddleware(next, merge newHandler, loggerFactory)
          notifyAllSockets sockets


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
    member this.UseGiraffeWithHotReload(handler: HttpHandler, settings : LiveUpdate.Settings) =
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
          if ctx.Request.Path = PathString.op_Implicit settings.WebsocketRefreshRoute then
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
      this.UseMiddleware<LiveUpdate.HotReloadGiraffeMiddleware>(handler, sockets, settings) |> ignore

    member this.UseGiraffeWithHotReload(handler: HttpHandler) =
      this.UseGiraffeWithHotReload(handler, LiveUpdate.Settings.Default)
