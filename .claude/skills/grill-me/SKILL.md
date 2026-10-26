---
name: grill-me
description: Interview the user relentlessly about a plan or design until reaching shared understanding, resolving each branch of the decision tree one question at a time. Use when the user wants to stress-test a plan, get grilled on a design, or says "grill me", "poke holes in this", "challenge my plan" or similar. Also use proactively, before writing code or a plan, when the user asks for a new feature, a significant change, a refactor or an architecture/design decision and the request leaves real choices open (behavior, scope, data model, API shape, UX, edge cases, migration) that the code cannot answer.
---

Interview the user relentlessly about every aspect of their plan until you reach a shared understanding. Walk down each branch of the design tree, resolving dependencies between decisions one-by-one.

## When it was not asked for

If you started this skill on your own (the user did not ask to be grilled), say so in one sentence first: you have a few questions before you start, and they can say "skip" to let you go with sensible defaults. If they skip, state the defaults you chose and continue.

Do not grill for small, clear changes: a bug fix with an obvious cause, a rename, a one-file tweak, or a task whose open points the codebase settles.

## How to ask questions

Use the **AskUserQuestion tool** for every question you ask. Never pose questions as plain text in your response — always use the multiple-choice popup so the user can quickly select an answer or type a custom one.

Ask **one question at a time**. Wait for the user's answer before moving to the next question. This keeps the conversation focused and prevents overwhelm.

For each question, provide 2–4 concrete multiple-choice options representing the most likely answers or directions. Think about what the user would realistically choose — generic options like "Yes" / "No" aren't helpful unless the question is genuinely binary. Put your recommendation first and mark it "(Recommended)". The user always has the "Other" field available to write something custom.

## Flow

1. After receiving an answer, briefly acknowledge the decision (1–2 sentences max), then immediately ask the next question via AskUserQuestion.
2. If a question can be answered by exploring the codebase, its docs or the repository's agent instructions, explore them yourself instead of asking the user.
3. Ask about what changes the outcome first (scope, behavior, data, contracts), details last.
4. Continue until all branches of the design tree are resolved.
5. When finished, provide a concise summary of all decisions made, then carry on with the original task using them.
