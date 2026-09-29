#!/usr/bin/env python3
"""Moves C# code from Friflo's ECS names to Sage's own (issue #25).

Idempotent: run it again on code that is already migrated, or after merging a branch written against
the old names, and it changes only what is still spelled the old way.

    python3 tools/migrate_ecs_api.py            # rewrite src/, games/, tests/ in place
    python3 tools/migrate_ecs_api.py --check    # list what would change; exit 1 if anything would
    python3 tools/migrate_ecs_api.py path ...   # only these files or directories

What it does (every rule is a plain regex over the text; see CS_RULES and PROJECT_RULES):
  - drops `using Friflo.Engine.ECS;` and the `Friflo.Engine.ECS.` prefix of the types Sage now owns
    (Entity, IComponent, ITag, Tags, ArchetypeQuery, CommandBuffer);
  - ArchetypeQuery<...> -> Query<...>, CommandBuffer -> EntityCommands,
    Commands.DeleteEntity(e.Id) -> Commands.Destroy(e),
    WithoutAllComponents(ComponentTypes.Get<T>()) -> WithoutComponent<T>();
  - Friflo's EntityName component -> the string Entity.Name:
    `e.Name = new EntityName(x)` -> `e.Name = x`,
    `e.TryGetComponent<EntityName>(out var n) ? n.value : x` -> `e.Name ?? x`,
    `e.GetComponent<EntityName>().value` and `e.Name.value` -> `e.Name`;
  - an entity component's `.Type.Type` (Friflo's ComponentType) -> `.Type` (a System.Type);
  - the console registrars WorldCommands -> WorldConsoleCommands and ScaleCommands ->
    ScaleConsoleCommands (their files are renamed with `git mv` too);
  - in .csproj / .props: drops the `Friflo.Engine.ECS` global using and the `Transform` alias (with its
    comment), which only existed to beat Friflo's own Transform.

What it leaves alone:
  - files that alias Friflo (`using F = Friflo.Engine.ECS;`): the hand-written implementation of the
    Sage types in Sage.Simulation (World, ComponentSchema, the savers, the ECS schema), and the few
    tests that inspect Friflo's schema itself;
  - bin/, obj/ and generated code.

What it cannot do (the build points at each one):
  - ComponentSchema's public API takes System.Type now, not Friflo's ComponentType/TagType, so a
    `type.Type` on what TryComponent/TryResolveComponent/TryTag returned becomes `type`;
  - a query held in a nullable field (`Query<T>?`) is a Nullable<struct> now;
  - a Friflo API with no Sage equivalent (EntityStore, EntityUtils, the schema types) belongs in
    Sage.Simulation behind `using F = Friflo.Engine.ECS;`, or has to be replaced by the Sage API.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIRS = ["src", "games", "tests"]
SKIP_DIRS = {"bin", "obj", ".git"}
IMPLEMENTATION_MARKER = "using F = Friflo.Engine.ECS;"

CS_RULES = [
    # usings and fully qualified names of what Sage now owns
    # (a using at the start of a line only, and no name inside a string: a test's embedded source and
    # the generators' type-name constants are rewritten by hand)
    (r"^using Friflo\.Engine\.ECS;[ \t]*\r?\n", ""),
    (r"(?<![\"\w.])Friflo\.Engine\.ECS\.ArchetypeQuery\b", "Query"),
    (r"(?<![\"\w.])Friflo\.Engine\.ECS\.(Entity|IComponent|ITag|Tags|EntityName|CommandBuffer)\b", r"\1"),
    # queries and deferred structural changes
    (r"\bArchetypeQuery\b", "Query"),
    (r"\bCommandBuffer\b", "EntityCommands"),
    (r"\bCommands\.DeleteEntity\(\s*([\w.\[\]]+?)\.Id\s*\)", r"Commands.Destroy(\1)"),
    (r"\.WithoutAllComponents\(\s*ComponentTypes\.Get<(\w+)>\(\)\s*\)", r".WithoutComponent<\1>()"),
    (r"^([ \t]*)var (\w+) = ComponentTypes\.Get<(\w+)>\(\);[ \t]*\r?\n(.*?)\.WithoutAllComponents\(\2\)",
     r"\4.WithoutComponent<\3>()"),
    # Friflo's EntityName component -> Entity.Name (a string)
    (r"\.Name\s*=\s*new EntityName\((.+?)\);", r".Name = \1;"),
    (r"\.TryGetComponent<EntityName>\(out var (\w+)\)\s*\?\s*\1\.value\b", r".Name is { } \1 ? \1"),
    (r"\.TryGetComponent\(out EntityName (\w+)\)\s*\?\s*\1\.value\b", r".Name is { } \1 ? \1"),
    (r"\.GetComponent<EntityName>\(\)\.value\b", ".Name"),
    (r"\.Name\.value\b", ".Name"),
    (r"\.Name is \{ \} (\w+) \? \1 : ", ".Name ?? "),
    # one of an entity's components: Friflo's ComponentType -> System.Type
    (r"\b(component|c|entry)\.Type\.Type\b", r"\1.Type"),
    # the console registrars (ConsoleCommand is the console's; EntityCommands the ECS's)
    (r"\bWorldCommands\b", "WorldConsoleCommands"),
    (r"\bScaleCommands\b", "ScaleConsoleCommands"),
]

PROJECT_RULES = [
    (r"^[ \t]*<!-- Friflo has a Transform too; this one is Sage's \(docs/design/03 §3\.1\)\. -->[ \t]*\r?\n", ""),
    (r"^[ \t]*<Using Include=\"Friflo\.Engine\.ECS\" />[ \t]*\r?\n", ""),
    (r"^[ \t]*<Using Include=\"Sage\.Simulation\.Transform\" Alias=\"Transform\" />[ \t]*\r?\n", ""),
]

FILE_RENAMES = [
    ("src/Sage.Simulation/ECS/WorldCommands.cs", "src/Sage.Simulation/ECS/WorldConsoleCommands.cs"),
    ("src/Sage.Simulation/Diagnostics/ScaleCommands.cs", "src/Sage.Simulation/Diagnostics/ScaleConsoleCommands.cs"),
]

CS_COMPILED = [(re.compile(p, re.MULTILINE), r) for p, r in CS_RULES]
PROJECT_COMPILED = [(re.compile(p, re.MULTILINE), r) for p, r in PROJECT_RULES]


def source_files(paths):
    for path in paths:
        if os.path.isfile(path):
            yield path
            continue
        for dirpath, dirnames, filenames in os.walk(path):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for name in sorted(filenames):
                if name.endswith((".cs", ".csproj", ".props")):
                    yield os.path.join(dirpath, name)


def migrate(text, rules):
    for pattern, replacement in rules:
        text = pattern.sub(replacement, text)
    return text


def main(argv):
    check = "--check" in argv
    targets = [a for a in argv if not a.startswith("--")] or [os.path.join(ROOT, d) for d in DIRS]
    changed = []

    for old, new in FILE_RENAMES:
        old_path, new_path = os.path.join(ROOT, old), os.path.join(ROOT, new)
        if os.path.exists(old_path) and not os.path.exists(new_path):
            changed.append(f"{old} -> {new}")
            if not check and subprocess.call(["git", "mv", old, new], cwd=ROOT) != 0:
                os.rename(old_path, new_path)

    for path in source_files(targets):
        is_cs = path.endswith(".cs")
        with open(path, "rb") as f:
            raw = f.read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw.decode("utf-8-sig")
        if is_cs and (IMPLEMENTATION_MARKER in text or path.endswith(".g.cs")):
            continue
        new = migrate(text, CS_COMPILED if is_cs else PROJECT_COMPILED)
        if new != text:
            changed.append(os.path.relpath(path, ROOT))
            if not check:
                with open(path, "wb") as f:
                    f.write((b"\xef\xbb\xbf" if bom else b"") + new.encode("utf-8"))

    for c in changed:
        print(("would change " if check else "changed ") + c)
    print(f"{len(changed)} change(s){' pending' if check else ''}")
    return 1 if check and changed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
