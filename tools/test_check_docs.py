# -*- coding: utf-8 -*-
"""Unit tests for tools/check_docs.py (issue #298).

`--self-test` proves the checker finds one planted fault of each kind in the real repository; these
prove its parts on their own, against documents, sources and registry dumps made up in a temporary
folder, so a rule can be changed and its edges seen. Run from the repository root:

    python3 -m unittest discover -s tools -p 'test_*.py'
"""
import json
import os
import shutil
import sys
import tempfile
import textwrap
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_docs  # noqa: E402  (the path above is what finds it)


class Sandbox(unittest.TestCase):
    """A made-up repository: check_docs's ROOT points at it while a test runs."""

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix='check_docs_test_')
        self._root = check_docs.ROOT
        check_docs.ROOT = self.root

    def tearDown(self):
        check_docs.ROOT = self._root
        shutil.rmtree(self.root, ignore_errors=True)

    def write(self, path, text):
        full = os.path.join(self.root, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, 'w', encoding='utf-8', newline='') as f:
            f.write(textwrap.dedent(text))
        return full

    def registry(self, **sections):
        dump = {'format': check_docs.REGISTRY_FORMAT, 'config': 'Test', 'game': 'test',
                'commands': [], 'cvars': [], 'recordTypes': [], 'entityInputs': [], 'entityOutputs': [],
                'gameEvents': []}
        dump.update(sections)
        path = os.path.join(self.root, 'registry.json')
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(dump, f)
        return check_docs.read_registry(path)

    def problems(self, code):
        found = []
        check_docs.check_outputs_and_events(found, code, *check_docs.read_code_names())
        return found


class OutputsAndEvents(Sandbox):
    """TODO #62: an output or game event name a document claims has to exist."""

    def setUp(self):
        super().setUp()
        self.write('src/Engine.cs', '''
            public static class Doors
            {
                public const string OnOpened = "OnOpened";         // fired by name, never declared
                public void OnWorldCreated(World world) { }        // a method, not an output
                public event Action<Widget>? Activated;            // a C# event
                // OnInComment is only in a comment, so it does not exist
                string Message = "fires OnInString";               // nor is a word inside a string a name
                string Url = "http://example//OnAfterSlashes";     // a string, not a comment
                char Quote = '"'; string After = "OnAfterQuote";
            }
        ''')
        self.code = self.registry(entityOutputs=[{'name': 'OnUse'}, {'name': 'OnStartTouch'}],
                                  gameEvents=[{'name': 'Used'}, {'name': 'TriggerEntered'}, {'name': 'Added<T>'}])

    def doc(self, *lines):
        self.write('docs/design/99-test.md', '## As built (test)\n' + '\n'.join(lines) + '\n')

    def test_a_wrong_output_name_fails(self):
        # Doc 16 said interaction fires `OnUsed`; it is `OnUse` (the case TODO #62 was filed for).
        self.doc('- Pressing use fires `OnUsed` on what you look at.')
        problems = self.problems(self.code)
        self.assertEqual(1, len(problems), problems)
        self.assertIn('docs/design/99-test.md:2: `OnUsed` reads as an entity output', problems[0])

    def test_declared_fired_and_method_names_pass(self):
        self.doc('- It fires `OnUse`, `onstarttouch` (any case), `OnOpened` (fired by literal) and a '
                 'state machine\'s `OnEnterAlert`; a module overrides `OnWorldCreated`.')
        self.assertEqual([], self.problems(self.code))

    def test_names_only_in_comments_strings_or_after_a_url_do_not_exist(self):
        self.doc('- It fires `OnInComment`, `OnInString` and `OnAfterSlashes`.')
        problems = self.problems(self.code)
        self.assertEqual(3, len(problems), problems)

    def test_a_literal_after_a_char_quote_is_still_read(self):
        self.doc('- It fires `OnAfterQuote`.')
        self.assertEqual([], self.problems(self.code))

    def test_a_wrong_event_name_fails(self):
        # Docs 04 and 10 named `TriggerEntered`/`TriggerExited` game events when no type declared them.
        self.doc('- Physics sends the game events `TriggerEntered`/`TriggerExited` (test: X).',
                 '- Interaction sends an `Interacted` event.')
        problems = self.problems(self.code)
        self.assertEqual(['docs/design/99-test.md:2: `TriggerExited` reads as a game event, and no [GameEvent] '
                          'struct has that name',
                          'docs/design/99-test.md:3: `Interacted` reads as a game event, and no [GameEvent] '
                          'struct has that name'], problems)

    def test_events_by_their_struct_a_generic_stem_a_call_shape_or_a_csharp_event_pass(self):
        self.doc('- `Used(User, Target)` and `Added<Health>` events; a widget\'s `Activated` event.',
                 '- **Room for events:** `Events` is a list, which is a label and not an event.',
                 '- `Interacted` is a word in a sentence that names no event at all.')
        self.assertEqual([], self.problems(self.code))

    def test_plans_and_history_are_not_claims(self):
        self.doc('- **Not built:** a `TopicAsked` event, which wraps',
                 '  onto `OnNeverFired`; both are plans.',
                 '- But this bullet is a claim again: `OnAlsoNeverFired`.')
        self.write('docs/history/old.md', '## As built\n- It fired `OnUsed`.\n')
        problems = self.problems(self.code)
        self.assertEqual(1, len(problems), problems)
        self.assertIn('`OnAlsoNeverFired`', problems[0])

    def test_outside_as_built_a_design_doc_is_not_checked(self):
        self.write('docs/design/99-test.md', '## Design\n- It will fire `OnSomeday`.\n')
        self.assertEqual([], self.problems(self.code))

    def test_the_readme_is_all_claims(self):
        self.write('README.md', '# Sage\nIt fires `OnSomeday`.\n')
        self.assertEqual(1, len(self.problems(self.code)))


class ClaimLines(Sandbox):

    def lines(self, text):
        path = self.write('docs/x.md', text)
        return [number for number, _ in check_docs.claim_lines(path)]

    def test_as_built_sections_and_not_yet_lists(self):
        # Line 1 too: a heading that says "as built" is a line that says so.
        self.assertEqual([1, 2, 7], self.lines('''\
            ## As built
            - claim
            - **Not yet:**
              - plan
              - plan
            - not yet, this one either
            - claim
            ## Open questions
            - question
        '''))

    def test_a_not_built_bullet_covers_its_wrapped_lines_and_ends_at_the_next_bullet(self):
        self.assertEqual([1, 4, 5, 6], self.lines('''\
            ## As built
            - **Not built:** a plan that
              wraps onto a second line
              - but a nested bullet is a claim of its own
                and so is the line it wraps onto
            - claim
        '''))

    def test_a_line_that_says_as_built_is_a_claim_anywhere(self):
        self.assertEqual([2], self.lines('## Design\nThis is as built.\nThis is not.\n'))


class Registry(Sandbox):

    def test_names_come_from_the_dump(self):
        code = self.registry(commands=[{'name': 'map'}], entityOutputs=[{'name': 'OnUse'}],
                             gameEvents=[{'name': 'Removed<T>'}, {'name': 'Died'}])
        self.assertEqual({'map'}, code['command'])
        self.assertEqual({'OnUse'}, code['output'])
        self.assertEqual({'Removed', 'Died'}, code['event'])

    def test_a_dump_without_game_events_is_an_error(self):
        path = os.path.join(self.root, 'old.json')
        with open(path, 'w', encoding='utf-8') as f:
            json.dump({'format': check_docs.REGISTRY_FORMAT}, f)
        with self.assertRaisesRegex(check_docs.RegistryError, 'gameEvents'):
            check_docs.read_registry(path)

    def test_a_missing_dump_or_another_format_is_an_error(self):
        with self.assertRaisesRegex(check_docs.RegistryError, 'no registry dump'):
            check_docs.read_registry(os.path.join(self.root, 'nowhere.json'))
        path = os.path.join(self.root, 'future.json')
        with open(path, 'w', encoding='utf-8') as f:
            json.dump({'format': 99, 'gameEvents': []}, f)
        with self.assertRaisesRegex(check_docs.RegistryError, 'format 99'):
            check_docs.read_registry(path)


class Parsing(unittest.TestCase):

    def test_span_names(self):
        self.assertEqual(['TriggerEntered'], check_docs.span_names('TriggerEntered(Trigger, Other)'))
        self.assertEqual(['Added'], check_docs.span_names('Added<T>'))
        self.assertEqual(['A', 'B'], check_docs.span_names('A/B'))
        self.assertEqual([], check_docs.span_names('world.Events.Reader<Collided>(this)'))
        self.assertEqual([], check_docs.span_names('snake_case'))

    def test_numbers_in_words_and_digits(self):
        self.assertEqual(12, check_docs.read_number('twelve'))
        self.assertEqual(12, check_docs.read_number('12'))
        self.assertIsNone(check_docs.read_number('several'))
        self.assertEqual('fifty', check_docs.write_number('forty', 50))
        self.assertIsNone(check_docs.write_number('forty', 49))   # prose cannot hold it; a person rewords
        self.assertEqual('49', check_docs.write_number('40', 49))


class Counts(Sandbox):

    def test_a_wrong_count_is_reported_and_fix_rewrites_it(self):
        code = self.registry(commands=[{'name': 'a_b'}, {'name': 'c_d'}], recordTypes=[{'name': 'x'}])
        path = self.write('docs/x.md', '- **3 console commands, one record types.** <!-- counts -->\n')
        problems = []
        check_docs.check_counts(problems, code, None, fix=False)
        self.assertEqual(['docs/x.md:1: says 3 console commands, and there are 2'], problems)

        check_docs.check_counts([], code, None, fix=True)
        with open(path, encoding='utf-8') as f:
            self.assertEqual('- **2 console commands, one record types.** <!-- counts -->\n', f.read())

    def test_about_is_within_a_tenth(self):
        code = self.registry(commands=[{'name': 'c%d_x' % i} for i in range(48)])
        self.write('docs/x.md', 'about 50 console commands <!-- counts -->\n')
        problems = []
        check_docs.check_counts(problems, code, None, fix=False)
        self.assertEqual([], problems)


if __name__ == '__main__':
    unittest.main()
