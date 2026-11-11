"""Validate the source metadata and catalog shipped in every release."""
import json,re,pathlib,xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[1]
version=ET.parse(root/'agent/PhoneBox.Agent/PhoneBox.Agent.csproj').find('.//Version').text
assert re.fullmatch(r'\d+\.\d+\.\d+',version),version
gradle=(root/'android/app/build.gradle.kts').read_text()
assert re.search(r'versionName = "'+re.escape(version)+'"',gradle),'Android version differs'
assert int(re.search(r'versionCode = (\d+)',gradle)[1])>0
changelog=(root/'CHANGELOG.md').read_text(encoding='utf-8')
assert re.search(r'^## '+re.escape(version)+r' — \d{4}-\d{2}-\d{2}$',changelog,re.M),'Missing dated release notes'
widgets=json.loads((root/'agent/PhoneBox.Agent/widgets.json').read_text(encoding='utf-8'))
templates=json.loads((root/'agent/PhoneBox.Agent/templates.json').read_text(encoding='utf-8'))
types={w['type'] for w in widgets}
assert len(types)==len(widgets),'Duplicate widget types'
assert len({t['id'] for t in templates})==len(templates),'Duplicate templates'
for t in templates:
 for w in t['widgets']:
  assert w['type'] in types,(t['id'],w['type'])
  assert 0<=w.get('x',0) and 0<=w.get('y',0) and w['x']+w['w']<=12 and w['y']+w['h']<=6,(t['id'],w)
readme=(root/'README.md').read_text(encoding='utf-8')
import importlib.util,sys
sys.dont_write_bytecode=True
spec=importlib.util.spec_from_file_location('pages_build',root/'tools/pages/build.py');build=importlib.util.module_from_spec(spec);spec.loader.exec_module(build)
site=build.landing_html()
assert f'**{len(widgets)} widgets**' in readme and f'**{len(templates)} templates**' in readme,'README catalog counts differ'
assert f'{len(widgets)} widgets, {len(templates)} templates' in site and f'PhoneBox {version}' in site,'Demo catalog counts or version differ'
# the landing page takes its numbers from the sources; a number typed by hand would go stale
assert not re.search(r'\d+ (widgets|templates)',(root/'tools/pages/index.html').read_text(encoding='utf-8')),'Hard-coded catalog count in the landing page'
demo=(root/'tools/pages/demo.js').read_text(encoding='utf-8')
pages=json.loads(re.search(r'const PAGES = (\[[^\]]*\])',demo)[1].replace("'",'"'))
assert sorted(pages)==sorted(t['id'] for t in templates),'The demo does not show every template exactly once'
print(f'PASS: {version}; Android metadata; {len(widgets)} widgets; {len(templates)} templates; README/demo counts')
