"""texgen.py's own tests (issue #627): every kind tiles, a map that does not is caught, and a recipe
builds its maps and its sheet. Run by CI with the other tools' tests (unittest discover)."""
import json
import os
import tempfile
import unittest

import texgen


class TexgenTests(unittest.TestCase):
    def test_every_kind_tiles(self):
        size = 32
        for kind in texgen.KINDS:
            colour, normal, _ = texgen.draw({'name': kind, 'kind': kind, 'normal': True, 'seed': 3}, size)
            self.assertLess(texgen.seam_ratio(colour, size), 2.0, kind)
            self.assertLess(texgen.seam_ratio(normal, size), 2.0, kind + ' normal')

    def test_every_kind_tiles_finished(self):
        size = 32
        for kind in texgen.KINDS:
            colour, normal, _ = texgen.draw({'name': kind, 'kind': kind, 'normal': True, 'seed': 3, 'finish': True}, size)
            self.assertLess(texgen.seam_ratio(colour, size), 2.0, kind)
            self.assertLess(texgen.seam_ratio(normal, size), 2.0, kind + ' normal')

    def test_every_layer_of_the_finish_turns_on_and_off(self):
        base = {'name': 'b', 'kind': 'brick', 'seed': 3}
        plain = texgen.draw(base, 64)[0]
        self.assertEqual(plain, texgen.draw(dict(base, finish=False), 64)[0])
        self.assertEqual(plain, texgen.draw(dict(base, finish={k: False for k in texgen.FINISH}), 64)[0])
        everything = texgen.draw(dict(base, finish=True), 64)[0]
        self.assertNotEqual(plain, everything)
        for layer in texgen.FINISH:
            without = texgen.draw(dict(base, finish={layer: False}), 64)[0]
            self.assertNotEqual(everything, without, layer)

    def test_cracks_and_stains_go_where_they_belong_unless_a_recipe_says(self):
        self.assertGreater(texgen.finish_of({'kind': 'brick', 'finish': True})['cracks'], 0)
        self.assertEqual(0, texgen.finish_of({'kind': 'grass', 'finish': True})['cracks'])
        self.assertEqual(0, texgen.finish_of({'kind': 'sand', 'finish': True})['stains'])
        self.assertEqual(0.5, texgen.finish_of({'kind': 'grass', 'finish': {'cracks': 0.5}})['cracks'])
        self.assertEqual(texgen.FINISH['cracks'], texgen.finish_of({'kind': 'grass', 'finish': {'cracks': True}})['cracks'])

    def test_the_finish_is_fixed_by_the_seed(self):
        one = {'name': 'p', 'kind': 'planks', 'seed': 5, 'finish': True}
        self.assertEqual(texgen.draw(one, 32)[0], texgen.draw(dict(one), 32)[0])
        self.assertNotEqual(texgen.draw(one, 32)[0], texgen.draw(dict(one, seed=6), 32)[0])

    def test_a_map_that_does_not_tile_is_caught(self):
        size = 32
        ramp = [(int(255 * (i % size) / size),) * 3 + (255,) for i in range(size * size)]
        self.assertGreater(texgen.seam_ratio(ramp, size), 10)

    def test_a_recipe_writes_its_maps_and_sheet(self):
        recipe = {'size': 16, 'materials': [{'name': 'a', 'kind': 'stone', 'normal': True, 'spec': True},
                                            {'name': 'b', 'kind': 'cloud', 'variants': 2}],
                  'sheet': {'name': 'sheet', 'columns': 2}}
        with tempfile.TemporaryDirectory() as out:
            texgen.build(recipe, out)
            for f in ('a.png', 'a_normal.png', 'a_spec.png', 'b_1.png', 'b_2.png', 'sheet.png'):
                self.assertTrue(os.path.getsize(os.path.join(out, f)) > 0, f)
            with open(os.path.join(out, 'sheet.json'), encoding='utf-8') as f:
                sheet = json.load(f)
            self.assertEqual((32, 32), (sheet['width'], sheet['height']))
            self.assertEqual({'x': 0, 'y': 16, 'w': 16, 'h': 16}, sheet['tiles']['b_2'])

    def test_bad_input_says_what_is_wrong(self):
        with self.assertRaisesRegex(ValueError, "unknown kind 'lava'"):
            texgen.draw({'name': 'x', 'kind': 'lava'}, 16)
        with self.assertRaisesRegex(ValueError, "unknown finish"):
            texgen.draw({'name': 'x', 'kind': 'sand', 'finish': {'glitter': 1}}, 16)
        with tempfile.TemporaryDirectory() as out, self.assertRaisesRegex(ValueError, 'power of two'):
            texgen.build({'size': 100, 'materials': [{'name': 'x', 'kind': 'sand'}]}, out)


if __name__ == '__main__':
    unittest.main()
