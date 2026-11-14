# Writing a C# module: the SDK, the Hello module and the test harness (card P4-5)

Status: built in phase 4 card P4-5. Applies owner decisions D16 to D26, D40, D52, D57, D59, D61, D62, D65, D67, D69, D73, D74.
The protocol can be spoken without the SDK (the wire contract is language-neutral, D25); the SDK is a small convenience.

## Projects

| Project | What it is |
|---|---|
| `MUD.Modules.Sdk` | Library. `ModuleDefinition` (what a module declares), `ModuleRunner` (connection loop, Ping loop, one delivery at a time, verdicts, `--describe`), `ModuleContext` (named helper methods that call core, D40), `ModuleEvents` (builders for emitted event records), `Payloads` (generic map helpers). References `MUD.Contracts` and `Grpc.Net.Client`. |
| `MUD.Modules.Hello` | The trivial module. Assembly name `hello`, so it publishes as `modules/hello/hello` (D67). |
| `MUD.Tests/ModuleTestHarness.cs` | The test harness. Lives in `MUD.Tests` because it needs core internals. |

## A module in a screenful

```csharp
public static Task<int> Main(string[] args)
{
    ModuleDefinition definition = ModuleDefinition.Create("hello", "1.0.0")
        .Request("emit:message.send", "state", "timer.schedule")          // requests only: the grants file decides (D18)
        .Behavior("HelloGreeter", new[] { "living.enter" }, async (call, context) =>
        {
            if (call.IsBefore) { return BehaviorResult.Allow(); }
            await context.SendMessageAsync(call.Party("mover").EntityId, "Hello!", ModuleEvents.System);
            return BehaviorResult.Allow();
        })
        .Start(async context => { /* read state, reconcile, schedule timers (idempotent, D13) */ })
        .Timer(async (fired, context) => { /* timer.fired */ });
    return ModuleRunner.RunProcessAsync(definition, args);
}
```

The module name must equal its folder name. `RunProcessAsync` prints the `--describe` JSON and exits when asked (D67); otherwise it reads
`MUD_MODULE_NAME`, `MUD_MODULE_TOKEN` and `MUD_CORE_SOCKET`, which only core's supervisor sets. There is no hand-run mode (D69); the program
says so and exits with status 3.

## What the runner does, in order

1. `Hello` with the token, `ContractVersion.Current`, the module version and the manifest built from the definition (D17, D24, D18).
   A refusal is logged with core's reason and the process exits with status 2.
2. Opens `Subscribe`, runs `ModuleDefinition.Start` (the module reconciles), then calls `Ready` (retrying briefly until the stream is open),
   so core's required-module gate (D21) opens only after reconciliation.
3. Pings every `PingInterval` (5 s by default; core treats 30 s of silence as hung). If pings keep failing for `ConnectionLossGrace` (15 s,
   a flagged default; the owner has not set the grace period, D20) or the stream ends, the process exits with status 1 and core's supervisor
   restarts it with backoff.
4. Handles deliveries one at a time in arrival order, so a handler never races another one of the same module (D62).
   A Before delivery is answered with `SubmitVerdict` (Allow or Deny, a message, emitted events, the `handled` flag for `command.entered`).
   An After delivery needs no verdict; events returned from the handler are sent with `Emit`, caused by the delivery.
   A handler that throws sends no verdict: core's deadline passes and the hook's fail policy applies (D60). The error is logged.
5. `timer.fired` goes to `ModuleDefinition.Timer` (D59, D74). Timers are in core's memory and do not survive a reconnect: recreate them in `Start`.

## The context: named helpers over the wire calls (D40)

| Helper | Wire call | Capability needed (D65) |
|---|---|---|
| `SendMessageAsync(targetId, text, channel)` | Emit `message.send` | `emit:message.send` |
| `SetComponentAsync`, `RemoveComponentAsync` | Emit `component.set` / `component.remove` | `emit:component.set`, `emit:component.remove` (own `<module>.*` names only, D50) |
| `AddFeatureAsync`, `RemoveFeatureAsync` | Emit `feature.add` / `feature.remove` | `emit:feature.add`, `emit:feature.remove` (D72) |
| `MoveEntityAsync` | Emit `entity.move` | `emit:entity.move` |
| `AddBehaviorAsync`, `RemoveBehaviorAsync` | Emit `behavior.add` / `behavior.remove` | `emit:behavior.add`, `emit:behavior.remove` (D55) |
| `EmitAsync(records)` | Emit | `emit:<event name>` for each record |
| `SetStateAsync`, `GetStateAsync` | SetState, GetState | `state` (whole-document replace per key, D73) |
| `ScheduleTimerAsync`, `ScheduleTimerAtAsync`, `CancelTimerAsync` | ScheduleTimer, CancelTimer | `timer.schedule` |
| `GetEntityAsync`, `QueryEntitiesAsync` | GetEntity, QueryEntities | `read` (core still answers Unimplemented, see below) |

State and timer helpers throw `ModuleCallException` with core's own message (which names a missing capability). Emit helpers return the
list of refusals instead of throwing, because core applies the valid events and skips only the refused ones (D61); each refusal is also logged.
`context.IsGranted("state")` tells a module what the grants file allowed, so a module can run with a partial grant (D18).
`ModuleEvents` builds the same records without sending them, for use in a verdict: `BehaviorResult.Deny("...").WithEmitted(ModuleEvents.Message(...))`.
Channel names are say, tell, room, combat, system, chat (D44). Payload numbers arrive as floating point (D52).

## The Hello module

Behavior type `HelloGreeter` on hook `living.enter`, attached to a room (or a player) by a builder. In the After phase it greets the mover by name,
counts the move, and keeps the count in the state document `moves` (`{ "count": n }`), which it reads back in `Start`. A timer named `hello-tick`
repeats every minute and logs the count. Grant it `emit:message.send`, `state` and `timer.schedule` in `~/.config/mud/module-grants.yaml`:

```yaml
modules:
  hello:
    - emit:message.send
    - state
    - timer.schedule
```

To try it: `dotnet publish MUD.Modules.Hello -c Release -o <release>/modules/hello`, which produces `modules/hello/hello`; add the grants above;
start core (or `modules rescan` then `modules start hello`); a builder attaches the entry with the existing creator `behavior` command.
Module logs appear in core's log with a `[module:hello]` prefix.

## The test harness

`ModuleTestHarness` (in `MUD.Tests`) runs a module in process, through the SDK's own `ModuleRunner`, against core's real module host on a real
Unix domain socket in a temporary directory. Real: run directory, Kestrel, token and Hello handshake, grants file, the stream, Ping, the
(Module, Type) dictionary, the Before chain with its deadline, verdicts, Emit, state, timers, the game-thread queue. Faked: MongoDB (an in-memory
state repository) and the clock (it starts at the current time and moves only when the test calls `AdvanceClock` or `Clock.Advance`).

```csharp
[Collection("World construction")]
public class MyModuleTests
{
    [Fact]
    public async Task Greets()
    {
        using (var harness = new ModuleTestHarness("hello", "emit:message.send state timer.schedule"))
        {
            await harness.StartAsync(HelloModule.Define());
            Room hall = harness.NewRoom("test.hall");
            harness.Attach(hall, BehaviorHooks.LivingEnter, "HelloGreeter");
            var ann = harness.Place(harness.NewRoom("test.start"), "ann");
            await harness.MoveAsync(ann, hall);
            await harness.WaitForAsync(() => harness.Pump() >= 0 && ann.MessagesSoFar().Any(m => m.Contains("move 1")));
        }
    }
}
```

`HelloModuleTests` also includes one test where core's real `ModuleSupervisor` starts the real Hello program from a published-style folder
as a child process with its token in the environment.

## Notes and limits

- Core fires no event when a player logs in (`World.AddPlayer` enters the room without `entity.move`), so the Hello greeting goes with the move
  into a room that carries the entry, not with the login itself. A login event would be its own core card.
- `GetEntity`, `QueryEntities` and `Log` are still unimplemented on the core side (P4-4 left them out); the SDK helpers for the reads exist and
  will start working when core answers them. `Log` is not wrapped: `context.Log` writes to standard output, which core re-logs.
- The state document, timer, emit and queue numbers are core's flagged defaults (`ModuleHostOptions`), not SDK settings.
- Not verified: a published module under systemd on a host; behavior under load.
