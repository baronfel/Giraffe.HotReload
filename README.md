# Giraffe.HotReload

A repo to explore using [FSharp.Compiler.Portacode](https://github.com/fsprojects/FSharp.Compiler.PortaCode) for hot-reloading Giraffe Views

To test the current set up:

* `cd` to the `samples/ReloadSample` directory
* run the `Giraffe.HotReload.Cli` project from that working directory to generate the project options
  * `dotnet run --project ../../src/Giraffe.HotReload.Cli/Giraffe.HotReload.Cli.fsproj -- --eval @out.args`
  * you may need to ensure that file exists first
* run the `Giraffe.HotReload.Cli` project from that working directory in watch mode
  * `dotnet run --project ../../src/Giraffe.HotReload.Cli/Giraffe.HotReload.Cli.fsproj -- --watch --webhook:http://localhost:5000/update @out.args`
* run the `ReloadSample` project
  * `dotnet run`
* make changes to the `ReloadSample` project

#### Enabling auto-refresh of your page

The new middleware exposes a websocket-friendly connection at `localhost:5000/ws`, and if you include a simple script like in your root page template every page in your app will support hot-refresh.
The important part is the `onmessage` handler, where the page is refreshed when a message is sent.

```js
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
``` 

---

## Builds

MacOS/Linux | Windows
--- | ---
[![Travis Badge](https://travis-ci.org/baronfel/Giraffe.HotReload.svg?branch=master)](https://travis-ci.org/baronfel/Giraffe.HotReload) | [![Build status](https://ci.appveyor.com/api/projects/status/github/baronfel/Giraffe.HotReload?svg=true)](https://ci.appveyor.com/project/baronfel/Giraffe.HotReload)
[![Build History](https://buildstats.info/travisci/chart/baronfel/Giraffe.HotReload)](https://travis-ci.org/baronfel/Giraffe.HotReload/builds) | [![Build History](https://buildstats.info/appveyor/chart/baronfel/Giraffe.HotReload)](https://ci.appveyor.com/project/baronfel/Giraffe.HotReload)  


## Nuget 

Stable | Prerelease
--- | ---
[![NuGet Badge](https://buildstats.info/nuget/Giraffe.HotReload)](https://www.nuget.org/packages/Giraffe.HotReload/) | [![NuGet Badge](https://buildstats.info/nuget/Giraffe.HotReload?includePreReleases=true)](https://www.nuget.org/packages/Giraffe.HotReload/)

---

### Building


Make sure the following **requirements** are installed in your system:

* [dotnet SDK](https://www.microsoft.com/net/download/core) 2.0 or higher
* [Mono](http://www.mono-project.com/) if you're on Linux or macOS.

```
> build.cmd // on windows
$ ./build.sh  // on unix
```

#### Environment Variables

* `CONFIGURATION` will set the [configuration](https://docs.microsoft.com/en-us/dotnet/core/tools/dotnet-build?tabs=netcore2x#options) of the dotnet commands.  If not set it will default to Release.
  * `CONFIGURATION=Debug ./build.sh` will result in things like `dotnet build -c Debug`
* `GITHUB_TOKEN` will be used to upload release notes and nuget packages to github.
  * Be sure to set this before releasing

### Watch Tests

The `WatchTests` target will use [dotnet-watch](https://github.com/aspnet/Docs/blob/master/aspnetcore/tutorials/dotnet-watch.md) to watch for changes in your lib or tests and re-run your tests on all `TargetFrameworks`

```
./build.sh WatchTests
```

### Releasing
* [Start a git repo with a remote](https://help.github.com/articles/adding-an-existing-project-to-github-using-the-command-line/)

```
git add .
git commit -m "Scaffold"
git remote add origin origin https://github.com/user/MyCoolNewLib.git
git push -u origin master
```

* [Add your nuget API key to paket](https://fsprojects.github.io/Paket/paket-config.html#Adding-a-NuGet-API-key)

```
paket config add-token "https://www.nuget.org" 4003d786-cc37-4004-bfdf-c4f3e8ef9b3a
```

* [Create a GitHub OAuth Token](https://help.github.com/articles/creating-a-personal-access-token-for-the-command-line/)
    * You can then set the `GITHUB_TOKEN` to upload release notes and artifacts to github
    * Otherwise it will fallback to username/password


* Then update the `RELEASE_NOTES.md` with a new version, date, and release notes [ReleaseNotesHelper](https://fsharp.github.io/FAKE/apidocs/fake-releasenoteshelper.html)

```
#### 0.2.0 - 2017-04-20
* FEATURE: Does cool stuff!
* BUGFIX: Fixes that silly oversight
```

* You can then use the `Release` target.  This will:
    * make a commit bumping the version:  `Bump version to 0.2.0` and add the release notes to the commit
    * publish the package to nuget
    * push a git tag

```
./build.sh Release
```


### Code formatting

To format code run the following target

```
./build.sh FormatCode
```

This uses [Fantomas](https://github.com/fsprojects/fantomas) to do code formatting.  Please report code formatting bugs to that repository.
