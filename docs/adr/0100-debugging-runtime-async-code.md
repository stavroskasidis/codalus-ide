# 0100. Debugging runtime-async code (.NET 11)

- **Status:** accepted (0.13, 2026-11-13)
- **Date:** 2026-11-13
- **Deciders:** maintainer (D53 in the [roadmap](../roadmap.md#decision-register), settled after the spike as recommended)
- **Amends:** [ADR 0086](0086-a-managed-coreclr-debugger.md) and [ADR 0094](0094-the-codalus-debugger-by-default.md) (the Codalus debugger's stepping, for async code of both kinds)

## Context

.NET 11 can compile `async` methods for runtime async (`<Features>$(Features);runtime-async=on</Features>` in a `net11.0` project): the compiler makes no state machine, the method keeps its own body, marked `MethodImplAttributes.Async` (`0x2000`), and each `await` is a call to `System.Runtime.CompilerServices.AsyncHelpers.Await` (or `UnsafeAwaitAwaiter`). The runtime suspends and resumes the method itself: a suspended call returns to its caller, and a resumed one is called by the runtime's dispatcher of continuations (`AsyncHelpers.RuntimeAsyncTask.DispatchContinuations`) on a thread-pool thread. Its PDB has no async stepping information (the `AsyncMethodSteppingInformation` custom debug information state machines have).

The Codalus debugger (ADR 0086, the default since ADR 0094) had nothing async-specific: no use of the PDB's async information, no yield or resume offsets. It read a state machine's hoisted variables and its original `this` (M43b), and that was all. Whether stepping, values, the stack and exceptions work on runtime-async code had never been tested, and the user guide said nothing.

## The spike (2026-11-13)

A fixture, `samples/Debugging/Async.slnx`: `AsyncDebuggee` (state machines) and `AsyncDebuggee.RuntimeAsync` (the same files, runtime async on), each referencing a small library built the other way (`AsyncLib.RuntimeAsync`, `AsyncLib`), so the two kinds await each other in both directions. Its scenarios: locals before and after `Task.Delay` and `Task.Yield` (which resumes on a thread-pool thread), an awaited call of the user's that returns a value, a chain of three awaits, an exception thrown after an await, and the library awaiting a callback of the program's. `RuntimeAsyncDebuggingTests` runs each scenario on both builds (`[Theory]` over the two programs), through the real adapter, on .NET 11.0.0 (SDK 11.0.100), Linux x64.

What the spike found, before and after this milestone's fixes:

| Scenario | State machines (before) | Runtime async (before) | Now, both |
|---|---|---|---|
| A breakpoint after an await (`Task.Delay`, `Task.Yield`): it stops on the thread-pool thread, with the parameters, locals and `this` | works | works | works |
| Run to Cursor after an await (the IDE's hidden breakpoint) | works | works | works |
| Step Over an `await` that suspends the method | **ran on to the program's end** | **ran on to the program's end** | **fixed here**: stops on the method's next line once it resumes, on whichever thread |
| Step Over an awaited call of the user's that suspends | ran on | ran on | **fixed here** |
| Step Into an awaited async method | **missed the body**: the step landed in the kickoff method (`[DebuggerStepThrough]` compiler code), went out of it and over the rest of the line | works (an ordinary call) | stops on the method's opening brace (state machines **fixed here**) |
| Step Out of an async method after it resumed, back to the awaiting caller | **ran on**: what called it is the thread pool | **ran on** | state machines **fixed here**: stops on the caller's next line once it resumes, with `DoubleAsync returned 64` first in Locals and as `$ReturnValue`. Runtime async: **a limit** (below): it stops in the awaiting call only where that next calls the user's code (here `Name`'s getter, so on the next line, mid-line), without the value |
| Step Over a resumed method's closing brace, to the awaiting caller | ran on | ran on | state machines **fixed here**: stops on the caller's next line; runtime async **a limit**: `Main` calls none of the user's code after its await, so the program runs on |
| An exception thrown after an await (`all` exceptions): the stop's top frame is the throwing method, on the `throw`, with its locals and `$exception` | works | works | works |
| The call stack after awaits: the resumed method on top, the thread pool's and the runtime's frames `subtle` (folded into [External Code] under Just My Code) | works | works | works |
| The awaiting callers in the call stack (an async call stack) | not shown | not shown | **a limit** for both (below) |
| Set Next Statement after an await, to a later line of the method | allowed | allowed | allowed |
| The two kinds awaiting each other: a breakpoint in the library built the other way, Step Over its await of the program's callback | breakpoint works; the step ran on | breakpoint works; the step ran on | **fixed here** |
| Step Into the program's callback from that library | missed the body (as above) | works | works |
| Step Out of the callback after it resumed, back to the library | ran on | ran on | the runtime-async program's callback, back to the state-machine library: **fixed here** (its `MoveNext` is called as the callback completes). The state-machine callback awaited by the runtime-async library: **a limit**; the step ends in the next call it sees resume, the program's state machine awaiting the library |

The state-machine build's names in the call stack are the compiler's (`Worker.<RunAsync>d__4.MoveNext`); a runtime-async method is shown by its own name (`Worker.RunAsync`).

What the spike found on the way:
- **A cancelled step's completion was taken for the next step's** (code every session uses). When a breakpoint stopped the program while a step's stepper was running (the user's breakpoint; here also a step's own), ICorDebug could still deliver that stepper's completion after the user's next request: the adapter took it for the new step's end and stopped at once, on another thread. `CorDebugTarget` now drops the completion of a stepper it cancelled. It finds it by reference (ClrDebug hands out one wrapper per stepper, seen on Linux); were it ever another wrapper, the completion is taken as before.
- **A breakpoint set where a thread stands** is hit as soon as the thread runs on, and ICorDebug finds a breakpoint by its place, so a late hit of a removed one there reads as the new one's. The temporary breakpoints leave out the line the step starts from.
- **The thread that started a call can resume it.** A call resumed by the thread pool may run on the very thread that stepped into it, once that thread is back in the pool: a first version left such a hit to the thread's stepper, which was lost in the pool's code, and the step ended in the caller instead. A temporary breakpoint's hit ends the step on any thread now, and cancels the stepper. Found by runs of the class, one failure in about eight.
- **A race in the program itself.** A method started by its caller on one thread can resume on another before the caller has handed the method's task its continuation; stopped in between, the task names no awaiting state machine yet. The step out then finds it on another thread's stack: a state machine's `MoveNext` holding an awaiter of the task in a field or a local. Found the same way.

## What the runtime doesn't give (reported upstream)

Three limits need the runtime's own changes; each is a `TODO(issue)` in the code and the checkpoint, to report to dotnet/runtime:

1. **A resumed runtime-async call isn't seen by Just My Code steppers.** The runtime's dispatcher resumes the awaiting call without a method entry, so a Just My Code step from the dispatcher (where a completed call returns) stops only at the first method of the user's that the resumed call *calls*. Reported: treat a resumption of a Just My Code method as its entry for steppers (as a state machine's `MoveNext` call is), or give the debugger an event for it.
2. **The awaiting call of a runtime-async call can't be found.** There is no task between two runtime-async calls, and the runtime's continuation chain (`Continuation`, `ResumeInfo`) isn't exposed through ICorDebug. So there is no async call stack (the awaiting callers aren't on the thread's stack after a resume), no exact Step Out target, and no value for what the call returned. Reported: an ICorDebug way to walk a suspended or running runtime-async call's continuations (method, IL offset, the continuation's object), as debuggers walk `Task` continuations for state machines.
3. **The value an awaited runtime-async call returned** (`ICorDebugILFrame3::GetReturnValueForILOffset` at the call or at its `Await`): none is given. Part of 2.

Until they ship, under runtime async: **Step Out of a resumed method** (and Step Over its closing brace) runs it to its end, then stops in the awaiting call where it first calls the user's code after the await (the step goes out of that method to the caller), or runs on like Continue when it calls none. Only what resumes on the step's own thread is seen: when the awaiting code resumes on another thread, the program runs on too (macOS CI, run 43526072431). No returned value is shown. The same holds for a state machine awaited by runtime-async code. The **async call stack** is a limit for state machines too: the classic IDE reads it from the tasks' continuations; that is deferred (`TODO(issue)`), not a runtime limit.

## Decision

D53, settled after the spike as recommended: fix in the Codalus debugger what its own code can, for both kinds of async code with one mechanism, and record the rest as the limits above, linked to their upstream reports.

### Stepping over an await (both kinds)

- **Which methods.** `ClrModuleInfo.AsyncKind`: `Runtime` for a method with `MethodImplAttributes.Async`; `StateMachine` for a `MoveNext` of a type implementing `IAsyncStateMachine` (an iterator's isn't). Read from metadata, so it doesn't depend on the PDB's format.
- **The call.** A step over or into from an async method keeps the call it starts in (`AsyncCall`): its method, and what tells it from the method's other calls, held by a strong handle until the user lets the program run on: a state machine's object (`this` of `MoveNext`), or a runtime-async method's `this` when it is an object. A static runtime-async method has none: any call of it ends the step (a limit, below).
- **Breakpoints first.** Before the step starts, a temporary breakpoint goes on each line of the method (each visible sequence point; not where one of the user's is bound already, whose hit ends the step as well). The step ends at the first one its call reaches, on any thread (`AsyncResumed`). They are set first, not when the method suspends, because a call resumed by the thread pool returns into code that isn't the user's, where the runtime's Just My Code stepper reports nothing: there was no moment to set them.
- **Suspended or done.** When the step's stepper ends in another method, the call suspended if: a state machine's state is 0 or more (`<>1__state`: -1 running, -2 done); a runtime-async call is out of its frame, in code that isn't the user's or right after the call in its caller, from IL without a `ret` (a completed method returns from its closing brace's line). Then the stepper's end is ignored and the breakpoints wait. Otherwise the step goes on as before (a step over the end of a method stops in its caller).
- **Ending.** The first hit of the call's (on any thread, the stepping one too, once it is back in the thread pool) ends the step and cancels its stepper; the stepper's own end on the next line does too. None goes on the line the step starts from (above). Any other stop (a breakpoint, an exception, Pause) removes the temporary breakpoints.

### Step Into a state machine

The kickoff method (`[AsyncStateMachine]`, `[DebuggerStepThrough]`) and its state machine's methods other than `MoveNext` (its constructor) are made non-user code for the runtime's Just My Code (`ICorDebugFunction2::SetJMCStatus`, `IClrProcess.SetUserCode(module, method, false)`) the first time a step into lands in the kickoff, and the step goes on into what it calls: the runtime's stepper then goes through them to `MoveNext`. Stepping out of the kickoff, as for any non-user code, had skipped the method's body.

### Step Out of a resumed call

When the frame below an async call isn't the user's (the thread pool, the dispatcher), the call has resumed and its awaiting caller isn't on the stack. Step Out (and a step over or into the method's last line):
- **State machines:** the call's task (`<>t__builder.m_task`) names its continuation; when that is another state machine's box (`StateMachine`, an await without a synchronization context), or, before the awaiting call has set it, a state machine on another thread's stack holding an awaiter of the task, the step is a step over that awaiting call's await: its lines get the temporary breakpoints, the program runs without a stepper, and the step ends where the awaiting call resumes, with the task's result (`m_result`) shown as what the method returned.
- **Otherwise** (runtime async; a continuation that isn't a state machine): a Just My Code step into the rest of the call, on into what runs next from it or from code that isn't the user's (the dispatcher, or the task completing, which resume the awaiting call on this thread), and out of a method the call itself calls. It ends in the first of the user's code it reaches: a state machine's `MoveNext` (called as the task completes), or a method the resumed awaiting call calls, out of which it goes to that call. The value is shown only when that is a state machine holding an awaiter of the call's task. For runtime async, the dispatcher's own task (`RuntimeAsyncTask<T>`, the outermost runtime-async call's) is looked up the same way, and a state machine awaiting it gets the temporary breakpoints too, for when its continuation runs on another thread. The limits above.

## Consequences

- Step Over, Step Into and Step Out behave on async code as the classic IDE's do for state machines, but for the async call stack; runtime-async code gets the same for stepping over awaits, breakpoints, values, exceptions and Set Next Statement, and the limits above for Step Out of a resumed call.
- Each step in an async method sets and removes a breakpoint per line of the method; a stop's removal is immediate. A long method costs a few milliseconds more per step.
- A static runtime-async method's other calls (on other threads) can end a step over an await in it, when one reaches one of its lines first: nothing tells the calls apart (`TODO(issue)`, with limit 2).
- `samples/Debugging/Async.slnx` is its own solution, built only by `RuntimeAsyncDebuggingTests`' fixture, so the other debugger classes don't build it.
- Not covered by the spike, `TODO(issue)`: Edit and Continue in a runtime-async method after an await; Parallel Stacks, the Threads window, and the profiler's .NET Async view and call trees (M18b) on runtime-async code; the async call stack (both kinds); state machines with a synchronization context (a UI thread's continuation isn't the box).
