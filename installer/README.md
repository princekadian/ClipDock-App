# ClipDock Installer

Build prerequisites:

- .NET 8 SDK
- Inno Setup 6

Build the self-contained x64 payload:

```powershell
dotnet publish ..\src\ClipDock.App\ClipDock.App.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o ..\artifacts\publish\1.0.0-win-x64-final
```

Compile the installer:

```powershell
iscc ClipDock.iss
```

The output is written to `artifacts\installer`.
