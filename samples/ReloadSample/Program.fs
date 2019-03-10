module ReloadSample.App

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Giraffe.HotReload
open System.Net.Http
open Microsoft.AspNetCore.Http

// ---------------------------------
// Models
// ---------------------------------

type Message =
    {
        Text : string
    }

// ---------------------------------
// Views
// ---------------------------------

module Views =
    open GiraffeViewEngine

    let wsConnection =
      script [] [
        rawText """
var socket = new WebSocket('ws://localhost:5000/ws');
socket.onopen = function(event) {
  console.log('Connection opened');
}
socket.onmessage = function(event) {
  console.log(event.data);
  window.location.reload();
  return false;
}
socket.onclose = function(event) {
  console.log("connection closed");
}
socket.onerror = function(error) {
  console.log("error", error);
}
"""
        ]

    let layout (content: XmlNode list) =
        html [] [
            head [] [
                title []  [ encodedText "ReloadSample" ]
                link [ _rel  "stylesheet"
                       _type "text/css"
                       _href "/css/main.css" ]
            ]
            body [] (content @ [wsConnection])
        ]

    let partial () =
        h1 [] [ encodedText "ReloadSample with changes!" ]

    let index (model : Message) =
        [
            partial()
            p [] [ encodedText model.Text ]
        ] |> layout

// ---------------------------------
// Web app
// ---------------------------------

let indexHandler (name : string) =
    let greetings = sprintf "Hello earthling %s, from Giraffe!" name
    let model     = { Text = greetings }
    let view      = Views.index model
    htmlView view

let webApp (env: IHostingEnvironment) : HttpHandler =
    choose [
        GET >=>
            choose [
                route "/" >=> indexHandler (sprintf "world in the %s environment" env.EnvironmentName)
                routef "/hello/%s" indexHandler
            ]
        setStatusCode 404 >=> text "Not Found" ]

// ---------------------------------
// Error handler
// ---------------------------------

let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

// ---------------------------------
// Config and Main
// ---------------------------------


let configureApp (app : IApplicationBuilder) =
    let env = app.ApplicationServices.GetService<IHostingEnvironment>()
    let app = app.UseStaticFiles()
    (match env.IsDevelopment() with
    | true  -> app.UseDeveloperExceptionPage()
    | false -> app.UseGiraffeErrorHandler errorHandler)
#if DEBUG
        .UseGiraffeWithHotReload(webApp env)
#else
        .UseGiraffe(webApp env)
#endif
    |> ignore

let configureServices (services : IServiceCollection) =
    services.AddGiraffe() |> ignore

let configureLogging (builder : ILoggingBuilder) =
    builder
           .AddConsole()
           .AddDebug() |> ignore

[<EntryPoint>]
let main _ =
    // let contentRoot = Directory.GetCurrentDirectory()
    let webroot     = "wwwroot"
    WebHostBuilder()
        .UseKestrel()
        // .UseContentRoot(contentRoot)
        .UseIISIntegration()
        .UseWebRoot(webroot)
        .Configure(Action<IApplicationBuilder> configureApp)
        .ConfigureServices(configureServices)
        .ConfigureLogging(configureLogging)
        .Build()
        .Run()
    0
