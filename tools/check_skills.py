"""Checks every skill's header (the --- block at the top of skills/<name>/SKILL.md) is valid YAML with a name and a description.
A description with an unquoted ": " in it breaks the header, and the skill is then never picked up by itself: Claude Code's own
`claude plugin validate` doesn't say so. Run: python tools/check_skills.py   (needs PyYAML: pip install pyyaml)"""
import glob, io, os, sys

try:
    import yaml
except ImportError:
    sys.exit('needs PyYAML: pip install pyyaml')

root = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'skills')
bad = 0
for path in sorted(glob.glob(os.path.join(root, '*', 'SKILL.md'))):
    name = os.path.basename(os.path.dirname(path))
    text = io.open(path, encoding='utf-8').read()
    parts = text.split('---', 2)
    problem = None
    if not text.startswith('---') or len(parts) < 3:
        problem = 'no --- header at the top'
    else:
        try:
            head = yaml.safe_load(parts[1]) or {}
            if head.get('name') != name: problem = f'name is {head.get("name")!r}, the folder is {name!r}'
            elif len(str(head.get('description', ''))) < 40: problem = 'no real description'
        except yaml.YAMLError as e:
            problem = 'the header is not valid YAML (quote the description if it has ": " in it): ' + str(e).splitlines()[0]
    print(f'{name}: ' + (problem or 'ok'))
    bad += bool(problem)
sys.exit(1 if bad else 0)
