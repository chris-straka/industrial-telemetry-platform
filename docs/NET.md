.g means generated. The convention is old (designer files, source generators); what is
new in .NET 6 is IMPLICIT USINGS, which is why this particular .g.cs exists at all.

[Where the worker stores its auto injected packages](/src/Industrial.Diagnostics.Worker/obj/Debug/net10.0/Industrial.Diagnostics.Worker.GlobalUsings.g.cs)

[security](https://learn.microsoft.com/en-us/aspnet/core/security/?view=aspnetcore-10.0)
[performance](https://learn.microsoft.com/en-us/aspnet/core/performance/overview?view=aspnetcore-10.0)

# Registration: explicit list vs classpath scan

.NET registers every dependency up front, as ordinary statements against
`builder.Services`. The container knows exactly what those lines put in it and nothing
else. Spring inverts that: a class declares itself with `@Component`, `@Service` or
`@Repository`, `@ComponentScan` walks the packages at startup collecting them, and
`@Autowired` resolves by type at the injection site.

Four things follow from the difference.

**Finding what is registered.** Here you read `Program.cs` top to bottom and you have the
whole answer. In Spring the answer is spread across every annotated class and depends on
which packages the scan covers, so a bean that is not picked up looks identical to one
that was never written.

**When a mistake surfaces.** A missing .NET registration throws `InvalidOperationException`
at the first resolve, which for a scoped service means the first request that needs it.
Spring fails the whole context at startup with `NoSuchBeanDefinitionException`. Spring's is
the better failure -- earlier and total. .NET can be pushed toward it with
`ValidateOnBuild`, and `ValidateOnStart` on options is the same instinct applied to config.

**Ambiguity.** Two implementations of one interface is a `NoUniqueBeanDefinitionException`
in Spring until you add `@Primary` or `@Qualifier`. .NET has no error: the last
registration wins for a single resolve, and asking for `IEnumerable<T>` hands you all of
them in registration order. Quieter, and quiet is not always better.

**Conditional wiring.** Spring needs framework concepts for this -- `@Profile`,
`@ConditionalOnProperty`. Registration here is just code, so a plain `if` over
`builder.Environment` does it, and a loop can register a family of services with nothing
new to learn.

The trade is typing against readability. Spring's annotations scale to hundreds of beans
without a correspondingly huge configuration file; .NET's list stays one ordered thing you
can read, which is worth more on a codebase this size than the lines it costs.

# DI lifetimes

A DI lifetime defines how long a registered service instance lives and when the DI
container creates/reuses it

Transient is created every time you ask for it.

Scoped is one instance per scope. In ASP.NET Core the FRAMEWORK opens a scope per HTTP
request, which is why `DbContext` is scoped by default -- you rarely create one yourself.
In a worker there are no requests, so you open scopes explicitly (`CreateScope()`), or
sidestep them with a factory like `IDbContextFactory` as `Diagnostics.Worker` does.

Singleton is created once and reused for the entire app lifetime

# Project SDKs

**The SDK should describe what the process actually is**, because it's the first thing
anyone reads in a csproj:

| SDK                        | means                                     | projects          |
| -------------------------- | ----------------------------------------- | ----------------- |
| `Microsoft.NET.Sdk.Worker` | hosted background service, serves nothing | `Sensor.Emulator` |
| `Microsoft.NET.Sdk`        | class library or one-shot CLI             | `Data.ML`, `Shared` |
| `Microsoft.NET.Sdk.Web`    | serves HTTP                               | Everything else   |

`.Worker` and `.Web` both provide `Microsoft.Extensions.*` implicit usings;
The plain SDK covers only `System.*`

`.Web` adds a FrameworkReference (an MSBuild item) to `Microsoft.AspNetCore.App` (~150 DLLs)

```xml
<FrameworkReference Include="Microsoft.AspNetCore.App" />
```

Points to the ASP.NET Core shared framework on your .NET install.
It's not copied into your build output like NuGet packages are via `PackageReference`.

```Dockerfile
# If you need Microsoft.AspNetCore.App
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# If you don't need it
FROM mcr.microsoft.com/dotnet/runtime
```

# IHttpClientFactory

## The handler chain

`HttpClient` is a thin wrapper. It does not own connections. It holds a reference to the
HEAD of a linked list of handlers:

```
HttpClient                 <- thin wrapper, cheap to create
   |  SendAsync
   v
LifetimeTracking...Handler <- OUTERMOST. No behaviour; the factory's tracking handle.
   v
DelegatingHandler          <- logging          (added by IHttpClientFactory)
   v
DelegatingHandler          <- Polly resilience (AddStandardResilienceHandler)
   v
DelegatingHandler          <- OTel instrumentation
   v
SocketsHttpHandler         <- PRIMARY handler. OWNS THE TCP CONNECTION POOL.
   v
sockets
```

HttpClient is at the TOP, not the end. Each `DelegatingHandler` holds an `InnerHandler`,
so the chain is walked outermost-first on the way out and unwound on the way back.

`DelegatingHandler` is middleware-SHAPED. Same chain-of-responsibility idea as ASP.NET Core
middleware, mirrored onto the client side: server middleware wraps INBOUND requests,
these wrap OUTBOUND ones. The difference is plumbing -- server middleware is composed by
a builder, these each hold their next link explicitly in `InnerHandler`.

## Vocabulary

A HANDLER is one link. `HttpMessageHandler` is the base type.
A CHAIN is the linked list of them, terminating in a primary handler that does the I/O.

| type                 | what it is                                                      |
| -------------------- | --------------------------------------------------------------- |
| `HttpMessageHandler` | base type for any link                                          |
| `DelegatingHandler`  | a link that wraps an inner handler (the middleware-shaped ones) |
| `SocketsHttpHandler` | the terminal link. Actually opens sockets. Owns the pool.       |

`DelegatingHandler` is the .NET NAME for these -- call them delegating handlers, not
middleware. "Middleware" is the ASP.NET Core term for the SERVER-side pipeline. The
comparison is an analogy for understanding the shape, not a naming convention.

## Who owns what

| thing                | owns                                                     |
| -------------------- | -------------------------------------------------------- |
| `SocketsHttpHandler` | the TCP connection pool. The expensive part.             |
| the chain            | the handlers, terminating in that primary handler        |
| `IHttpClientFactory` | a CACHE of chains keyed by client name. One per process. |
| `HttpClient`         | nothing. A reference to the head of a chain.             |

EACH CHAIN HAS ITS OWN CONNECTION POOL, because each chain terminates in its own
`SocketsHttpHandler` instance and the pool lives on that object. This is the mechanism
behind rotation: a new chain means a new `SocketsHttpHandler`, which means an empty pool
and a fresh DNS lookup on the next request. Two clients on different names never share
connections, even to the same host.

MANY HttpClients share ONE chain. That is the whole point of the factory: every
`CreateClient("name")` returns a NEW HttpClient object pointing at the SAME cached chain.
Creating clients per-request is cheap because the connections are shared, not duplicated.

So the steady state for one client name is MANY CLIENTS -> ONE CHAIN -> MANY CONNECTIONS.
The pool is plural because HTTP/1.1 carries one request at a time per connection, so
concurrent requests need separate sockets (`docs/Networking.md`). Under HTTP/2 one
connection multiplexes many requests and the pool stays small.

"Many chains" only happens two ways: different client NAMES, or briefly after expiry, when
the de-listed chain is still serving whoever holds it while the new one is current.

Because the cache is process-wide, every part of the app asking for "telemetry" gets a
client over the same connections.

## Why not just one static HttpClient?

You can, and it fixes half the problem. The two failure modes are separate:

| approach                      | socket exhaustion                              | stale DNS                   |
| ----------------------------- | ---------------------------------------------- | --------------------------- |
| `new HttpClient()` per call   | breaks -- disposal leaves sockets in TIME_WAIT | fine                        |
| one static HttpClient forever | fine -- one pool, reused                       | breaks -- never re-resolves |
| `IHttpClientFactory`          | fine                                           | fine -- chains rotate       |

So the factory is not about "creating objects on the fly". It is the only option that
handles both. A static client is a perfectly reasonable choice for a target whose address
never moves.

## Handler rotation

Each cached chain has a lifetime (default 2 minutes from creation).

Nothing switches proactively. On the NEXT `CreateClient` call after expiry, the factory
builds a fresh chain and caches that instead. Clients handed out before expiry keep
pointing at the old chain -- not just to drain requests in flight, but for every request
they make from then on. It is disposed once the CLIENT is unreachable and collected, which
in the captured case is never. See "Inside the factory" for the mechanism.

This has NOTHING to do with DI scopes. The factory runs its own expiry and cleanup; chain
disposal is the factory's bookkeeping, not the container's. Two independent mechanisms
that both happen to involve the word "dispose".

Why rotate at all: DNS.

```
1. handler resolves edge-gateway -> 172.18.0.5, opens connections, keeps them
2. the edge-gateway container is recreated, Docker gives it 172.18.0.9
3. DNS now says .9, but the pooled connections still point at .5, which is dead
4. requests fail or hang until those connections error out
```

A handler that lives forever keeps talking to an address that no longer exists. A new
`SocketsHttpHandler` resolves DNS fresh. `SocketsHttpHandler.PooledConnectionLifetime`
attacks the same problem more directly by capping how long an individual CONNECTION lives.

## Inside the factory

**The thing to hold onto: rotation happens to a dictionary entry, not to your client.**
Nothing ever reaches into a live `HttpClient` and swaps its handler. So whether you benefit
from rotation depends entirely on how long your `HttpClient` stays alive, and that is the
whole subject.

### Who references whom

Two references, aimed at the same object, with different strengths:

```
ExpiredHandlerTrackingEntry ──_livenessTracker (weak)──> LifetimeTrackingHttpMessageHandler
HttpClient                  ──its handler     (strong)──> LifetimeTrackingHttpMessageHandler
```

`_livenessTracker` is a FIELD ON the entry; its TARGET is the handler. There is only one
weak reference in the whole design. And the same object sits at the head of the chain the
requests actually travel down:

```
LifetimeTrackingHttpMessageHandler  ──>  Polly  ──>  SocketsHttpHandler
                                                     owns the TCP sockets
```

`LifetimeTrackingHttpMessageHandler` is the outermost handler and adds no behaviour -- it
exists only to give the factory a stable object to watch. `HttpClient` holds it strongly.
The factory watches that same object weakly, which is how it detects that every client
using the chain has gone away WITHOUT keeping them alive itself.

### The two collections

| field              | type                                                             | role                                |
| ------------------ | ---------------------------------------------------------------- | ----------------------------------- |
| `_activeHandlers`  | `ConcurrentDictionary<string, Lazy<ActiveHandlerTrackingEntry>>` | the CURRENT chain per client name   |
| `_expiredHandlers` | `ConcurrentQueue<ExpiredHandlerTrackingEntry>`                   | de-listed chains, waiting on the GC |

`ActiveHandlerTrackingEntry` holds `Handler`, `Lifetime`, `Name`, a DI `Scope`, and a
`Timer`. One entry per name, one chain per entry.

### CreateClient

Looks up `_activeHandlers[name]`, building the entry via `CreateHandlerEntry` if absent --
and that is when your `AddHttpClient` configuration lambda runs, which is why `BaseAddress`
is set there rather than per call. Then:

```csharp
new HttpClient(entry.Handler, disposeHandler: false)
```

A NEW client object every call, wrapping the SAME handler until expiry.
`disposeHandler: false` means disposing your client never touches the shared chain.

### Expiry

At `HandlerLifetime` (default 2 min, measured from creation, not last use -- traffic does
not keep it alive) `ExpiryTimer_Tick` removes the entry from `_activeHandlers` and pushes
it onto `_expiredHandlers`.

Removal is DE-LISTING, not destruction. The dictionary is only the "what do I hand out for
this name" lookup, so deleting the key is the sole way to force the next `CreateClient` to
build a fresh chain. Nothing can revoke a reference already handed out, and nothing tries:
a client holding the expired chain keeps using it, not just to finish an in-flight request
but for every request it makes from then on.

### Cleanup

`CleanupTimer_Tick` walks `_expiredHandlers` testing `CanDispose`, which is simply "is the
weak reference dead?". A weak reference does not keep its target alive, so it reads null
once the GC has collected the handler -- which can only happen once every `HttpClient`
referencing it became unreachable. Only then are `InnerHandler` and `Scope` disposed and
the TCP connections closed.

GC-driven rather than timer-driven, and it has to be: disposing a handler mid-request
would kill the request.

### So it comes down to client lifetime

**Resolved per call** (what `TransmissionWorker` does). `client` is a local. After the POST
the iteration ends, the local goes out of scope, the `HttpClient` is unreachable, the weak
reference reads null on the next cleanup tick, the old chain is disposed. The next
`CreateClient` finds no key and builds a fresh chain: new `SocketsHttpHandler`, new
connections, fresh DNS. Rotation works. Within any 2-minute window you are still reusing
one chain and its open sockets -- the rebuild happens once per lifetime, not per send.

**Captured in a field of a singleton.** Hypothetical -- there is no such field in this
repo -- but one line away, since hosted services like `TransmissionWorker` ARE singletons.
`private readonly HttpClient _client = factory.CreateClient(...)` instead of resolving in
the loop is all it takes:

```
container -> singleton -> HttpClient -> LifetimeTracking -> Polly -> SocketsHttpHandler
                                       |_______________ the chain ______________|
```

Read that as a REFERENCE PATH: each object holds a reference to the next one. Note the
chain is not something the handler points AT -- the handler is its head.

An object survives GC as long as something reachable references it, so the container
keeping the singleton alive keeps every object to its right alive too, for the life of the
process. The handler is never collected, so `CanDispose` never turns true, so the entry
sits in `_expiredHandlers` indefinitely holding a chain that is still in active use. And
the replacement chain is only ever handed out BY `CreateClient`, which is no longer being
called. Same sockets, same IP, until the process restarts.

Note the container is only the root of that path -- it is the CAPTURE that pins the chain,
not the container tracking the client.

## Captive dependencies and root-provider disposables

Two separate gotchas that are easy to conflate.

> Captive dependency

Injecting a shorter-lived service into a longer-lived one pins it. A transient captured in
a singleton becomes, in practice, a singleton -- one instance for the process lifetime.
For a typed HttpClient that means one handler chain forever, defeating rotation. For a
DbContext it means an ever-growing change tracker and no recovery from a dead connection.

> Transient disposables resolved from the ROOT provider

The DI container tracks `IDisposable` instances IT CREATES so it can dispose them when the
container is disposed. For the root container, "when the container is disposed" means
PROCESS SHUTDOWN. So every transient disposable resolved from the root accumulates for the
life of the app -- a slow leak that looks like nothing until it doesn't.

Resolving inside a scope fixes it: the scope disposes what it created when it ends.

Note this does NOT apply to a client from `CreateClient`, because it is never RESOLVED
from DI in the first place -- the container cannot track what it did not create. (Its
disposal also happens to be a no-op, but that is a separate fact.) Reaching for a scope to
"fix" that case is cargo cult.

A TYPED client is different: `T` IS resolved from the container, so if `T` is `IDisposable`
it gets tracked like anything else.

# Where a config value comes from

`WebApplication.CreateBuilder` stacks five sources. Later ones win on a key collision:

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. user secrets (Development only)
4. environment variables
5. command line arguments

That order is why `docker-compose.yaml` can override anything in `appsettings.json`
without editing it: the compose `environment:` block is layer 4 and the file is layer 1.

An environment variable name cannot contain a colon on Linux, so .NET rewrites a double
underscore into one when it reads the variable. `Kafka__BootstrapServers` in compose is
`Kafka:BootstrapServers` by the time configuration is queried, which is why every key in
this repo is READ with a colon and WRITTEN with underscores. Reading the underscore form
returns null, silently -- it is a key nobody set.

The layering is also what makes this repo's "no fallback defaults" rule enforceable. Since
`appsettings.json` is a layer rather than a set of defaults applied when a key is missing,
a key absent from every layer binds to `default` and the `[Required]` attribute on the
options class fails at boot. See `# Fail at boot vs fail after boot` below.

# Configuration: the empty-string hole

```csharp
var serviceName = builder.Configuration["OTel:ServiceName"]
    ?? throw new InvalidOperationException("Missing 'OTel:ServiceName' configuration.");
```

| config state                         | what the indexer returns | does `?? throw` fire |
| ------------------------------------ | ------------------------ | -------------------- |
| key absent                           | `null`                   | yes                  |
| `OTel__ServiceName=` (set but empty) | `""`                     | NO                   |
| `OTel__ServiceName="   "`            | `"   "`                  | NO                   |

So a blank env var slips through and fails later, somewhere less obvious --
`new Uri("")` throws an ArgumentException that names nothing useful.

The fix for one value is not the Options pattern, it is one line:

```csharp
var serviceName = builder.Configuration["OTel:ServiceName"];
if (string.IsNullOrWhiteSpace(serviceName))
    throw new InvalidOperationException("Missing or empty 'OTel:ServiceName' configuration.");
```

# When Options is actually worth it

For ONE value, `IsNullOrWhiteSpace` and the Options pattern give the same guarantee, and
the direct check is less machinery. Options starts paying when:

1. Several related values belong together -- one typed object instead of loose locals.
2. Something other than Program.cs needs them -- inject `IOptions<T>` rather than
   `IConfiguration`, so consumers cannot do untyped string lookups of their own.
3. Validation is more than "not blank" -- `[Range]`, `[Url]` beat hand-written ifs, and
   one pass reports EVERY failure instead of only the first one hit.

`[Required]` already rejects null, empty and whitespace: `RequiredAttribute.IsValid` ends
with `stringValue.Trim().Length != 0` and `AllowEmptyStrings` defaults to false.

The C# `required` keyword does NOT do this. It is enforced by the compiler at
object-initialiser call sites, and the configuration binder builds objects by reflection,
which skips it entirely. Use `[Required]`, not `required`, for bound config.

# Fail at boot vs fail after boot

Not a compile-time/runtime distinction -- both are runtime. The question is WHEN.

```
builder.Configuration[...] in Program.cs   -> startup. Process dies at boot.
configuration[...] inside ExecuteAsync     -> after the host started and reported ready.
```

Since .NET 6 an unhandled exception in a `BackgroundService` stops the host by default
(`BackgroundServiceExceptionBehavior.StopHost`), so the second one still crashes -- just
later, after the container has claimed to be up. That is the real cost: a crash loop that
looks like a healthy start followed by a mystery exit, instead of a clean refusal to boot.
