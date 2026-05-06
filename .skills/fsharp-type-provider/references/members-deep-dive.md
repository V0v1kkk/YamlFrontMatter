# Provided Members: Deep Dive

This file is the API reference for every kind of member you can attach to a `ProvidedTypeDefinition`. For each, it shows the canonical signature, an example, and any erased-vs-generative subtlety. Treat it as a lookup; you don't need to read it linearly.

## Table of contents
- [ProvidedConstructor](#providedconstructor)
- [ProvidedProperty](#providedproperty)
- [ProvidedMethod](#providedmethod)
- [ProvidedField](#providedfield)
- [ProvidedEvent](#providedevent)
- [ProvidedParameter](#providedparameter)
- [Static parameters](#static-parameters)
- [XML documentation](#xml-documentation)
- [Custom attributes](#custom-attributes)
- [Delayed member generation](#delayed-member-generation)
- [Interfaces (generative)](#interfaces-generative)
- [Enumerations (generative)](#enumerations-generative)
- [Abstract classes and base constructors (generative)](#abstract-classes-and-base-constructors-generative)
- [Nested types](#nested-types)
- [Units of measure](#units-of-measure)
- [`hideObjectMethods` and `nonNullable`](#hideobjectmethods-and-nonnullable)

---

## ProvidedConstructor

```fsharp
ProvidedConstructor(parameters, invokeCode = fun args -> <expr>, ?IsTypeInitializer)
```

Erased: `args.[0]` is the first explicit parameter. Generative: `args.[0]` is `this`, parameters start at `args.[1]`.

```fsharp
// Default ctor (erased)
let ctor0 = ProvidedConstructor([], invokeCode = fun _ -> <@@ "state" :> obj @@>)

// With parameter (erased)
let ctor1 =
    ProvidedConstructor(
        [ ProvidedParameter("inner", typeof<string>) ],
        invokeCode = fun args -> <@@ (%%(args.[0]) : string) :> obj @@>)

// Static / type initializer
let cctor =
    ProvidedConstructor([], invokeCode = fun _ -> <@@ () @@>,
                       IsTypeInitializer = true)
```

For generative types, **at least one constructor is mandatory** — even an empty `ProvidedConstructor([], invokeCode = fun _ -> <@@ () @@>)` is enough.

---

## ProvidedProperty

```fsharp
ProvidedProperty(name, propertyType,
                 ?isStatic, ?getterCode, ?setterCode,
                 ?indexParameters)
```

```fsharp
// Static, constant
let staticProp =
    ProvidedProperty("Greeting", typeof<string>, isStatic = true,
        getterCode = fun _ -> <@@ "hello" @@>)

// Instance, reading from erased base
let instProp =
    ProvidedProperty("Inner", typeof<string>,
        getterCode = fun args -> <@@ (%%(args.[0]) :> obj) :?> string @@>)

// Mutable instance
let mutableProp =
    ProvidedProperty("Count", typeof<int>,
        getterCode = fun args -> <@@ (%%(args.[0]) :> obj :?> ref<int>).Value @@>,
        setterCode = fun args -> <@@ (%%(args.[0]) :> obj :?> ref<int>).Value <- (%%(args.[1]) : int) @@>)
```

Indexer / parametrised properties accept `indexParameters: ProvidedParameter list`.

---

## ProvidedMethod

```fsharp
ProvidedMethod(name, parameters, returnType,
               invokeCode = fun args -> <expr>,
               ?isStatic)
```

```fsharp
// Static method
let staticMeth =
    ProvidedMethod("Greet", [ProvidedParameter("name", typeof<string>)],
                   typeof<string>, isStatic = true,
                   invokeCode = fun args ->
                       <@@ sprintf "Hello, %s!" (%%(args.[0]) : string) @@>)

// Instance method (erased: args.[0] = first param; there is no 'this' in erased)
// Wait — actually for INSTANCE methods on an erased type, args.[0] IS the instance,
// because the receiver is treated like an extra parameter at index 0:
let instMeth =
    ProvidedMethod("ToUpper", [], typeof<string>,
        invokeCode = fun args ->
            <@@ ((%%(args.[0]) :> obj) :?> string).ToUpper() @@>)
```

Subtle: for **instance** methods on **erased** types, `args.[0]` is the receiver. For **static** methods on erased types, `args.[0]` is the first explicit parameter. For **generative** methods (instance or static), `args.[0]` is `this` for instance methods and the first parameter for static methods. The rule is: instance has `this` at index 0 in both kinds; static does not — and erased simply treats the receiver as a positional argument.

When in doubt, write the quotation, build, and inspect the splicing error — F# error messages name the parameter index that mismatches.

---

## ProvidedField

Generative only.

```fsharp
let field    = ProvidedField("_count", typeof<int>)
let litField = ProvidedField.Literal("MaxSize", typeof<int>, 100)
```

`ProvidedField.Literal` produces a compile-time constant — used for enum values and `Microsoft.FSharp.Core.LiteralAttribute`-style fields visible to the F# compiler at typecheck time.

---

## ProvidedEvent

```fsharp
let evt =
    ProvidedEvent(
        "Changed", typeof<System.EventHandler>,
        adderCode   = fun args -> <@@ ignore (%%(args.[1]) : System.EventHandler) @@>,
        removerCode = fun args -> <@@ ignore (%%(args.[1]) : System.EventHandler) @@>)
myType.AddMember evt
```

`args.[0]` = `this`, `args.[1]` = the handler being added/removed.

---

## ProvidedParameter

```fsharp
ProvidedParameter(name, parameterType,
                  ?isOut, ?optionalValue)
```

```fsharp
let p = ProvidedParameter("name", typeof<string>)
let optP = ProvidedParameter("count", typeof<int>, optionalValue = 0)
```

To attach attributes to a parameter (e.g. `[<ReflectedDefinition>]`):

```fsharp
let exprParam = ProvidedParameter("p", typeof<Microsoft.FSharp.Quotations.Expr<int>>)
exprParam.AddCustomAttribute {
    new CustomAttributeData() with
        member _.Constructor =
            typeof<ReflectedDefinitionAttribute>.GetConstructor([||])
        member _.ConstructorArguments = [||] :> _
        member _.NamedArguments       = [||] :> _ }
```

---

## Static parameters

```fsharp
containerType.DefineStaticParameters(
    [ ProvidedStaticParameter("Count", typeof<int>) ],
    fun typeName args -> createType typeName (args.[0] :?> int))
```

Multiple parameters, mixing required and optional:

```fsharp
containerType.DefineStaticParameters(
    [ ProvidedStaticParameter("ConnectionString", typeof<string>)
      ProvidedStaticParameter("Schema", typeof<string>, parameterDefaultValue = "dbo") ],
    fun typeName args ->
        let conn   = args.[0] :?> string
        let schema = args.[1] :?> string
        createType typeName conn schema)
```

If **all** parameters have defaults the SDK warns at build time — that is intentional, because the unapplied container type and the default-applied container type are indistinguishable to consumers.

---

## XML documentation

```fsharp
prop.AddXmlDoc "Gets the current connection status."

// Lazy version — only evaluated when IntelliSense asks
prop.AddXmlDocDelayed(fun () ->
    sprintf "Column '%s' of type %s." colName colType.Name)
```

Delayed XML doc is essential for very large schemas — eagerly building doc strings for thousands of columns slows down design-time noticeably.

---

## Custom attributes

`AddCustomAttribute` is supported on:
- `ProvidedTypeDefinition`
- `ProvidedMethod`
- `ProvidedProperty`
- `ProvidedParameter`

Not on `ProvidedConstructor` — for `[<Obsolete>]` on a constructor, use the dedicated `AddObsoleteAttribute` helper.

```fsharp
myType.AddCustomAttribute {
    new CustomAttributeData() with
        member _.Constructor =
            typeof<System.ObsoleteAttribute>.GetConstructor([| typeof<string> |])
        member _.ConstructorArguments =
            [| CustomAttributeTypedArgument(typeof<string>, "use NewType instead" :> obj) |] :> _
        member _.NamedArguments = [||] :> _ }
```

---

## Delayed member generation

```fsharp
myType.AddMembersDelayed(fun () ->
    [ for col in schema.Columns ->
        ProvidedProperty(col.Name, col.Type,
            getterCode = fun args -> <@@ fetchColumn (%%(args.[0]) : obj) col.Name @@>) ])
```

Use this whenever member count is data-driven and large (databases with hundreds of tables, JSON with deeply nested rows). The callback is invoked on first IntelliSense / type-check demand, not at provider construction.

`AddMemberDelayed` exists for a single member.

---

## Interfaces (generative)

```fsharp
let iface = ProvidedTypeDefinition("IContract", None,
                                   isErased = false, isInterface = true)
let absMeth = ProvidedMethod("Execute", [], typeof<unit>)
absMeth.AddMethodAttrs(MethodAttributes.Virtual ||| MethodAttributes.Abstract)
iface.AddMember absMeth

myType.AddInterfaceImplementation iface
let impl =
    ProvidedMethod("Execute", [], typeof<unit>,
                   invokeCode = fun _ -> <@@ () @@>)
myType.AddMember impl
myType.DefineMethodOverride(impl, iface.GetMethod("Execute"))

provAsm.AddTypes [iface; myType]
```

---

## Enumerations (generative)

```fsharp
let e = ProvidedTypeDefinition("Status", Some typeof<Enum>, isErased = false)
e.SetEnumUnderlyingType(typeof<int>)
for (name, value) in [ "Active", 1; "Inactive", 2; "Pending", 3 ] do
    e.AddMember (ProvidedField.Literal(name, e, value))
provAsm.AddTypes [e]
```

The base type **must** be `Some typeof<Enum>`; the literal field's declaring type must be the enum itself.

---

## Abstract classes and base constructors (generative)

```fsharp
let baseT =
    ProvidedTypeDefinition(provAsm, ns, "AnimalBase", Some typeof<obj>,
        isErased = false, isAbstract = true, isSealed = false)

let baseCtor =
    ProvidedConstructor(
        [ ProvidedParameter("name", typeof<string>) ],
        invokeCode = fun _ -> <@@ () @@>,
        IsImplicitConstructor = true)
baseT.AddMember baseCtor

let derivedT =
    ProvidedTypeDefinition(provAsm, ns, "Dog", Some (baseT :> System.Type),
                           isErased = false)
let derivedCtor =
    ProvidedConstructor(
        [ ProvidedParameter("name", typeof<string>) ],
        invokeCode = fun _ -> <@@ () @@>)
derivedCtor.BaseConstructorCall <-
    fun args -> (baseCtor :> ConstructorInfo), [args.[1]]
derivedT.AddMember derivedCtor

provAsm.AddTypes [baseT; derivedT]
```

`BaseConstructorCall` is a function — it receives the derived constructor's `args` (where `args.[0]` is `this`) and returns the target base constructor plus the argument expressions to pass to it.

---

## Nested types

Erased:

```fsharp
let outer = ProvidedTypeDefinition(asm, ns, "Outer", Some typeof<obj>)
let inner = ProvidedTypeDefinition("Inner", Some typeof<obj>)
inner.AddMember(ProvidedProperty("Value", typeof<int>, isStatic = true,
    getterCode = fun _ -> <@@ 42 @@>))
outer.AddMember inner
```

Generative — additionally register with the assembly:

```fsharp
provAsm.AddNestedTypes([inner], ["Outer"])
```

Forgetting `AddNestedTypes` is the #1 cause of "type X.Inner is not defined" errors with generative providers.

---

## Units of measure

```fsharp
let kg     = ProvidedMeasureBuilder.SI "kg"
let m      = ProvidedMeasureBuilder.SI "m"
let m_s    = ProvidedMeasureBuilder.Ratio(m, ProvidedMeasureBuilder.SI "s")
let kgFloat = ProvidedMeasureBuilder.AnnotateType(typeof<float>, [kg])

let weight =
    ProvidedProperty("Weight", kgFloat, getterCode = fun _ -> <@@ 70.0 @@>)
```

Compound units: `Product`, `Ratio`, `Square`, `Inverse`, `One`. Custom units: define an erased `ProvidedTypeDefinition` and pass it to `AnnotateType`.

---

## `hideObjectMethods` and `nonNullable`

```fsharp
ProvidedTypeDefinition(asm, ns, "Connection", Some typeof<obj>,
                       hideObjectMethods = true,    // hide Equals/GetHashCode/ToString from IntelliSense
                       nonNullable       = true)    // [<AllowNullLiteral(false)>]
```

`hideObjectMethods` is almost always desirable for "container" provided types where the user shouldn't think of them as `obj`. `nonNullable` is useful when a `null` literal would be meaningless for the provided type (e.g. a row from a non-nullable schema).
