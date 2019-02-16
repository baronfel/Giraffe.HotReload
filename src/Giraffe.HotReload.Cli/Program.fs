// F# Compiler Daemon sample
//
// Sample use, assumes app has a reference to ELmish.XamrinForms.LiveUpdate:
//
// cd Fabulous\Samples\CounterApp\CounterApp
//   adb -d forward  tcp:9867 tcp:9867
// dotnet run --project ..\..\..\Fabulous.Cli\Fabulous.Cli.fsproj -- --eval @out.args
// dotnet run --project ..\..\..\Fabulous.Cli\Fabulous.Cli.fsproj -- --watch --webhook:http://localhost:9867/update @out.args

open FSharp.Compiler.PortaCode.ProcessCommandLine

[<EntryPoint>]
let main argv =
    try
      ProcessCommandLine argv
    with e ->
      printfn "Error: %s\n\t%s" e.Message e.StackTrace
      1
