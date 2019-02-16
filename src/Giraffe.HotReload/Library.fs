namespace Giraffe.HotReload

module rec LiveUpdate =
  open Giraffe
  open GiraffeViewEngine
  open Microsoft.AspNetCore.Http
  open Microsoft.Extensions.Logging
  open FSharp.Compiler.PortaCode.CodeModel
  open FSharp.Compiler.PortaCode.Interpreter

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
        | DDeclMember (membDef, body) -> if membDef.Name = name then Some (membDef, body) else None
        | _ -> None)


  let handleUpdate (middleware: HotReloadGiraffeMiddleware) : HttpHandler =
    let error message = setStatusCode 400 >=> json { message = message }
    let interpreter = EvalContext(System.Reflection.Assembly.Load)

    let tryFindWebAppInFile (file: DFile) =
      tryFindMemberByName "webApp" file.Code

    let tryFindWebApp (files: DFile []) = files |> Array.choose tryFindWebAppInFile |> Array.tryHead

    let tryEvalMember (def: DMemberDef, bodyExpr: DExpr) =
//      if not def.IsValueDef then Error "The `webApp` member must have no parameters and be a static value"
//      else

      let entity = interpreter.ResolveEntity(def.EnclosingEntity)
      let memberRef = interpreter.GetExprDeclResult(entity, def.Name)

      match getVal memberRef with
      | :? HttpHandler as handler ->
        middleware.Update handler
        Ok "The handler has been changed"
      | other ->
        Error (sprintf "The handler was of the wrong type. Expected an `HttpHandler` but got an `%A`" (other.GetType()))

    let updateFiles (files: DFile[]) =
      lock interpreter (fun () ->
        files |> Array.iter (fun file -> interpreter.AddDecls file.Code)
        files |> Array.iter (fun file -> interpreter.EvalDecls (envEmpty, file.Code))
      )
      match files with
      | [|  |] -> error "Must send some files"
      | files ->
        match tryFindWebApp files with
        | Some webAppMember ->
          match tryEvalMember webAppMember with
          | Ok result ->
            setStatusCode 200 >=> json { message = result }
          | Error errMsg -> error errMsg
        | None -> error "Couldn't find a member called `webApp` with signature `Giraffe.HttpHandler`."

    bindJson updateFiles

  let updater middleware: HttpHandler =
    route "/update" >=> choose [
      GET >=> htmlView welcomePage
      PUT >=> handleUpdate middleware
    ]

  /// A middleware that delegates to GiraffeMiddlware, but updates the middleware when the HttpHandler is updated
  type HotReloadGiraffeMiddleware(next: RequestDelegate,
                                      handler: HttpHandler,
                                      loggerFactory: ILoggerFactory) as self =
        let merge handler = choose [LiveUpdate.updater self; handler ]

        let mutable innerMiddleware = Middleware.GiraffeMiddleware(next, merge handler, loggerFactory)

        member __.Invoke (ctx: HttpContext) = innerMiddleware.Invoke ctx

        /// Replace the current giraffe pipeline with a new one based on the provided `HttpHandler`
        member __.Update(newHandler) =
          innerMiddleware <- Middleware.GiraffeMiddleware(next, merge newHandler, loggerFactory)


[<AutoOpen>]
module Extensions =
  open Giraffe
  open Microsoft.Extensions.Logging
  open Microsoft.AspNetCore.Http
  open Microsoft.AspNetCore.Builder

  type IApplicationBuilder with
    member this.UseGiraffeWithHotReload(handler: HttpHandler) =
      let loggerFactory  = this.ApplicationServices.GetService(typeof<ILoggerFactory>) :?> ILoggerFactory

      let run = fun (next: RequestDelegate) ->
        let mw = LiveUpdate.HotReloadGiraffeMiddleware(next, handler, loggerFactory)
        let del = RequestDelegate (fun ctx ->  mw.Invoke(ctx) :> _)
        del
      this.Use run
