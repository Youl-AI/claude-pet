# plugin/

This is the Claude Code plugin package: hook scripts under `hooks/`, the
plugin manifest under `.claude-plugin/`, and a `bin/` directory that holds
the built pet executable.

## `bin/pet.exe` is a build artifact, not source

`plugin/bin/` is **not tracked in git** (see `.gitignore`). It is produced
by publishing `src/PetApp`, and it must be rebuilt after every fresh
checkout before the plugin will do anything visible.

Build it with:

```
dotnet publish src/PetApp/PetApp.csproj -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o plugin/bin
```

This is a **framework-dependent** publish (relies on an already-installed
.NET + WPF desktop runtime), so `pet.exe` comes out well under 1 MB instead
of bundling the whole runtime. Do not publish with `--self-contained true`
(or omit the flag entirely) — that pulls in the entire .NET runtime plus
native WPF DLLs and produces a 100+ MB binary, which does not belong in
this directory, let alone in git.

> Note: the more obvious `--self-contained false` CLI switch does not
> reliably override an inferred self-contained publish in some environments
> when combined with `-r` and `-p:PublishSingleFile=true` — that is why the
> command above uses the `-p:SelfContained=false` MSBuild property instead.
> If the resulting `pet.exe` is large, do a clean rebuild (delete
> `src/PetApp/bin`, `src/PetApp/obj`, `src/PetCore/bin`, `src/PetCore/obj`,
> and `plugin/bin`), republish, and verify the resulting `pet.exe` size
> before committing anything.

## Why this matters for the plugin to work at all

`hooks/session_start.ps1` launches `$env:CLAUDE_PLUGIN_ROOT/bin/pet.exe` on
every `SessionStart`. If `plugin/bin/pet.exe` does not exist, the hook's
`Test-Path` check simply skips starting the pet — it fails silently rather
than erroring, so on a fresh checkout the plugin will appear to do nothing
until you run the publish command above once.
