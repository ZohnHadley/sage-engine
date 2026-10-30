# -*- coding: utf-8 -*-
"""The Showcase's scene (content/data/scene.json) and its signs' lines (content/data/signs.json).

The placements are long lists (plinths, standing stones, 48 crystals, each zone's sign), so they are written
here rather than by hand; change this and run it, rather than editing the two files.

Run:  python games/Showcase/tools/make_scene.py
"""
import json
import os
import textwrap

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'content', 'data', 'scene.json')


def f(v):
    return ('%.2f' % v).rstrip('0').rstrip('.') if isinstance(v, float) else str(v)


def at(x, y, z):
    return '[%s, %s, %s]' % (f(float(x)), f(float(y)), f(float(z)))


def brush(x, y, z, size, material, shape=None, yaw=None, sides=None, name=None, prefab='sage:brush'):
    b = {'size': size, 'material': 'showcase:' + material}
    if sides:
        b['sides'] = sides
    s = '{ "prefab": "%s", "at": %s' % (prefab, at(x, y, z))
    if yaw is not None:
        s += ', "yaw": %s' % f(float(yaw))
    if name:
        s += ', "name": "%s"' % name
    s += ', "overrides": { "parts": { "brush": %s } } }' % json.dumps(b)
    return s


def place(prefab, x, y, z, name=None, yaw=None, pitch=None, scale=None, extra=''):
    s = '{ "prefab": "%s", "at": %s' % (prefab, at(x, y, z))
    if yaw is not None:
        s += ', "yaw": %s' % f(float(yaw))
    if pitch is not None:
        s += ', "pitch": %s' % f(float(pitch))
    if scale is not None:
        s += ', "scale": [%s, %s, %s]' % (f(scale), f(scale), f(scale))
    if name:
        s += ', "name": "%s"' % name
    s += extra + ' }'
    return s


SIGNS = []   # (zone, speaker, text)


def zone(name, x, y, z, size, then, end=None):
    """A sign: a player-only trigger whose relay does `then` as the player walks in (and `end` as they leave).
    A `msg` in `then` becomes the zone's bark line, said as a subtitle."""
    acts = []
    for a in then:
        if 'message' in a:
            texts = a['message']['text'].split(' | ')
            SIGNS.append((name, a['message']['speaker'], texts))
            acts += [{"bark": "sign%d" % (i + 1)} for i in range(len(texts))]
        else:
            acts.append(a)
    outs = [{"output": "OnStartTouch", "target": "!self", "input": "Trigger"}]
    if end:
        outs.append({"output": "OnEndTouch", "target": end, "input": "Trigger"})
    parts = {"body": {"size": size}, "logic_relay": {"then": acts}}
    if any('message' in a for a in then):
        parts["barks"] = "sign_" + name
    return ('{ "prefab": "zone", "at": %s, "name": "%s",\n        "overrides": { "parts": %s },\n        "outputs": %s }'
            % (at(x, y, z), name, json.dumps(parts), json.dumps(outs)))


def msg(text, speaker):
    return {"message": {"text": text, "speaker": speaker}}


def cmd(line):
    return {"command": line}


def signpost(x, z, yaw):
    return [brush(x, 0, z, [0.16, 2.3, 0.16], 'planks'),
            brush(x, 1.45, z, [1.5, 0.8, 0.1], 'planks', yaw=yaw)]


P = []   # (comment, [lines])

P.append(('The hub (10, -38): plaza.map, flattened. The player starts on its south side looking north at the gallery; '
          'the start-up relay turns the post chain on and passes two weeks so the moon is full (see README).', [
    place('sage:logic_auto', 10, 0, -38, name='startup',
          extra=', "outputs": [ { "output": "OnMapSpawn", "target": "startup_relay", "input": "Trigger" },\n'
                '                    { "output": "OnMapSpawn", "target": "moving lamp", "input": "TweenTo" },\n'
                '                    { "output": "OnMapSpawn", "target": "scorch_relay", "input": "Trigger", "delay": 0.5 } ]'),
    '{ "prefab": "relay", "at": [10, 0, -38], "name": "startup_relay",\n        "overrides": { "parts": { "logic_relay": { "then": %s } } } }' % json.dumps(
        [cmd("sv_cheats 1"), cmd("r_post 1"), cmd("r_bloom 1"), cmd("r_hdr 1"), cmd("r_aa fxaa"), cmd("r_shadows 1"), {"pass_time": 336}]),
    zone('hub', 10, 1.5, -38, [16, 5, 16], [msg("N: materials. E: lights hall. W: fog field. NE knoll: the sky. | S: yard, campfire, weather court and pool, then the pond. | Console (~): time_set 21, sky_set showcase:painted, weather storm 6, r_stats", "Showcase")]),
    place('torch_lamp', 2.6, 1.5, -45.4, name='plaza lamp', scale=0.18),
    place('torch_lamp', 17.4, 1.5, -45.4, name='plaza lamp', scale=0.18),
    place('torch_lamp', 2.6, 1.5, -30.6, name='plaza lamp', scale=0.18),
    place('torch_lamp', 17.4, 1.5, -30.6, name='plaza lamp', scale=0.18),
]))

gallery = [('show_brick', 'normal map'), ('show_metal', 'specular map'), ('show_rune', 'emissive map'),
           ('show_chrome', 'environment map'), ('show_vertex', 'vertex colours'), ('show_glow', 'emissive'),
           ('show_moss', 'moss')]
g = [zone('gallery', 11.5, 1.5, -50, [26, 5, 10], [msg("L to R: normal+spec maps, specular map, emissive map, | environment map (chrome), vertex colours, emissive (blooms), moss normals. | The wall is a brush: no tangents, so its normal map does nothing yet.", "Materials")])]
g += signpost(1.5, -47.5, 20)
for i, (prefab, name) in enumerate(gallery):
    x = 2.5 + 3 * i
    g.append(brush(x, -0.2, -53, [1.3, 1.2, 1.3], 'stone', sides=24, prefab='sage:cylinder'))
    g.append(place(prefab, x, 1.0, -53, name=name, yaw=180 if prefab == 'show_vertex' else None))
for x in (3.5, 11.5, 19.5):
    g.append(brush(x, -1.5, -55.5, [8, 5, 0.5], 'brick'))
P.append(('The materials gallery (x 2.5..20.5, z -53): a plinth a surface feature, a brick wall behind.', g))

P.append(('The lights hall (52, -38): hall.map, lightmapped, its door to the west. Inside, a shadowed spot over the '
          'north crate, a shadowed cold lamp that the start-up sends east and back across the hall, a torch on the '
          'east wall, another by the door, and a pulsing pink spot from the north wall.', [
    zone('hall', 52, 1.5, -38, [12, 5, 10], [msg("A spot and a moving point lamp, each with a shadow map (r_shadow_lamps). | A flickering torch and a pulsing pink spot. | The hall is lightmapped: only its lamps light the inside.", "Lights")]),
    *signpost(44, -41.5, 90),
    place('spot_lamp', 52.6, 4.5, -41, name='hall spot', pitch=-90, scale=0.12),
    place('moving_lamp', 47.5, 2.1, -35.5, name='moving lamp', scale=0.16),
    place('torch_lamp', 57.6, 2.5, -38, name='hall torch', scale=0.14),
    place('accent_lamp', 48, 3.8, -42.6, name='hall accent', yaw=180, pitch=-35),
    place('torch_lamp', 46.6, 2.6, -34.5, name='hall fill', scale=0.12),
]))

fog = [
    zone('fog_linear', -36, 2, -41, [28, 8, 20], [cmd("sky_set showcase:linear"), cmd("weather fog 3"), msg("East half: linear fog, from its start to its end. | Walk west for Exp2 fog. Leaving the field puts the day sky back.", "Fog")]),
    zone('fog_exp2', -64, 2, -41, [28, 8, 20], [cmd("sky_set showcase:day"), cmd("weather fog 3"), msg("West half: Exp2 fog, soft near and closing in fast. | The post chain's depth haze (r_post) thickens the distance too.", "Fog")]),
    zone('fog_field', -50, 2, -41, [58, 10, 22], [], end='fog_out'),
    '{ "prefab": "relay", "at": [-50, 0, -41], "name": "fog_out",\n        "overrides": { "parts": { "logic_relay": { "then": %s } } } }' % json.dumps(
        [cmd("sky_set showcase:day"), cmd("weather clear 4")]),
    *signpost(-20, -38, -90),
]
for k in range(11):
    x = -26 - 6 * k
    fog.append(brush(x, -1, -36 if k % 2 == 0 else -46, [0.9, 5.5, 0.9], 'stone', sides=12, prefab='sage:cylinder'))
P.append(('The fog field (x -22..-78, z -41): standing stones going down the slope into the fog. Walking in sets the '
          'linear-fog sky and the `fog` weather; the west half the Exp2 day sky; leaving puts them back.', fog))

yard = [zone('yard', 22, 1.5, -16, [16, 5, 12], [msg("Skinned mannequins: a walk/run blend space and an upper-body aim layer. | 48 crystals, one mesh and material: r_instancing 1 draws them in one call.", "Yard")]),
        *signpost(16, -23, 0),
        place('walker', 15, 0.3, -12, name='walker', yaw=-90,
              extra=', "outputs": [ { "output": "OnTweenDone", "target": "!self", "input": "Arrived" } ]'),
        place('drill', 16.5, 0.3, -18.5, name='drill', yaw=0)]
for i in range(8):
    for j in range(6):
        yard.append(place('crystal', 21.5 + i, 0.3, -21 + j, name='crystal', yaw=(i * 37 + j * 23) % 60))
P.append(('The yard (22, -16): yard.map, flattened. A mannequin walking a lane, one drilling, and the crystal field.', yard))

P.append(('The campfire (-12, -14): flames, smoke and embers (soft particles), a flickering lamp with shadows, and '
          'scorch decals the start-up lays on the stones round it.', [
    zone('campfire', -12, 1.5, -14, [10, 5, 10], [msg("Soft particles (flames, smoke), embers, a flickering lamp with shadows. | Scorch decals laid at start; fx_decal showcase:scorch adds one. | Best at night: time_set 21.", "Campfire")]),
    *signpost(-8, -18, 45),
    brush(-12, -0.1, -14, [5, 0.3, 5], 'stone', sides=24, prefab='sage:cylinder'),
    brush(-12, 0.2, -14, [1.4, 0.2, 0.2], 'planks', yaw=30),
    brush(-12, 0.2, -14, [1.4, 0.2, 0.2], 'planks', yaw=-30),
    place('fire', -12, 0.3, -14, name='fire'),
    place('smoke', -12, 1.0, -14, name='smoke'),
    place('embers', -12, 0.5, -14, name='embers'),
    place('fire_light', -12, 1.4, -14, name='fire light'),
    place('marker', -12, 0.8, -14, name='scorch_1'),
    place('marker', -10.4, 0.8, -12.9, name='scorch_2'),
    place('marker', -13.6, 0.8, -15.3, name='scorch_3'),
    place('marker', -10.9, 0.8, -15.7, name='scorch_4'),
    '{ "prefab": "relay", "at": [-12, 0, -14], "name": "scorch_relay",\n        "overrides": { "parts": { "logic_relay": { "then": %s } } } }' % json.dumps(
        [{"cue": {"cue": "scorch", "at": "scorch_1"}}, {"cue": {"cue": "scorch_small", "at": "scorch_2"}},
         {"cue": {"cue": "scorch_small", "at": "scorch_3"}}, {"cue": {"cue": "scorch_small", "at": "scorch_4"}}]),
]))

P.append(('The weather court (40, 5): court.map, lightmapped, with a roofed shelter in its north-east corner. Walking '
          'in rolls the rain in; the paving darkens and puddles gather; under the shelter the drops stop. The pool '
          'stands in its south-west quarter.', [
    zone('court', 40, 2, 5, [14, 6, 14], [cmd("weather rain 4"), msg("Rain rolls in. Open ground wets in about twelve seconds, then puddles gather. | Under the shelter the drops stop. weather storm 4 for lightning. | Leaving the court clears it.", "Weather")], end='court_out'),
    '{ "prefab": "relay", "at": [40, 0, 5], "name": "court_out",\n        "overrides": { "parts": { "logic_relay": { "then": %s } } } }' % json.dumps(
        [cmd("weather clear 8")]),
    *signpost(32.5, -1, 60),
    brush(37, 0.3, 6.15, [6, 0.7, 0.3], 'stone'),
    brush(37, 0.3, 9.85, [6, 0.7, 0.3], 'stone'),
    brush(34.15, 0.3, 8, [0.3, 0.7, 3.4], 'stone'),
    brush(39.85, 0.3, 8, [0.3, 0.7, 3.4], 'stone'),
    brush(37, 0.3, 8, [5.4, 0.04, 3.4], 'pool_tile'),
    place('pool', 37, 0.55, 8, name='pool'),
    zone('pool_sign', 37, 1.5, 8, [8, 4, 6], [msg("A water surface with no shore fade over a tiled floor: | ripples, the sky's reflection, the tiles through it.", "Pool")]),
]))

P.append(('The pond (17.5, 62): the basin south of the court holds water to 10.7 m under the origin; the box is '
          'placed from the origin, not the ground, and covers the basin with room to spare.', [
    '{ "prefab": "pond", "at": [17.5, -12.7, 62], "relativeTo": "Origin", "name": "pond" }',
    zone('pond_sign', 18, 2, 46, [30, 8, 14], [msg("Ripples, sky and shore reflections, the bottom through the water, a soft shore. | Wade in and dive (crouch) for the view under the surface.", "Pond")]),
    *signpost(20, 33, 0),
]))

P.append(('The terrain terrace (-30, 10): deck.map, a plank deck on the hillside, the ground under it laid flat. '
          'It looks south-west at the ridge, where rock is painted over the grass by slope.', [
    zone('deck', -30, 1.5, 10, [8, 5, 6], [msg("Grass; sand below 9.5 m by height; rock on slopes over 14 degrees; a detail texture. | This deck is a .map on the ground (onTerrain) that flattens the ground under it.", "Terrain")]),
    *signpost(-26, 6, -45),
]))

P.append(('The knoll (60, -80): the highest ground near the hub, a round platform to watch the sky from.', [
    zone('knoll', 60, 1.5, -80, [10, 5, 10], [msg("A procedural sky: clouds, a moon, stars that turn (time_set 22, time_scale 600). | sky_set showcase:painted: painted day and night panoramas. | sky_set showcase:alien; sky_set showcase:day to go back.", "Sky")]),
    *signpost(58, -73, 200),
    brush(60, -0.4, -80, [7, 0.8, 7], 'stone', sides=32, prefab='sage:cylinder'),
    brush(60, 0.4, -81.5, [2.2, 0.45, 0.5], 'planks'),
]))

lines = ['[', '  // The Showcase\'s scene: where everything goes. Coordinates are metres from the origin (absolute 512, 0, 512),',
         '  // x east and z south, and y is height above the ground unless a placement says `"relativeTo": "Origin"`.',
         '  // The brush levels are map records (world.json); everything else is placed here. Written by',
         '  // tools/make_scene.py: change that and run it rather than editing this file.',
         '  {', '    "type": "scene", "id": "showcase", "streamed": true, "terrain": "hills",',
         '    "origin": [512, 0, 512], "relativeTo": "Ground",',
         '    "environment": { "sky": "day", "hour": 16, "weather": "clear" },',
         '    "maps": ["plaza", "hall", "court", "deck", "yard"],',
         '    "player": { "prefab": "player", "at": [11.5, 0.3, -39], "yaw": 0 },',
         '    "place": [']
items = []
for comment, ls in P:
    block = [''] + ['      // ' + c for c in textwrap.wrap(comment, 108)]
    for l in ls:
        block.append('      ' + l + ',')
    items.append(block)
flat = [x for b in items for x in b]
flat[-1] = flat[-1].rstrip(',')
lines += flat
lines += ['    ]', '  }', ']']
open(OUT, 'w', newline='\n').write('\n'.join(lines) + '\n')
print('ok', len(lines))

sl = ['[', "  // What each zone's sign says (scene.json): bark lines on the occasions `sign1`..`sign3`, said as subtitles when the",
      "  // player walks into the zone (its relay's `bark` action). Written by tools/make_scene.py."]
for i, (zname, spk, text) in enumerate(SIGNS):
    sl.append('  { "type": "barks", "id": "sign_%s", "speaker": "%s", "range": 0, "cooldown": 0,' % (zname, spk))
    body = ',\n               '.join('{ "on": "sign%d", "text": %s }' % (k + 1, json.dumps(t)) for k, t in enumerate(text))
    sl.append('    "lines": [ %s ] }%s' % (body, ',' if i < len(SIGNS) - 1 else ''))
sl.append(']')
open(os.path.join(os.path.dirname(OUT), 'signs.json'), 'w', newline='\n').write('\n'.join(sl) + '\n')
