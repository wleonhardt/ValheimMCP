"""Capture a piece of the live world as a unit-test fixture.

  HUBNER_URL=... HUBNER_LIVE_WORLDS=Test python3 python/live/capture_scene.py NAME X Z [RADIUS] [NOTE...]

Calls scene_capture on the endpoint, fetches the file with scene_get and saves it to tests/ValheimMCP.Tests/fixtures/scenes/NAME.scene.json. From then on `dotnet test`
runs the support model on it and compares it with the support values the game recorded (SceneFixtureTests). A scene where our model and the game disagree is the bug report:
commit it with the fix."""
import json, os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import hubner

DEST = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'tests', 'ValheimMCP.Tests', 'fixtures', 'scenes')


def capture(client, name, x, z, radius=24, note='', dest=DEST):
    info = client.scene_capture(name, x, z, radius, note)
    text = client.scene_get(name)
    json.loads(text)                                                # refuse to save something that is not JSON
    os.makedirs(dest, exist_ok=True)
    path = os.path.join(dest, f'{name}.scene.json')
    with open(path, 'w') as f: f.write(text)
    return info, path


if __name__ == '__main__':
    if len(sys.argv) < 4: sys.exit(__doc__)
    c = hubner.Client()
    hubner.live_guard(c)
    info, path = capture(c, sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4]) if len(sys.argv) > 4 else 24, ' '.join(sys.argv[5:]))
    print(json.dumps(info)); print('saved', os.path.relpath(path))
