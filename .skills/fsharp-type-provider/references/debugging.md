# Debugging F# Type Providers

Type provider code runs **inside another process** — the F# compiler (`fsc`), F# Interactive (`fsi`), Visual Studio, or Rider. To debug it you must attach to that process and trigger your provider's code paths.

## Recipe 1 — Debug in `fsc.exe` (compiler)

1. From the consumer project directory, run a verbose build to capture the compiler's command line:
   ```text
   dotnet build -v:n > build.log
   ```
2. Find the `fsc.exe` invocation; copy the `@response.rsp` argument path or the full args.
3. Launch the debugger:
   ```text
   devenv /debugexe "C:\Program Files\dotnet\dotnet.exe" \
                    "C:\Program Files\dotnet\sdk\<ver>\FSharp\fsc.exe" @response.rsp
   ```
4. Set debug type ".NET Core" (right-click `dotnet.exe` → properties).
5. Place breakpoints in your provider's `createTypes`, `DefineStaticParameters` callback, etc.
6. Press F5.

## Recipe 2 — Debug in `dotnet fsi`

```text
devenv /debugexe "C:\Program Files\dotnet\dotnet.exe" fsi script.fsx
```

…where `script.fsx` references the runtime DLL and uses your provider. Faster turnaround than rebuilding a full project — useful for quick reproductions.

## Recipe 3 — Debug in Visual Studio

```text
devenv /debugexe devenv.exe MyProj.fsproj
```

Set debug type to ".NET Framework 4.0". The new VS instance loads the project; opening any file that uses your provider triggers TP execution.

## Recipe 4 — Debug in Rider / Ionide

Rider: Run → Edit Configurations → add a .NET Project; target the JetBrains.Rider.Backend or attach to the existing IDE process. Less ergonomic — usually quicker to use Recipe 1 against a `.fsx`.

## Critical settings inside the debugger

- **First-chance exceptions on**: `Ctrl+Alt+E` → tick "Common Language Runtime Exceptions" → Thrown. Many TP failures bubble up as silently-caught exceptions inside the compiler; you'll never see them otherwise.
- **Just My Code off**: Tools → Options → Debugging → uncheck "Enable Just My Code". TP code lives in a different assembly load context; JMC misclassifies it.

## Common error messages and root causes

| Error | Likely cause |
|---|---|
| `Could not load file or assembly 'YamlDotNet, ...'` | Design-time dep not bundled in `typeproviders/fsharp41/<tfm>/`; see [packaging.md](packaging.md). |
| `The type provider 'MyProvider.MyErasingProvider' reported an error: <X>` | Exception thrown in provider construction or static-parameter callback. The `<X>` text is your exception's `.Message`. |
| `Type mismatch when splicing expression` | `args.[i]` indexed wrong (erased ↔ generative confusion). |
| `InvalidOperationException: Specified method is not supported` (in metadata token access) | Used `Type.MakeGenericType` instead of `ProvidedTypeBuilder.MakeGenericType`. |
| `Type 'X' is not defined` (when consumer references a generative type) | Forgot `provAsm.AddTypes [t]` or `AddNestedTypes`. |
| `error FS3033: The type provider ... reported an error: error FS3173: A type provider returned 'null' from GetType` | Provider returned `null` from `GetTypes()` — usually a bug in `createTypes` returning an empty list when expected. |
| Consumer compiles but `MyProvider.Provided` namespace is empty | Missing `[<assembly: TypeProviderAssembly("...DesignTime.dll")>]` on Runtime DLL, *or* missing `IsFSharpDesignTimeProvider=true` in the Runtime → DesignTime project reference. |

## Logging for hard cases

The provider runs in a host process you don't own; `printfn` may go nowhere. Three workable channels:

1. **`System.Diagnostics.Debugger.Log`** — visible in the attached debugger's Output window.
2. **`File.AppendAllText("/tmp/tp.log", msg + "\n")`** — simple and reliable; `tail -f /tmp/tp.log` in another terminal.
3. **Throw an exception with diagnostic data** — F# surfaces the message inline at the consumer's source location. Crude but effective for "what value did this static parameter receive".

For a long-running investigation, instrument your provider with conditional logging guarded by an env-var check:

```fsharp
let log msg =
    if System.Environment.GetEnvironmentVariable "MYPROVIDER_TRACE" = "1" then
        System.IO.File.AppendAllText("/tmp/myprovider.log", msg + "\n")
```

## "It works in my unit test but not in a consumer project"

Almost always one of:
1. Missing `TypeProviderAssembly` attribute on the runtime DLL.
2. Missing or wrong `assemblyReplacementMap` — design-time-assembly types are leaking into spliced quotations.
3. Design-time dep not bundled in the package (works in unit tests because they reference the design-time project directly, with all deps available locally).

Check in that order. The fastest diagnostic: build the consumer with `dotnet build -bl`, open the resulting `msbuild.binlog` in MSBuild Structured Log Viewer, and search for "TypeProvider" — you'll usually see the exact load failure.

## Reloading a provider in IDE without restart

VS / Rider keep design-time DLLs loaded. After rebuilding the provider, the IDE often serves stale cached types. Workarounds:
- Close and reopen the consuming `.fs` file.
- If that fails, restart the IDE.
- Add `[<assembly: System.Reflection.AssemblyVersion("...")>]` with a different version to your design-time DLL each build to force the host to reload.

This is a known limitation; budget for it.
