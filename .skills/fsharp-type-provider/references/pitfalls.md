# Common Pitfalls and Failure Modes

Cross-referenced catalogue. When the user reports a symptom, find it here first; it almost certainly maps to one of these root causes.

---

## 1. `args.[0]` semantics swapped

**Symptom**: At consumer compile time, F# reports a type mismatch in the splice — "expected `int`, found `obj`" or similar — pointing inside a quotation.

**Cause**: `args.[i]` indexing rules differ between erased and generative providers and between static and instance members.

**Rules**:
- Erased *constructor*: `args.[0]` is the first parameter. (No `this`.)
- Erased *instance method/property*: `args.[0]` is the receiver, `args.[1]` is first param.
- Erased *static method/property*: `args.[0]` is first param.
- Generative *constructor*: `args.[0]` is `this`, `args.[1]` is first param.
- Generative *instance member*: `args.[0]` is `this`, `args.[1]` is first param.
- Generative *static member*: `args.[0]` is first param.

**Fix**: re-index every `args.[i]` matching the rules above.

---

## 2. Plain `Type.MakeGenericType` inside a quotation

**Symptom**: Consumer build fails with `InvalidOperationException` mentioning "metadata token", or "Type mismatch" on a generic instantiation that should be valid.

**Cause**: `Type.MakeGenericType` and `MethodInfo.MakeGenericMethod` produce reflection-only objects that lack metadata tokens. When the F# compiler later loads the assembly metadata, those tokens fail.

**Fix**:
```fsharp
let t = ProvidedTypeBuilder.MakeGenericType(genericTypeDef, [|typeArg|])
let m = ProvidedTypeBuilder.MakeGenericMethod(genericMethDef, [|typeArg|])
```

For methods built via these helpers, use `Expr.CallUnchecked` (from `ProviderImplementation.ProvidedTypes.UncheckedQuotations`) instead of `Expr.Call`.

---

## 3. Generative type with no constructor

**Symptom**: Consumer cannot instantiate the type, or compiler reports the type as having no accessible constructors.

**Cause**: Every generative type **must** have at least one `ProvidedConstructor`. The SDK does not synthesize a default constructor.

**Fix**:
```fsharp
let ctor = ProvidedConstructor([], invokeCode = fun _ -> <@@ () @@>)
myType.AddMember ctor
```

---

## 4. Forgot `provAsm.AddTypes`

**Symptom**: Consumer compile fails with "type X is not defined" or similar; the provided type is invisible at runtime.

**Cause**: For generative providers, every type **and every nested type** must be explicitly registered with the `ProvidedAssembly`.

**Fix**:
```fsharp
provAsm.AddTypes [myType]                       // root types
provAsm.AddNestedTypes([innerType], ["Outer"])  // nested types
```

---

## 5. Missing `TypeProviderAssembly` attribute

**Symptom**: Consumer references the runtime DLL successfully, but `MyProvider.Provided.*` is unresolved — no IntelliSense, no types.

**Cause**: The compiler needs an `[<assembly: CompilerServices.TypeProviderAssembly("...DesignTime.dll")>]` attribute on the runtime DLL to know which sibling DLL to load as the design-time component.

**Fix**: in the Runtime project's source:
```fsharp
[<assembly: CompilerServices.TypeProviderAssembly("MyProvider.DesignTime.dll")>]
do ()
```

If the same source file is also compiled into the design-time DLL (a common pattern), guard the attribute:
```fsharp
#if !IS_DESIGNTIME
[<assembly: CompilerServices.TypeProviderAssembly("MyProvider.DesignTime.dll")>]
do ()
#endif
```
…and add `<DefineConstants>IS_DESIGNTIME</DefineConstants>` in the design-time `.fsproj`.

---

## 6. Design-time dependency not bundled

**Symptom**: At consumer compile time, "Could not load file or assembly 'YamlDotNet, ...'" or similar.

**Cause**: A `PackageReference` in the design-time project resolved correctly during the *provider's* build, but didn't make it into the package's `typeproviders/fsharp41/<tfm>/` folder.

**Fix**: in the design-time `.fsproj`:
```xml
<PropertyGroup>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>
```

If the dep needs to be invisible to consumers' runtime closure too:
```xml
<PackageReference Include="YamlDotNet" Version="17.1.0">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

Verify with `unzip -l <package>.nupkg` after `dotnet pack`.

---

## 7. `assemblyReplacementMap` missing or wrong

**Symptom**: Consumer compile fails with "type 'MyProvider.DesignTime.X' is not defined" — note: **DesignTime**, not Runtime — meaning a quotation captured a reference to a type in the design-time DLL that the consumer never references.

**Cause**: The provider class doesn't declare an `assemblyReplacementMap`, so the SDK doesn't rewrite the design-time-assembly references that quotations embed.

**Fix**:
```fsharp
inherit TypeProviderForNamespaces(
    config,
    assemblyReplacementMap =
        [("MyProvider.DesignTime", "MyProvider.Runtime")])
```

The map is `(designTimeAssemblyName, runtimeAssemblyName) list`. Names exclude `.dll`.

---

## 8. Tests project doesn't link the design-time provider

**Symptom**: Tests build successfully but every assertion against a provided type fails with "type does not exist", or worse, accidentally binds to an unrelated type with the same name.

**Cause**: The test project references the runtime DLL but not the design-time provider, so the compiler never invokes the TP when compiling the test source.

**Fix**: in the test `.fsproj`:
```xml
<ProjectReference Include="..\src\MyProvider.DesignTime\MyProvider.DesignTime.fsproj">
  <IsFSharpDesignTimeProvider>true</IsFSharpDesignTimeProvider>
  <PrivateAssets>all</PrivateAssets>
</ProjectReference>
```

In addition to (not instead of) the runtime project reference.

---

## 9. All static parameters have defaults — SDK warning

**Symptom**: Build succeeds with a warning saying the provider has all-defaulted parameters.

**Cause**: When *all* parameters have defaults, the unapplied container type and the all-defaults specialization are observationally identical, which can produce confusing IntelliSense.

**Fix**: this is intentional SDK behaviour. Either ignore the warning if the design is correct, or make at least one parameter required.

---

## 10. Eager member generation slows down design-time

**Symptom**: Opening a `.fs` file that uses the provider takes 5-30 seconds; IntelliSense is unresponsive.

**Cause**: The provider materialised hundreds of members eagerly via `AddMember` in a loop.

**Fix**: switch to `AddMembersDelayed`:
```fsharp
myType.AddMembersDelayed(fun () ->
    [ for col in schema.Columns ->
        ProvidedProperty(col.Name, col.Type,
            getterCode = fun args -> ...) ])
```

Same for XML docs: prefer `AddXmlDocDelayed`.

---

## 11. Design-time dep version conflicts with the IDE host

**Symptom**: The dep is bundled correctly but loading still fails — "could not load file or assembly 'X, Version=1.2.3.0' ... found 'X, Version=2.0.0.0'".

**Cause**: The IDE process already loaded a different version of the same assembly. Strong-named or differently-versioned references collide.

**Fix**:
- If possible, downgrade your dep to the IDE's loaded version.
- Otherwise, mark the dep `<PrivateAssets>all</PrivateAssets>` and use ILRepack / ILMerge to rename-and-embed it in the design-time DLL. Last resort.

---

## 12. Quotations referencing internal/private members

**Symptom**: Consumer compile fails with "method is not accessible" pointing inside a spliced quotation.

**Cause**: F# quotations splice into the consumer's compilation unit, where access modifiers of the design-time DLL apply. `internal` types in the runtime DLL are invisible to consumers.

**Fix**: any type or member referenced from a quotation must be `public` in the runtime assembly. Use `[<assembly: InternalsVisibleTo>]` if absolutely necessary, but prefer making the type public.

---

## 13. Provider state shared incorrectly across IDE invocations

**Symptom**: Stale data persists after editing the schema source; consumer sees old members until IDE restart.

**Cause**: Provider class uses `let` bindings or static state that survives across `TypeProviderForNamespaces` instances. The compiler creates a fresh provider instance per type-check, but module-level mutable state lives on.

**Fix**: keep mutable state inside the provider instance (instance fields), not at module level. If caching across instances is necessary, key by something stable like file modification time and force-invalidate on key change.

---

## 14. `printfn` debugging produces no output

**Symptom**: You added `printfn "DEBUG: %A" args` inside a callback and see nothing.

**Cause**: The provider runs in the IDE / compiler process, which captures or discards stdout.

**Fix**: use one of:
- `System.Diagnostics.Debugger.Log(0, "tp", msg)` — Output window in attached debugger
- `File.AppendAllText("/tmp/tp.log", msg)` — reliable
- Throw an exception with diagnostic data — surfaces inline at consumer source location

See [debugging.md](debugging.md) for full guide.

---

## 15. Forgetting `addDefaultProbingLocation = true`

**Symptom**: Modern .NET SDK consumer builds fail with "could not find" pointing at design-time deps that *are* present in the package.

**Cause**: Newer .NET SDKs use a different assembly probing strategy; without `addDefaultProbingLocation = true`, the SDK doesn't probe the directory where the design-time DLL lives.

**Fix**:
```fsharp
inherit TypeProviderForNamespaces(
    config,
    assemblyReplacementMap = [...],
    addDefaultProbingLocation = true)
```

This flag is cheap and almost always correct to pass.
