# Submitting PICASSO to Forge

This checklist covers what's needed to publish PICASSO to the OutSystems Forge marketplace, once the Extension and demo app exist in a real OutSystems environment (per [`README-IntegrationStudio.md`](../src/Picasso.Extension/README-IntegrationStudio.md) and [`outsystems-app-spec.md`](outsystems-app-spec.md)). Everything here is grounded in OutSystems' own published guidance — [The complete guide to creating components](https://success.outsystems.com/support/forge_components/the_complete_guide_to_creating_components/) and [Forge components best practices](https://success.outsystems.com/documentation/best_practices/development/forge_components_best_practices/) — not guessed at. Neither Integration Studio nor Service Studio nor the Forge submission form itself is scriptable, so this stops at "here's exactly what to fill in," same as the rest of this repo.

## Assets already prepared in this repo

- **`icon.svg` / `icon-1024.png`** — application/module icon. OutSystems' spec: PNG, 1024×1024, transparency allowed. Use the same icon at every level (Application → Module → Actions) for visual consistency, per their own guidance — don't pick a different icon per action.
- **`icon-128.png`** — smaller variant for anywhere a 1024px file is impractical (this doc, a README badge, a social preview).
- **`LICENSE`** — MIT, already satisfies the license field.

Not prepared here, needs a live environment to produce: **screenshots** of the demo app's screens (once built per the app spec) — Forge listings are far more convincing with a picture of the layout table and decoded records than with text alone.

## Listing fields

| Field | Value |
|---|---|
| **Name** | `Picasso` — PascalCase, no file extensions, matches OutSystems' own naming convention example format ("Google Calendar Connector", not "Calendar_v4.oml"). |
| **Category** | **Integrations** — one of Forge's five standard categories (Integrations, Device capabilities, User Interface, Functional libraries & utilities, Development tools); this is unambiguously an integration. |
| **Short description** | One line, but the *listing* description must not stop there (see below) — e.g. "Parses real COBOL copybooks into OutSystems-ready structures — no hand-transcribed byte offsets." |
| **Long description** | Per OutSystems' own guidance, this must **not** be a one-liner. It should: explain why you built it, the problem it solves, notable features, and links to any external resources. The README's "Why this exists" and "What it does" sections are written for exactly this and can be reused close to verbatim. |
| **License** | MIT (link to `LICENSE`). |
| **Support / source** | Link to `github.com/jonasfiers/PICASSO`. |
| **Distribution format** | **OAP** — OutSystems' explicitly recommended format ("makes it easier to install and upgrade the component"). Export the module as `.oap` from Service Studio before uploading. |

## Before submitting — Forge's own best-practice checklist

Straight from their best-practices doc, applied to PICASSO specifically:

- [ ] **Icons at every level** — Application, Module, and Actions all show the same icon (`icon-1024.png`), not just the top-level one.
- [ ] **A separate demo app** — already planned in `outsystems-app-spec.md`. OutSystems' convention: name it `Picasso Demo` (component name + "Demo"/"Example") so it turns up when someone searches for help with Picasso itself. Single central screen, no login — the app spec already describes exactly that.
- [ ] **Documentation covers**: install/setup steps (the Integration Studio wiring doc already does this), and an overview of the public actions and their signatures (the README's action-surface table already does this — carry it into the Forge long description or link straight to the README).
- [ ] **Reactive Web over Traditional Web** for the demo app's UI, if choosing between them — OutSystems promotes this explicitly for anything Forge-facing. (The Extension itself has no UI framework choice; this only applies to the demo app screens.)
- [ ] **Versioning** — tag the first submission `1.0.0` (see `CHANGELOG.md`) and detail changes in every future upload; OutSystems calls this out specifically so the community can tell whether a bug they hit is already fixed.

## Compatibility — read this before building the demo app

**Integration Studio Extensions (the path this repo documents) are an OutSystems 11 / Traditional-Service-Studio mechanism only.** OutSystems Developer Cloud (ODC) does not run Integration Studio at all — confirmed against OutSystems' own ODC documentation. If a submission needs to reach ODC users too, that is a **separate, second wrapper**, not a checkbox on this one:

- ODC's equivalent of an Extension is an **External Library**, built with the standalone **OutSystems External Libraries SDK** (a NuGet-based toolkit, not a GUI app — it decorates plain C# with SDK attributes instead of Integration Studio's generated `Mss`/`ss`-prefixed stubs) and uploaded as a ZIP through the ODC Portal's *Integrate → External Logic* screen, no ODC organization account required just to build it.
- Because `Picasso.Core` already has zero OutSystems dependency (that was a deliberate netstandard2.0 design choice from day one), it's the same engine either way — an ODC wrapper would mean a **third** small project (`Picasso.ExternalLibrary` or similar) alongside `Picasso.Extension`, attribute-decorated per the SDK's own conventions, not a rewrite of the parser/codec.
- Forge submission itself is identical either way afterward — "submit it to Forge... the submission or update process is the same as for any asset developed in OutSystems," per OutSystems' own ODC docs.

This repo does not build that second wrapper. It's flagged here as a real, low-cost expansion path — reusing the exact same `Picasso.Core.dll` — worth a deliberate yes/no rather than defaulting into "O11 only" by omission.

## What this repo could not do for you

Same caveat as the Integration Studio doc, one level up: the Forge submission form itself lives at `forge.outsystems.com` behind an OutSystems account login, and actually submitting is not something automatable from outside a real session there. Everything above is what to have ready before that point.
