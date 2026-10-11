"""The project template lives here (templates/project; Claude Tools writes it to BepInEx/claude/templates/project).
The mod manager repo keeps the same files as template/, where its CONTRIBUTING sends other mod makers. This copies Claude Tools'
version over the manager's, so the two stay the same. Run after changing the template, then commit both repos.

    python tools/sync_project_template.py            copy, and list what changed
    python tools/sync_project_template.py --check    only say whether they differ (exit 1 if they do)
"""
import filecmp, os, shutil, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.normpath(os.path.join(HERE, '..', 'templates', 'project'))
TARGET = os.path.normpath(os.environ.get('MOD_MANAGER_DIR', os.path.join(HERE, '..', '..', 'valhiem-mod-manager')) + '/template')
SKIP = {'bin', 'obj', '.vs'}


def files(root):
    out = set()
    for d, dirs, names in os.walk(root):
        dirs[:] = [x for x in dirs if x not in SKIP]
        out |= {os.path.relpath(os.path.join(d, n), root) for n in names}
    return out


if not os.path.isdir(TARGET):
    sys.exit(f'no mod manager template at {TARGET} (set MOD_MANAGER_DIR to the mod manager repo)')
src, dst = files(SOURCE), files(TARGET)
changed = sorted(f for f in src & dst if not filecmp.cmp(os.path.join(SOURCE, f), os.path.join(TARGET, f), shallow=False))
added, removed = sorted(src - dst), sorted(dst - src)
if '--check' in sys.argv:
    for kind, names in (('differs', changed), ('only in Claude Tools', added), ('only in the manager', removed)):
        for f in names: print(f'{kind}: {f}')
    print('the same' if not (changed or added or removed) else 'not the same: run without --check to copy')
    sys.exit(1 if changed or added or removed else 0)
for f in changed + added:
    os.makedirs(os.path.dirname(os.path.join(TARGET, f)), exist_ok=True)
    shutil.copy2(os.path.join(SOURCE, f), os.path.join(TARGET, f))
    print(('updated ' if f in changed else 'added   ') + f)
for f in removed:
    os.remove(os.path.join(TARGET, f))
    print('removed ' + f)
print('nothing to copy: the same' if not (changed or added or removed) else f'{TARGET} now matches Claude Tools')
