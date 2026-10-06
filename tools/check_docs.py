# -*- coding: utf-8 -*-
"""Check the documentation against the code (TODO R19).

Three passes of this engine in a row ended the same way: the code was right and something the code
*said about itself* was wrong. A doc promised `.gltf` when the loader read `.glb`; `asset_list` offered
to reload a sound that `asset_reload` then refused; `ent_fire`'s own help advertised `!player`, which
nothing implemented. Each was found by a person reading carefully, which is not a strategy.

Claims fall into three kinds, and only two of them can be checked by a script:

  - **Behaviour** ("walking into a trigger fires OnStartTouch") — only a test proves this, so the rule is
    that the doc *cites* the test, and this script checks the citation resolves. Writing the citation is
    what makes you notice you have no test to cite.
  - **Vocabulary** (command, cvar, record, output and game event names) — read from the engine's
    registry dump and checked against every name the docs use. Its unit tests are tools/test_check_docs.py.
  - **Numbers** (how many commands, records, tests) — computed rather than typed, and `--fix` writes them.

What it cannot check is whether the thing works, which is what the scripted in-game runs are for.

The names come from the **registry dump** (`RegistryDump`, docs/REDESIGN.md §4.8, issue #18): the host
boots the Sandbox, writes every command, cvar, record type and entity input it registered, and quits. It
used to find them with regular expressions over call shapes, which is how the day `[Record]` gained a
`Plugin` argument the engine went from 25 record types to 1 without anyone being told. A name in the
dump was registered by running code, so renaming the registration API cannot hide it.

Usage:
    # once per build: the dump (Linux needs a display, hence xvfb-run; Windows runs it as is)
    dotnet build Sage.sln -c Development -p:SageSkipShaders=true
    (cd src/Sage.Host/bin/Development/net8.0 && xvfb-run -a ./Sage.Host -game ../../../../../games/Sandbox \
        -dump-registry ../../../../../user/registry.json)

    python tools/check_docs.py                 # links, citations, vocabulary, computable counts
    python tools/check_docs.py --tests 490     # also check the test count quoted in the docs
    python tools/check_docs.py --tests 490 --fix   # rewrite the counts to match
    python tools/check_docs.py --registry other.json   # a dump somewhere else (default user/registry.json)
    python -m unittest discover -s tools -p 'test_*.py'   # the checker's own tests
"""
import argparse
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CODE_DIRS = ['src', 'games']
DOC_SKIP_DIRS = {'.git', 'obj', 'bin', 'node_modules', 'user', 'packages'}
DEFAULT_REGISTRY = os.path.join(ROOT, 'user', 'registry.json')
REGISTRY_FORMAT = 1

# A line that says a thing is absent is not claiming it exists, even inside an "As built" section —
# those sections end with what is left to do, and naming it is the point.
ABSENCE = ('not yet', 'not built', 'not done', 'left:', 'left is', 'later', 'open question',
           'still to', 'would be', 'wants', 'waiting', 'deferred', 'instead of')


# ---------------------------------------------------------------- reading the code

class RegistryError(Exception):
    pass


def read_registry(path):
    """Every name the engine registers, by kind, from the registry dump the host wrote.

    Not from the source: two regex-based versions of this function each missed names the moment a
    registration was spelled a new way (a helper taking the name as a parameter; `[Record("x", Plugin =
    ...)]`), and each failure was silent — fewer names, fewer checks, and still "0 problem(s)".
    """
    if not os.path.exists(path):
        raise RegistryError('no registry dump at %s; write one with the host\'s -dump-registry '
                            '(see the top of tools/check_docs.py, or CLAUDE.md)' % rel(path))
    with open(path, encoding='utf-8') as f:
        dump = json.load(f)
    if dump.get('format') != REGISTRY_FORMAT:
        raise RegistryError('%s is registry format %r; this script reads format %d'
                            % (rel(path), dump.get('format'), REGISTRY_FORMAT))

    # A dump written before the host listed game events would make every event name in the docs look
    # wrong; say so instead.
    if 'gameEvents' not in dump:
        raise RegistryError('%s has no "gameEvents"; it was written by an older host, so write it again'
                            % rel(path))
    events = dump['gameEvents']

    def names(section, key='name'):
        return {entry[key] for entry in dump.get(section, [])}

    return {
        'command': names('commands'),
        'cvar': names('cvars'),
        'record': names('recordTypes'),
        'input': names('entityInputs'),
        'output': names('entityOutputs'),
        # Game events by their C# name, a generic one by its stem (`Added<T>` is `Added`): nothing
        # registers an event, so the host finds the `[GameEvent]` structs (TODO #62, issue #298).
        'event': {entry['name'].split('<')[0] for entry in events},
        'action': names('inputActions'),
        'part': names('prefabParts', 'id'),
        'component': names('components', 'id'),
        'system': names('systems', 'id'),
        # The open vocabularies (issue #28): their names and every registered entry's id.
        'vocabulary': names('vocabularies') | {entry['id'] for v in dump.get('vocabularies', []) for entry in v.get('entries', [])},
        'source': '%s (%s build, game %s)' % (rel(path), dump.get('config', '?'), dump.get('game') or 'none'),
    }


def read_code_literals():
    """Every name-shaped string literal in the code.

    The first two attempts at this matched registration calls — `CVars.Register("x")` and friends — and
    both were wrong, because a name can be registered by a helper that takes it as a parameter
    (`RegisterBus(ctx, "snd_volume", …)`) and because cvars are registered through whatever the local
    variable happens to be called. The question this check asks is narrower than "how is it registered":
    it is **"does this name exist in the code at all"**, and a string literal answers that exactly.

    Comments are stripped first, so a name that survives only in prose does not count as existing — which
    is the whole failure this tool is for.
    """
    literals = set()
    string = re.compile(r'"([a-z][a-z0-9_]*)"')
    comment = re.compile(r'//.*$', re.M)

    for directory in CODE_DIRS:
        for base, dirs, files in os.walk(os.path.join(ROOT, directory)):
            dirs[:] = [d for d in dirs if d not in ('obj', 'bin')]
            for name in files:
                if not name.endswith('.cs'):
                    continue
                text = open(os.path.join(base, name), encoding='utf-8', errors='replace').read()
                literals.update(string.findall(comment.sub('', text)))
    return literals


# `public event Action<UiLayer, Widget>? Activated;`: "its `Activated` event" is a C# event, not a game event.
CSHARP_EVENT = re.compile(r'\bevent\s+[A-Za-z0-9_<>?,.\s]+?\s([A-Z][A-Za-z0-9_]*)\s*[;={]')


def read_code_names():
    """Every PascalCase name the code declares or uses, and every PascalCase string literal in it; and
    the C# events it declares (the second set).

    What an output or event name in a document may also be: a C# identifier (`OnWorldCreated` is a
    module's method, not an output) or a literal (`FireOutput(trigger, "OnStartTouch")`, an output fired
    by name). Comments are stripped and string contents are not identifiers, so a name that survives only
    in prose or in a message does not count as existing.
    """
    names = set()
    # Strings and comments in one pass, left to right, so a `//` inside a string ("http://...") is not
    # taken for a comment, nor a quote inside a comment (or the char '"') for a string.
    token = re.compile(r"'(?:[^'\\\n]|\\.)'|" r'"(?:[^"\\\n]|\\.)*"|//[^\n]*|/\*.*?\*/', re.S)
    literal = re.compile(r'"([A-Z][A-Za-z0-9_]*)"')
    identifier = re.compile(r'\b[A-Z][A-Za-z0-9_]*\b')

    def strip(match):
        found = literal.fullmatch(match.group(0))
        if found:
            names.add(found.group(1))
        return '""' if match.group(0)[0] in '"\'' else ' '

    events = set()
    for directory in CODE_DIRS:
        for base, dirs, files in os.walk(os.path.join(ROOT, directory)):
            dirs[:] = [d for d in dirs if d not in ('obj', 'bin')]
            for name in files:
                if name.endswith('.cs'):
                    text = token.sub(strip, open(os.path.join(base, name), encoding='utf-8', errors='replace').read())
                    names.update(identifier.findall(text))
                    events.update(CSHARP_EVENT.findall(text))
    return names, events


def read_content_names():
    """The names content defines: record ids, and the classnames and targetnames in a map.

    A document may write `lit_default` or `hut_door` without being told off, because those are real
    names — they are simply defined in data rather than in code. An allowlist of guesses stood here for
    one commit and exempted nothing, which is the usual fate of a list that enumerates what a rule could
    derive.
    """
    names = set()
    record_id = re.compile(r'"id"\s*:\s*"([a-z_0-9]+)"')
    map_name = re.compile(r'"(?:targetname|classname)"\s+"([a-z_0-9]+)"')

    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in DOC_SKIP_DIRS]
        for name in files:
            path = os.path.join(base, name)
            if name.endswith('.json'):
                names.update(record_id.findall(open(path, encoding='utf-8', errors='replace').read()))
            elif name.endswith('.map'):
                names.update(map_name.findall(open(path, encoding='utf-8', errors='replace').read()))
    return names


def read_test_names():
    names = set()
    pattern = re.compile(r'public\s+(?:async\s+)?(?:void|Task)\s+([A-Za-z_0-9]+)\s*\(')
    for base, dirs, files in os.walk(os.path.join(ROOT, 'tests')):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin')]
        for name in files:
            if name.endswith('.cs'):
                text = open(os.path.join(base, name), encoding='utf-8', errors='replace').read()
                names.update(pattern.findall(text))
    return names


def docs():
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in DOC_SKIP_DIRS]
        for name in files:
            if name.endswith('.md'):
                yield os.path.join(base, name)


# ---------------------------------------------------------------- the checks

LINK = re.compile(r'\[[^\]]*\]\(([^)]+)\)')
CITATION = re.compile(r'\(tests?:\s*([^)]+?)\)', re.S)
CODE_SPAN = re.compile(r'`([^`]+)`')


def check_links(problems):
    checked = 0
    for path in docs():
        base = os.path.dirname(path)
        for number, line in enumerate(open(path, encoding='utf-8', errors='replace'), 1):
            for target in LINK.findall(line):
                if target.startswith(('http://', 'https://', '#', 'mailto:')):
                    continue
                target = target.split('#')[0].strip()
                if not target:
                    continue
                checked += 1
                if not os.path.exists(os.path.normpath(os.path.join(base, target))):
                    problems.append('%s:%d: link to %s, which does not exist' % (rel(path), number, target))
    return checked


def check_citations(problems, tests):
    """`(test: Name)` in a document must name a test that exists.

    Scanned over the whole file rather than line by line: a citation listing three tests wraps, and the
    first version of this quietly counted only the ones that fitted on a single line — a checker that
    ignores what it cannot parse is worse than none, because it reports success.
    """
    checked = 0
    for path in docs():
        text = open(path, encoding='utf-8', errors='replace').read()
        for match in CITATION.finditer(text):
            number = text.count(chr(10), 0, match.start()) + 1
            names = match.group(1).replace(chr(10), ' ').split(',')
            for name in [n.strip().strip('`') for n in names]:
                # A placeholder in a sentence *about* the convention — `(test: …)` — is not a citation.
                # Only something shaped like a method name is.
                if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', name):
                    continue
                checked += 1
                if name not in tests:
                    problems.append('%s:%d: cites test %s, which is not in tests/' % (rel(path), number, name))
    return checked


BULLET = re.compile(r'\s*(?:[-*+]|\d+\.)\s')


def claim_lines(path):
    """The lines of a document that claim something exists, with their numbers.

    Only claims about what *exists* are checked. A design document names what it plans as freely as what
    it has — `r_instancing` in 06's API sketch is a decision, not a lie — so the checks apply to the
    README (which describes the engine as it is), to "As built" sections, and to lines that announce
    themselves as built. History is what was true when it was written, and is left alone.
    """
    if os.sep + 'history' + os.sep in path:
        return

    name = os.path.basename(path)
    # The README and the game-making guide both describe the engine as it is, not as planned, so every
    # line in them is a claim (the guide once told readers to type `rec_print`, which never existed, and
    # only "As built" lines were being checked).
    whole_file = name in ('README.md', 'MAKING_A_GAME.md')
    section = whole_file          # inside a section that describes what exists
    block = None                  # inside what does not: 'list' (a list under "Not yet:"), or 'bullet'

    for number, line in enumerate(open(path, encoding='utf-8', errors='replace'), 1):
        lower = line.lower()
        indented = line.startswith((' ', '\t'))
        bullet = BULLET.match(line) is not None

        if not whole_file and line.startswith('#'):
            # A heading decides its section: "As built" describes what exists, "Open questions" and
            # "Not built" do not. (An earlier version let any *line* mentioning "as built" re-arm the
            # section, so a cross-reference to §11's As built inside Open questions turned the check
            # back on and flagged a question as a false claim.)
            section = 'as built' in lower and not any(m in lower for m in ABSENCE)
            block = None

        # A list under "Not yet:" lasts while it is indented; a "- **Not built:** a, b and c" bullet
        # lasts over the lines it wraps onto, and ends at the next bullet (its wrapped lines were read
        # as claims, so 16's "a `TopicAsked` event", in a Not built bullet, was checked as one).
        if not indented or (block == 'bullet' and bullet):
            block = None
        absent = any(marker in lower for marker in ABSENCE)
        # `- **Not yet:**` ends with the emphasis, not the colon.
        heading_of_a_list = line.rstrip().rstrip('*').rstrip().endswith(':')
        if absent and heading_of_a_list:
            block = 'list'        # "**Not yet:**", and the indented list under it
        elif absent and bullet and block is None:
            block = 'bullet'

        if (section or 'as built' in lower) and block is None and not absent:
            yield number, line


def check_vocabulary(problems, code, literals, content_names):
    """A snake_case name that looks like a command or cvar has to exist in the registry, the code or content."""
    vocabulary = literals | content_names | code['command'] | code['cvar'] | code['record'] | code['part'] \
        | code.get('vocabulary', set())
    prefixes = {name.split('_')[0] for name in (code['command'] | code['cvar']) if '_' in name}

    checked = 0
    for path in docs():
        for number, line in claim_lines(path):
            for span in CODE_SPAN.findall(line):
                token = span.strip().split()[0] if span.strip() else ''
                token = token.rstrip('.,;:')
                if not re.fullmatch(r'[a-z][a-z0-9]*_[a-z0-9_]+', token):
                    continue
                if token.split('_')[0] not in prefixes:
                    continue                      # not shaped like this engine's vocabulary
                checked += 1
                if token not in vocabulary:
                    problems.append('%s:%d: `%s` reads as a command or cvar, and no such name is in the code'
                                    % (rel(path), number, token))
    return checked


# "the `Used` event", "game events `TriggerEntered`/`TriggerExited`", "`Damaged` and `Died` events": code
# spans joined to the word by nothing but other spans, separators and markdown emphasis. Not a colon:
# "**Room for events:** `Events` is a list" labels a paragraph, and names no event.
EVENT_BEFORE = re.compile(r'\bevents?\b[\s*_]*((?:`[^`]+`(?:[\s*_,/]|\band\b|\bor\b)*)+)', re.I)
EVENT_AFTER = re.compile(r'((?:`[^`]+`(?:[\s*_,/]|\band\b|\bor\b)*)+)\b(?:game\s+)?events?\b', re.I)
PASCAL = re.compile(r'[A-Z][A-Za-z0-9]*')
OUTPUT = re.compile(r'On[A-Z][A-Za-z0-9]*')
STATE_OUTPUT = re.compile(r'On(?:Enter|Exit)[A-Z][A-Za-z0-9]*')


def span_names(span):
    """`TriggerEntered(Trigger, Other)` -> TriggerEntered, `Added<T>` -> Added, `A`/`B` -> A, B."""
    names = []
    for part in span.split('/'):
        part = re.split(r'[<(]', part.strip(), 1)[0].strip()
        if PASCAL.fullmatch(part):
            names.append(part)
    return names


def check_outputs_and_events(problems, code, code_names, csharp_events):
    """An output or game event a document says exists has to be one (TODO #62).

    The snake_case check above cannot see these: doc 16 said interaction fires `OnUsed` (it is `OnUse`)
    and docs 04 and 10 named `TriggerEntered`/`TriggerExited` game events before any type declared them
    (issue #269 added them later), and the checker passed all three. An output nothing fires is silent, since wires
    are type-checked on their *inputs*, so the docs are the only place the wrong name shows.

    An `On…` name must be an output in the registry dump, a state machine's `OnEnter<state>` /
    `OnExit<state>` (fired by content, issue #280), or a name the code uses (`OnWorldCreated` is a method;
    a game may fire an output by a literal it never declares). A name written as an event must be a
    `[GameEvent]` struct in the dump, or a C# `event` the code declares (a widget's `Activated`).
    """
    outputs = {name.lower() for name in code['output']}
    checked = 0
    for path in docs():
        for number, line in claim_lines(path):
            for span in CODE_SPAN.findall(line):
                for name in span_names(span):
                    if not OUTPUT.fullmatch(name):
                        continue
                    checked += 1
                    if name.lower() not in outputs and name not in code_names and not STATE_OUTPUT.fullmatch(name):
                        problems.append('%s:%d: `%s` reads as an entity output, and nothing declares or fires it'
                                        % (rel(path), number, name))

            seen = set()
            for pattern in (EVENT_BEFORE, EVENT_AFTER):
                for match in pattern.finditer(line):
                    for span in CODE_SPAN.findall(match.group(1)):
                        for name in span_names(span):
                            if name in seen or OUTPUT.fullmatch(name):
                                continue          # an output in a sentence about events is checked above
                            seen.add(name)
                            checked += 1
                            if name not in code['event'] and name not in csharp_events:
                                problems.append('%s:%d: `%s` reads as a game event, and no [GameEvent] struct '
                                                'has that name' % (rel(path), number, name))
    return checked


# The numbers this script maintains are the ones on a line marked `<!-- counts -->`, and only those.
# Everything else that quotes a number — "F4's mixer has 16 headless tests", "step 1 finished with 15" —
# is about a feature or a day rather than about the engine now, and is nobody's to rewrite.
#
# A bare marker maintains the engine's own totals. A marker with arguments maintains facts about the
# repository itself, which is the other half of what a document quotes and the half that used to rot
# unseen: `<!-- counts: files games/Hello, code games/Hello -->` checks "four files" and "fifty lines of
# code" on that line. Both of those were wrong within a day of being written, in the paragraph
# introducing an example whose whole point was that it could not rot.
COUNT_MARKER = re.compile(r'<!--\s*counts(?::\s*([^>]*?))?\s*-->')

# Letters and digits only: `\S+` swallowed the markdown around a number, so `**90 console commands`
# captured `**90`, which reads as no number and went unchecked — in the one line the tool was written
# to maintain.
COUNTS = [
    ('command', re.compile(r'([A-Za-z0-9]+)\s+console commands')),
    ('record', re.compile(r'([A-Za-z0-9]+)\s+record types')),
    ('test', re.compile(r'([A-Za-z0-9]+)\s+headless tests')),
    ('files', re.compile(r'([A-Za-z0-9]+)\s+files')),
    ('code', re.compile(r'([A-Za-z0-9]+)\s+lines of code')),
]

# Documents count in words as often as in digits, and a rule that forces digits would make the prose
# worse to save the tool work. `--fix` writes back in whichever form it found.
WORDS = {
    'one': 1, 'two': 2, 'three': 3, 'four': 4, 'five': 5, 'six': 6, 'seven': 7, 'eight': 8, 'nine': 9,
    'ten': 10, 'eleven': 11, 'twelve': 12, 'thirteen': 13, 'fourteen': 14, 'fifteen': 15,
    'sixteen': 16, 'seventeen': 17, 'eighteen': 18, 'nineteen': 19, 'twenty': 20, 'thirty': 30,
    'forty': 40, 'fifty': 50, 'sixty': 60, 'seventy': 70, 'eighty': 80, 'ninety': 90, 'hundred': 100,
}
NUMBERS = {value: word for word, value in WORDS.items()}

# What each kind is called when the tool has to say it out loud.
NOUNS = {'command': 'console commands', 'record': 'record types', 'test': 'headless tests',
         'files': 'files', 'code': 'lines of code'}

# "about fifty" is not a claim that it is exactly fifty. Within a tenth is what the word means.
APPROXIMATELY = ('about', 'roughly', 'around', '~', 'nearly', 'some')


def read_number(written):
    """A number as a document writes it, or None when the word is not one."""
    if written.isdigit():
        return int(written)
    return WORDS.get(written.lower().rstrip('.,;:'))


def write_number(written, value):
    """The same value in the same form, or None when prose cannot hold it.

    A document that says "fifty" and means 49 cannot be repaired by writing "49" into the middle of the
    sentence — "about 49 lines" reads like a machine wrote it, and "two 49 lines" (from "two hundred")
    reads like nothing at all. Those are reported for a person to word, not rewritten.
    """
    if written.isdigit():
        return str(value)
    return NUMBERS.get(value)


def count_files(where):
    """Files a person would say are in a directory: not build output, not hidden."""
    total = 0
    for base, dirs, files in os.walk(os.path.join(ROOT, where)):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin', '.git')]
        total += len([f for f in files if not f.startswith('.')])
    return total


def count_code(where):
    """Lines of code: neither blank nor a comment, in the languages this repository writes."""
    total = 0
    for base, dirs, files in os.walk(os.path.join(ROOT, where)):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin', '.git')]
        for name in files:
            if not name.endswith(('.cs', '.py')):
                continue
            for line in open(os.path.join(base, name), encoding='utf-8', errors='replace'):
                stripped = line.strip()
                if stripped and not stripped.startswith(('//', '#')):
                    total += 1
    return total


def check_counts(problems, code, test_count, fix):
    """Numbers a document quotes about the engine, or about this repository, are computed not typed."""
    engine_facts = {'command': len(code['command']), 'record': len(code['record']), 'test': test_count}
    checked = 0

    for path in docs():
        # History is skipped for *vocabulary* — it records what was true when it was written — but a
        # count is opt-in by its marker, and the handoff is the page whose numbers go stale fastest.
        lines = open(path, encoding='utf-8', errors='replace').read().split('\n')
        changed = False

        for index, line in enumerate(lines):
            marker = COUNT_MARKER.search(line)
            if marker is None:
                continue

            # What this line's numbers are about: the engine by default, plus whatever the marker names.
            facts = dict(engine_facts)
            for argument in (marker.group(1) or '').split(','):
                argument = argument.strip()
                if not argument:
                    continue
                kind, _, where = argument.partition(' ')
                where = where.strip()
                if kind == 'files' and where:
                    facts['files'] = count_files(where)
                elif kind == 'code' and where:
                    facts['code'] = count_code(where)
                else:
                    problems.append('%s:%d: counts marker says "%s", which is not `files <path>` or `code <path>`'
                                    % (rel(path), index + 1, argument))

            edits = []
            for kind, pattern in COUNTS:
                if facts.get(kind) is None:
                    continue
                for match in pattern.finditer(line):
                    written = match.group(1)
                    value = read_number(written)
                    if value is None:
                        continue                      # "several files", which is not a claim to check
                    checked += 1

                    actual = facts[kind]
                    before = line[:match.start()].lower()
                    loose = any(word in before[-24:] for word in APPROXIMATELY)
                    close_enough = abs(value - actual) <= max(1, actual // 10)
                    if value == actual or (loose and close_enough):
                        continue

                    rewritten = write_number(written, actual) if fix else None
                    if rewritten is None:
                        problems.append('%s:%d: says %s %s, and there %s %d'
                                        % (rel(path), index + 1, written, NOUNS[kind],
                                           'are' if actual != 1 else 'is', actual))
                    else:
                        edits.append((match.start(), match.end(), match.group(0).replace(written, rewritten, 1)))

            # Right to left, so that an edit cannot move the offsets of the ones still to come.
            for start, end, replacement in sorted(edits, reverse=True):
                lines[index] = lines[index][:start] + replacement + lines[index][end:]
                changed = True
        if changed:
            open(path, 'w', encoding='utf-8', newline='').write('\n'.join(lines))
            print('fixed counts in %s' % rel(path))
    return checked


SELF_TEST = """# Self test

## As built (pretend)
- It has a `map_reloadx` command. (test: ThisTestDoesNotExist)
- See [the missing file](./nowhere-at-all.md).
- **1 console commands, 2 record types, 3 headless tests.** <!-- counts -->
- It has nine files and about two hundred lines of code. <!-- counts: files games/Hello, code games/Hello -->
- **4 console commands, 5 record types.** <!-- counts -->
- `selfdump_registered_only` is a command no source file spells, and the dump has it.
- Pressing use fires `OnUsed` on the target, and a dump-only output `OnSelftestFired`.
- Interaction sends an `Interacted` event; the game events `SelftestEntered`/`Used` and `Added<T>` too.
- A widget's `Activated` event is a C# event; a state machine fires `OnEnterSelftest`.
- **Not built:** a bullet that wraps
  onto `OnSelftestNeverFired` and a `SelftestPlanned` event, which are plans.

## Not yet
- `map_neverexisted`, which a document may name here without being wrong.
"""

# The registry the self test reads: what the host would write, with names in it that no source file
# contains. The counts and the vocabulary have to come from here, or the planted faults and the two
# planted truths come out the wrong way round.
SELF_TEST_REGISTRY = {
    'format': REGISTRY_FORMAT, 'config': 'SelfTest', 'game': 'selftest',
    'commands': [{'name': n} for n in ('map_load', 'selfdump_registered_only', 'selfdump_b', 'selfdump_c')],
    'cvars': [{'name': 'selfdump_volume'}],
    'recordTypes': [{'name': n} for n in ('a', 'b', 'c', 'd', 'e')],
    'entityInputs': [{'name': 'Open'}],
    'entityOutputs': [{'name': 'OnSelftestFired'}],
    'gameEvents': [{'name': 'Used'}, {'name': 'Added<T>'}],
}


def self_test(test_count=0):
    """Write a document with one of each fault, and check that each is found.

    A checker nobody checks is a checker that quietly stops working — which is the failure it exists to
    prevent, so it would be a poor joke to leave it open. It has already earned itself once: a block
    rewrite of `check_counts` deleted this function, and `--self-test` crashed on the next run rather
    than pretending all was well.

    The last bullet is the other half of the job: a name in a "Not yet" list must *not* be reported, or
    the tool trains you to ignore it.
    """
    path = os.path.join(ROOT, 'docs', '_selftest.md')
    registry = os.path.join(ROOT, 'docs', '_selftest.registry.json')
    open(path, 'w', encoding='utf-8', newline='').write(SELF_TEST)
    json.dump(SELF_TEST_REGISTRY, open(registry, 'w', encoding='utf-8'))
    try:
        problems = []
        tests = read_test_names()
        code = read_registry(registry)
        check_links(problems)
        check_citations(problems, tests)
        check_vocabulary(problems, code, read_code_literals(), read_content_names())
        check_outputs_and_events(problems, code, *read_code_names())
        check_counts(problems, code, test_count, fix=False)
    finally:
        os.remove(path)
        os.remove(registry)

    mine = [p for p in problems if '_selftest' in p]
    wanted = ['nowhere-at-all.md', 'ThisTestDoesNotExist', 'map_reloadx',
              '1 console commands', '2 record types', '3 headless tests',
              'nine files', 'hundred lines of code', '`OnUsed`', '`Interacted`', '`SelftestEntered`']
    missing = [w for w in wanted if not any(w in problem for problem in mine)]
    # And what must *not* be reported: a "Not yet" name, and the two lines that are true of the dump —
    # its counts, and a name only the dump has. Reported, they mean the names came from somewhere else.
    # Nor an output or event the dump has, a C# event, a state machine's output or a Not built plan.
    wrong = [p for p in mine if 'map_neverexisted' in p or '4 console commands' in p or '5 record types' in p
             or 'selfdump_registered_only' in p
             or any('`%s`' % n in p for n in ('OnSelftestFired', 'Used', 'Added', 'Activated', 'OnEnterSelftest',
                                                'OnSelftestNeverFired', 'SelftestPlanned'))]

    for problem in mine:
        print('  found: ' + problem)
    if missing:
        print('SELF TEST FAILED: these faults were not reported: ' + ', '.join(missing))
    if wrong:
        print('SELF TEST FAILED: reported something true (a "Not yet" name, or a fact from the registry dump): '
              + '; '.join(wrong))
    if not missing and not wrong:
        print('self test: all %d faults found; the "Not yet" list and the registry\'s own names and counts '
              'were left alone' % len(wanted))
    return 1 if (missing or wrong) else 0


def rel(path):
    return os.path.relpath(path, ROOT).replace(os.sep, '/')


def main():
    parser = argparse.ArgumentParser(description='Check the documentation against the code.')
    parser.add_argument('--tests', type=int, default=None,
                        help='how many tests `dotnet test` reported, so the quoted count can be checked')
    parser.add_argument('--fix', action='store_true', help='rewrite the counts rather than complaining')
    parser.add_argument('--self-test', action='store_true', help='check that the checker still catches things')
    parser.add_argument('--registry', default=DEFAULT_REGISTRY,
                        help='the registry dump the host wrote with -dump-registry (default user/registry.json)')
    args = parser.parse_args()

    if args.self_test:
        # The real count when it was given, so the tool's own output does not quote a number that has
        # gone stale — which would be a small joke at its own expense.
        return self_test(args.tests or 0)

    try:
        code = read_registry(args.registry)
    except (RegistryError, ValueError, KeyError) as error:
        print('check_docs: %s' % error)
        return 2
    literals = read_code_literals()
    content_names = read_content_names()
    tests = read_test_names()
    problems = []

    links = check_links(problems)
    citations = check_citations(problems, tests)
    vocabulary = check_vocabulary(problems, code, literals, content_names)
    io_names = check_outputs_and_events(problems, code, *read_code_names())
    counts = check_counts(problems, code, args.tests, args.fix)

    print('%d links, %d test citations, %d vocabulary uses, %d output and event names, %d counts checked '
          '(%d commands, %d cvars, %d records, %d inputs, %d outputs, %d events in %s)'
          % (links, citations, vocabulary, io_names, counts,
             len(code['command']), len(code['cvar']), len(code['record']), len(code['input']),
             len(code['output']), len(code['event']), code['source']))
    if args.tests is None:
        print('note: no --tests N, so the test count in the docs was not checked')

    for problem in problems:
        print('  ' + problem)
    print('%d problem(s)' % len(problems))
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
