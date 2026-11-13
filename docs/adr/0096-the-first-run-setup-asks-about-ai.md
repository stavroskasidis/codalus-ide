# 0096. The first-run setup asks about AI, welcomes and sums up

- **Status:** accepted (0.13, 2026-11-12)
- **Date:** 2026-11-12
- **Deciders:** maintainer (the grilling of 2026-11-12, below)
- **Amended by:** [ADR 0097](0097-the-ides-ai-features-through-an-external-agent.md) (an agent answers the AI actions and, once turned on, suggestions as you type: the agent card says so)
- **Amends:** [ADR 0062](0062-ai-provider-api-and-data-boundaries.md) (the first-run setup offers AI; it stays off unless chosen there), M46 ([checkpoint](../checkpoints/m46.md): "AI stays off and isn't mentioned" no longer holds; the setup has five steps, a welcome page and a summary)

## Context

The first-run setup (M46) had four steps: Appearance, Keyboard, Your settings and Tools. It said nothing about AI, on purpose: ADR 0062 keeps AI off by default, and M46 read that as "not asked about". Since then the IDE gained three ways to use AI (the built-in assistant with a provider, external agents in AI Chat, and an agent's own CLI in AI Console), each set up from a different place: Tools → Options → AI, AI Chat's empty state, Tools → AI. A new user who wanted any of them had to find those first.

The maintainer asked to improve the first-time experience: an AI step in the setup "letting the user choose what their AI experience will be", and more suggestions, "e.g. add a welcome screen". While it was being built, the maintainer added: the AI step should explain the benefit of running an agent from Codalus IDE, and how the modes differ.

## The maintainer's decisions (the grilling, 2026-11-12)

| Question | Options | Verdict |
|---|---|---|
| What the AI step offers | No AI, an agent or a provider; no AI or an agent only; an on/off switch | **Three paths**: No AI (preselected), a coding agent (the one-click templates and More agents…), the built-in assistant with a provider. |
| When the connecting happens (Connect is an overlay of the main window and uses its Terminal, both behind the setup window) | After Done, in the main window; at the click, hiding the setup; inside the setup window | **After Done, in the main window.** The step only records the choice. |
| Inline and next edit suggestions | Two unchecked boxes under the built-in assistant; not in the setup; checked by default | **Two unchecked boxes.** |
| People who already use the IDE | What's New and Help only; a one-time notification; the AI step once after the upgrade | **What's New and Help only.** M46's rule holds: an upgrade never asks. |
| More inside the setup | A welcome page; a "coming from" preset; a "You're all set" page; the editor's basics on Appearance | **All four.** |
| More after the setup | A sample project in the start window; a getting-started checklist; What's New and tips in the start window; none now | **None now** (deferred, below). |
| Font and zoom on Appearance | The editor's font size; that and the font family; a new zoom for the whole UI | **The editor's font size only.** A zoom for the UI is a feature of its own. |
| One path or a combination | One path with a note; an agent and a provider together; several agents | **One path, with a note** that suggestions as you type need the built-in assistant. |
| Delivery | A mockup first; one change now; two changes | **One change now**, the mockup updated with it. |

## Decision

### The pages

The setup is a welcome page (a first start only), five steps and a summary (`SetupPage`, `SetupStep`, `SetupViewModel`):

- **Welcome**: the logo, what the setup covers, and "I'm coming from": the classic IDE, VS Code, Rider or ReSharper, somewhere else. None is chosen at first. An origin preselects the keyboard scheme the next time the Keyboard step is entered (Default, VS Code, ReSharper) and marks the matching importer on Your settings (a `.vssettings` file, VS Code); it saves nothing and leaves the theme alone. Its buttons are **Get Started** and **Use the defaults** (Skip setup). Help → Set Up Codalus IDE opens on Appearance, without it.
- **Appearance** gains the editor's font size (`editor.fontSize`, 8 to 32), shown on a sample of C# in the previewed theme's syntax colors and saved with the step when it changed.
- **AI** is step 5, after Tools.
- **The summary** ("You're all set") lists the theme, the keys, the font size, the tools and AI, shows four keys as the chosen scheme has them (every command, Go To All, build, start debugging), and says what its button does next. Back returns to the AI step; Skip setup doesn't show there.

### The AI step

Three cards. No AI has a line: the IDE as it is, nothing sent anywhere, completion and refactorings need no AI, Tools → AI turns it on later. The other two carry the same four facts so they read side by side: who answers, what it does, what leaves this machine, what you need. (The first build gave No AI the four facts too, "No one" and "Nothing" three times; the maintainer called it noise, 2026-11-12.)

| | A coding agent | The built-in assistant |
|---|---|---|
| Who answers | The agent's own program, with the user's account there | A model of the provider the user chooses |
| What it does | Works through a task by itself, in AI Console or AI Chat; no suggestions as you type | AI Chat with agent mode, the AI actions, and suggestions as you type when ticked |
| What leaves | What the agent reads, to its own provider (ADR 0079) | Only through `AiService`'s checks (ADR 0062) |
| What it needs | Node.js or its CLI, and a sign-in or key | A key, or a model server on this machine |

- **A coding agent is the recommended way** (the maintainer, 2026-11-12), as AI Console is the recommended window for one (ADR 0095): its card says "(recommended)" beside its title. Recommended isn't preselected.
- **No AI is chosen unless the user chooses another.** An agent or a provider is chosen, never assumed: neither list has a preselected entry.
- **A coding agent** shows the one-click templates (ADR 0083), the user's other connected agents (marked Connected) and More agents…; why to run an agent from Codalus IDE and not from a terminal, in three lines (the IDE's tools of ADR 0092, the preview and approvals of AI Chat, references from the editor); and its two windows, AI Console and AI Chat, with a line on what each does (ADR 0095). A remote window says why no agent connects (ADR 0083) and disables the list.
- **The built-in assistant** shows the providers the installed plugins declare, a local one marked, and the two as-you-type switches with what each sends, both off.
- **One path.** An agent and a provider aren't combined in the setup; either is added later from Tools → AI.
- **Short.** The maintainer, seeing the first build, asked for a less verbose step (2026-11-12): each fact is a line or two (80 characters at most, which `FirstRunSetupTests` holds it to), the reasons are three lines, the windows two, and the providers show by name. The user guide carries what was cut: which CLIs take the IDE's tools in AI Console, what an agent reads by itself, consoles that come back, and what Connect does.

### Nothing before Done

The step writes nothing and starts nothing, on Next or Back. Only the summary's button ends the setup as done, and then `ShellHost.ApplySetupAiAsync` runs in the main window, before the start window:

- **No AI**, with AI on: `ai.enabled` goes off in the user's settings. Agents, the provider and the other switches stay.
- **A coding agent**: Connect opens for its template (the list of agents for More agents…, or when none was chosen). Connect keeps its own consent and writes the agent and `ai.enabled` only after a passing test (ADR 0083); cancelling it leaves AI off. An agent that is connected already only turns AI on and becomes AI Chat's agent.
- **The built-in assistant**: `ai.enabled` on, the built-in assistant answering in AI Chat, the two switches as ticked, then the provider with its key and model as Choose AI Provider… does (asked for when none was chosen).

Skip setup, `Esc` and closing the window apply nothing, whatever the step holds. A step left as the settings have it (the default at a first start; what is on when the setup runs again) changes nothing, and the button reads **Done**. The step's state is read from the user's settings layer, where all of AI's switches live.

What still holds of ADR 0062: AI is off by default, only the user's own choice turns it on, a provider is chosen and never assumed, and the as-you-type features have their own switches, off by default.

## Consequences

- A new user meets AI once, with what each way costs in data, and can leave it off by doing nothing.
- The setup is longer: five steps and two pages. Use the defaults on the welcome page and Skip setup on every step end it at once.
- People who upgrade aren't asked. What's New names the step, and Help → Set Up Codalus IDE shows it with what they have.
- The step's text states product facts (which CLIs take the IDE's tools in AI Console, what each feature sends). They change with ADR 0092 and 0095 and must be kept in step with them and with `docs/user-guide/ai.md`.
- **Tests** (`FirstRunSetupTests`, Linux validated; Windows and macOS not yet): the welcome page and each origin; the font size on the sample and in the settings; the AI step's cards, facts and lists, from the keyboard; an agent chosen opening Connect only after the summary, with the start window waiting; skipping and closing applying nothing; the built-in assistant with a model server stand-in; No AI turning it off; a connected agent. The smoke test's `shell.setup` walks the pages in the shipped app and takes the website's pictures.
- Deferred (`TODO(issue)`):
  - a sample project in the start window ("Try a sample"), for someone with no code yet;
  - a getting-started checklist on the Start page (open, build, debug, Go To All, run a test);
  - What's New and the tip of the day in the start window (M38's open gap);
  - a zoom for the whole UI, then offered on Appearance;
  - an agent and a provider chosen together in the setup;
  - an importer for Rider's settings (the origin preselects ReSharper's keys only).
