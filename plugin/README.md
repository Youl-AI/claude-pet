# plugin/

This is the Claude Code plugin package: hook scripts under `hooks/`, the
plugin manifest under `.claude-plugin/`, and a `bin/` directory that holds
the built pet executable.

## `bin/claude-pet.exe` IS tracked in git

`plugin/bin/claude-pet.exe` is committed on purpose. A marketplace entry with
`"source": "./plugin"` is delivered to installing users by a **git clone**,
which carries only tracked files — so an ignored binary simply does not
reach them, and `session_start.ps1` then silently skips launching. The old
`.gitignore` had a bare `bin/`, which matches at any depth and swallowed
this directory; it is now scoped to `src/**/bin/` and `tests/**/bin/`.

The executable is named `claude-pet.exe`, not `pet.exe`. The VS Code Python
extension ships its own `pet.exe` (python-env-tools), and sharing the name
meant a user reaching for `taskkill /IM pet.exe` or `Get-Process pet` hit
that process instead of this one. The pet's own liveness check has always
used the `Local\claude-pet` mutex rather than a process name, so only the
humans were confused — but that was reason enough.

Debug symbols (`*.pdb`) stay ignored: they are useless to users and embed
the author's absolute build paths (including their Windows username).
Rebuild with the command below after changing any C# source.

Build it with:

```
dotnet publish src/PetApp/PetApp.csproj -c Release -r win-x64   -p:SelfContained=false -p:PublishSingleFile=true   -p:DebugType=none -p:DebugSymbols=false   -o plugin/bin
```

This is a **framework-dependent** publish (relies on an already-installed
.NET + WPF desktop runtime), so `claude-pet.exe` comes out well under 1 MB instead
of bundling the whole runtime. Do not publish with `--self-contained true`
(or omit the flag entirely) — that pulls in the entire .NET runtime plus
native WPF DLLs and produces a 100+ MB binary, which does not belong in
this directory, let alone in git.

> Note: the more obvious `--self-contained false` CLI switch does not
> reliably override an inferred self-contained publish in some environments
> when combined with `-r` and `-p:PublishSingleFile=true` — that is why the
> command above uses the `-p:SelfContained=false` MSBuild property instead.
> If the resulting `claude-pet.exe` is large, do a clean rebuild (delete
> `src/PetApp/bin`, `src/PetApp/obj`, `src/PetCore/bin`, `src/PetCore/obj`,
> and `plugin/bin`), republish, and verify the resulting `claude-pet.exe` size
> before committing anything.

## Why this matters for the plugin to work at all

`hooks/session_start.ps1` launches `$env:CLAUDE_PLUGIN_ROOT/bin/claude-pet.exe` on
every `SessionStart`. If `plugin/bin/claude-pet.exe` does not exist, the hook's
`Test-Path` check simply skips starting the pet — it fails silently rather
than erroring, so on a fresh checkout the plugin will appear to do nothing
until you run the publish command above once.
