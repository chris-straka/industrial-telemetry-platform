```cs
// Extension methods
// `this` attaches MapIngestionEndpoints to IEndpointRouteBuilder
// Now we can call app.MapIngestionEndpoints() as if it's class method coming from IEndpointRouteBuilder
public static void MapIngestionEndpoints(this IEndpointRouteBuilder app)
```

C# requires all extension methods to be hosted within a static class.

virtual has a default implementation, you may override it
abstract has no default implementation, you must override it

When you use `async`, it wraps your code in a Task and returns it for you.

```cs
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
```

Using `using` will cleanup the resource when it falls out of scope

```cs
using var consumer = new ConsumerBuilder<Ignore, string>(config).Build();
```

C# has different semantics on cleanup, it's not deterministic like C++ where the
destructor is called on scope exit. A type *can* declare a finalizer (`~Foo()`), which
runs on a GC thread at an unpredictable time if at all (the process can exit first).
Almost no type should: a finalizer's only job is releasing resources the GC cannot see,
which in practice means a raw native pointer or OS handle, and `SafeHandle` already
declares one for those. To make cleanup deterministic in C#, you use `using` and
`IDisposable`.

The two words are not interchangeable. **Finalization** is the GC-driven path;
**disposal** is the deterministic one. `using` triggers disposal, never finalization.

## Marshalling

Marshalling is converting data so that code on the other side of a boundary can use it.
The boundary can be managed/native inside one process, one process to another, or one
machine to another.

The split is not high level versus low level, it is two environments that represent data
differently. Conversion happens in both directions. When C# calls a native function the
arguments are converted on the way in, and when that function returns its result is
converted on the way back.

C# never becomes C. It compiles to IL, and the JIT turns IL into machine code. What
crosses the boundary is a *call* into a function that was compiled from C and lives in a
`.so` or `.dll`. The mechanism for making that call is P/Invoke (Platform Invoke).

```cs
[LibraryImport("libc")]
private static partial nint write(int fd, byte[] buf, nuint count);
```

The GC compacts the heap, which means it relocates objects. Native code takes an address
and expects it to stay put. So the runtime pins the array for the duration of the call,
passes the address of its first element, and unpins on return.

A type is **blittable** when its in-memory layout is identical on both sides, so it needs
no conversion at all: `int`, `byte`, `double`, and structs built only from those. Non
blittable types cost real work. `string` is UTF-16 in .NET and usually UTF-8 natively,
`bool` is 1 byte in .NET and 4 in Win32.

Marshalling vs serialization: serialization is one *technique* for marshalling, used when
the boundary is a wire or a process gap and the data must be rebuilt on the far side.
Across a managed/native boundary in one process nothing is rebuilt, you pin and pass a
pointer. A network call marshals its arguments by serializing them. A P/Invoke marshals
its arguments without serializing anything.

## Dispose

`IDisposable` is a contract with one method, `Dispose()`. Nothing in the runtime calls it
on its own. `using` calls it, because the compiler rewrites `using` into a try/finally,
and the DI container calls it at shutdown on the singletons it created.

The GC never calls `Dispose` on anything. The GC reclaims **memory**. `Dispose` releases
everything that is not memory: file handles, sockets, an OTel meter's registration with
its listeners. Disposing an object does not free it. The GC still does that later, on its
own schedule.

A **managed field** holds a normal .NET object, which the GC can see and collect. An
**unmanaged resource** is something the GC cannot see, such as a raw `IntPtr` from an OS
call or native memory from `Marshal.AllocHGlobal`.

Ownership decides who disposes. If your class created the disposable, your class disposes
it. If it was injected, the creator still owns it, so leave it alone.

```cs
public sealed class SensorMetrics : IDisposable
{
    public void Dispose() => _meter.Dispose();
}
```

That is the entire implementation for the common case.

`Dispose()` cannot be private, because `using` has to call it. It can be implemented
*explicitly*, which is the `IDisposable.` prefix below, not the `void`. An explicit
implementation takes no access modifier and is not callable on the type directly, only
through the interface.

```cs
class Foo : IDisposable
{
    void IDisposable.Dispose() { }   // explicit
}

var f = new Foo();
f.Dispose();                  // does not compile
((IDisposable)f).Dispose();   // works
using (var g = new Foo()) { } // works, using casts to IDisposable
```

One use is hiding `Dispose` behind a domain word like `Close()`. You always have to
implement `Dispose()` because that is what `using` calls, so you end up with two methods
doing the same thing, and the explicit form decides which one shows up when a caller types
`foo.`.

```cs
class Foo : IDisposable
{
    public void Close() { /* the real cleanup */ }
    void IDisposable.Dispose() => Close();   // hidden from foo.
}
```

`public void Dispose() => Close();` works exactly as well. The prefix is cosmetic, a style
choice about what callers see. `Stream` does not bother, it exposes both and its public
`Close()` simply calls `Dispose()`.

The other use, and the more common one, is when a class implements two interfaces that
declare the same member and each needs its own body.

### The Dispose(bool) pattern

`protected virtual void Dispose(bool disposing)`. `Dispose()` calls it with true and the
finalizer calls it with false. True releases the unmanaged handle and disposes the managed
fields, false releases the handle only.

The split exists for one reason. When a finalizer runs, the GC may already have finalized
the managed objects the class holds fields to. Calling `Dispose()` on them from the
finalizer is a use-after-finalize, and an exception escaping a finalizer kills the process.

So it is only worth writing when the class has a finalizer, or when the class is meant to
be inherited. The inheritance case is because `Dispose()` is not virtual, so a child
cannot override it. `Dispose(bool)` is virtual, so it is the only place a child can put
cleanup that still runs when someone calls `Dispose()`.

The rejected alternative is to hide the parent's `Dispose()` as an explicit interface
implementation and let the child re-implement the interface with its own:

```cs
class Child : Parent, IDisposable { public void Dispose() { ... } }
```

That compiles and `using` on a `Child` calls the child's version, but an explicit
implementation is private to `Parent`, so the child cannot invoke the parent's cleanup and
the fields `Parent` declared on that object leak. The child cannot duplicate that cleanup
either unless `Parent`'s fields are `protected`. That is the worse trade: protected fields
let every subclass read and mutate the parent's state forever, so the parent can no longer
hold its own invariants. `Dispose(bool)` exposes one call point and no state.

Neither case is true of most classes.

`GC.SuppressFinalize(this)` takes the object off the finalization queue once `Dispose()`
has already done the cleanup. With no finalizer there is nothing to suppress.

On .NET 5 and later, finalizers do not run at process exit. .NET Framework did run them
with a time budget, which is where the opposite belief comes from.
