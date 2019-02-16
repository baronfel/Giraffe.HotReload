module Tests

module View =
  open Giraffe.GiraffeViewEngine

  let layout (content: XmlNode list) =
    html [] [
        head [] [
            title []  [ str "Giraffe" ]
        ]
        body [] content
    ]

  let index =
    layout [
      div [ _class "container" ] [
        h3 [_title "Welcome"] [str ("Hello from Giraffe.HotReload")]
      ]
    ]

module Harness =
  open System
  open Giraffe
  open Giraffe.HotReload
  open Microsoft.AspNetCore
  open Microsoft.AspNetCore.Builder
  open Microsoft.AspNetCore.Hosting
  open Microsoft.Extensions.DependencyInjection

  let webApp =
    route "/" >=> htmlView View.index

  let configureApp (app: IApplicationBuilder) =
    app.UseGiraffeWithHotReload webApp
    |> ignore


  let configureServices (svcs: IServiceCollection) =
      svcs.AddGiraffe()
      |> ignore

  let builder =
    WebHost.CreateDefaultBuilder()
      .Configure(Action<_> configureApp)
      .ConfigureServices(Action<_> configureServices)

open Expecto
open Microsoft.AspNetCore.TestHost
open System.Net.Http
open System.Net

let testsWithServer category tests =
  let server = new TestServer(Harness.builder)
  testList category [
    for test in tests -> test server
  ]

[<Tests>]
let tests =
  testsWithServer "live update" [
    fun server -> testTask "can delegate to normal giraffe middleware"  {
      let! (response: HttpResponseMessage) = server.CreateRequest("/").GetAsync()
      Expect.equal response.StatusCode HttpStatusCode.OK "should have retrieved the root"

      let! body = response.Content.ReadAsStringAsync ()
      Expect.stringContains body "Hello from Giraffe.HotReload" "should contain welcome message"
    }
    fun server -> testTask "can access the liveUpdate welcome page" {
      let! (response: HttpResponseMessage) = server.CreateRequest("/update").GetAsync()
      Expect.equal response.StatusCode HttpStatusCode.OK "should have retrieved the welcome page"

      let! body = response.Content.ReadAsStringAsync ()
      Expect.stringContains body "Welcome to LiveUpdate web api" "should contain the liveupdate welcome message"
    }
  ]
