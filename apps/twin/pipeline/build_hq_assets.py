"""Build the detailed scenery and crane models with Blender and copy them into Unity Resources.

    python3 pipeline/build_hq_assets.py [--blender /Applications/Blender.app/Contents/MacOS/Blender]

Writes the layout Blender needs (facility halls from generate_yard.FACILITIES, goliath span/height from the
placement dock), runs pipeline/blender/build_assets.py headless, then copies data/props_hq/<model>.obj to
unity/.../Resources/ShipyardTwin/Props/<model>.obj.bytes. Same model names as generate_scenery.py, so the
detailed models replace the simple ones; run generate_scenery.py first for scenery.json.
"""

import argparse
import json
import shutil
import subprocess
from pathlib import Path

from generate_yard import FACILITIES
from paths import DATA, ROOT, UNITY_RESOURCES

# Must match Unity: YardLayout.WallThickness (6) and GantryCraneView rail offset (8) / girder height (92).
WALL, RAIL_OFFSET, GIRDER_HEIGHT = 6.0, 8.0, 92.0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--blender', default='/Applications/Blender.app/Contents/MacOS/Blender')
    parser.add_argument('--dock', default='DT-DOCK', help='골리앗 크레인이 놓이는 도크 (폭으로 경간 결정)')
    args = parser.parse_args()
    dock = next(f for f in FACILITIES if f[0] == args.dock)
    pit_half_width = dock[6] / 2 - WALL
    layout = {
        'goliath': {'span': 2 * (pit_half_width + WALL + RAIL_OFFSET), 'height': GIRDER_HEIGHT},
        'halls': [{'id': f[0], 'kind': f[1], 'sx': f[5], 'sy': f[6], 'h': f[7] or 20} for f in FACILITIES if f[1] in ('작업장', '창고')],
    }
    out = DATA / 'props_hq'
    layout_path = DATA / 'blender_layout.json'
    layout_path.parent.mkdir(parents=True, exist_ok=True)
    layout_path.write_text(json.dumps(layout, ensure_ascii=False, indent=1), encoding='utf-8')
    script = ROOT / 'pipeline' / 'blender' / 'build_assets.py'
    result = subprocess.run([args.blender, '-b', '--factory-startup', '-P', str(script), '--', str(layout_path), str(out)],
                            capture_output=True, text=True)
    exported = [line for line in result.stdout.splitlines() if line.startswith('exported ')]
    if result.returncode != 0 or not exported:
        print(result.stdout[-3000:], result.stderr[-3000:])
        raise SystemExit(f'Blender 실패 (exit {result.returncode})')
    target = UNITY_RESOURCES / 'Props'
    target.mkdir(parents=True, exist_ok=True)
    for obj in sorted(out.glob('*.obj')):
        shutil.copyfile(obj, target / (obj.name + '.bytes'))
    size = sum(f.stat().st_size for f in out.glob('*.obj'))
    print('\n'.join(exported))
    print(f'{out}: 모델 {len(exported)}개, {size / 1e6:.1f}MB → {target}')


if __name__ == '__main__':
    main()
