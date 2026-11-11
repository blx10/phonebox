#!/usr/bin/env python3
"""Assembles the static web demo (GitHub Pages) from the real panel and settings page:  python tools/pages/build.py _site"""
import re, shutil, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WWW = ROOT / 'android' / 'app' / 'src' / 'main' / 'assets' / 'www'
AGENT = ROOT / 'agent' / 'PhoneBox.Agent'
PAGES = Path(__file__).resolve().parent


def version():
    """The agent's version: the demo reports it as the installed one, and the landing page shows it."""
    return re.search(r'<Version>([^<]+)</Version>', (AGENT / 'PhoneBox.Agent.csproj').read_text(encoding='utf-8'))[1]


def demo_tag(panel=False, relay=False):
    return f'<script src="../demo.js" data-version="{version()}"{" data-panel" if panel else ""}{" data-relay" if relay else ""}></script>\n'


def landing_html():
    """The landing page with the catalog counts and the version taken from the sources."""
    import json
    count = lambda name: len(json.loads((AGENT / name).read_text(encoding='utf-8')))
    html = (PAGES / 'index.html').read_text(encoding='utf-8')
    for key, value in (('widgets', count('widgets.json')), ('templates', count('templates.json')), ('version', version())):
        assert '{{' + key + '}}' in html, key
        html = html.replace('{{' + key + '}}', str(value))
    assert '{{' not in html
    return html


def panel_html(relay=False):
    """The panel's index.html with the demo API loaded first (a classic script, before the ES modules)."""
    html = (WWW / 'index.html').read_text(encoding='utf-8')
    tag = '<script type="module" src="main.js"></script>'
    assert tag in html
    return html.replace(tag, demo_tag(panel=True, relay=relay) + tag)


def settings_html(relay=False):
    """The real settings page with the demo API loaded before its script."""
    html = (AGENT / 'settings.html').read_text(encoding='utf-8')
    assert '<script>' in html
    return (html.replace('<script>', demo_tag(relay=relay) + '<script>', 1)
            .replace('href="/favicon.ico"', 'href="../favicon.ico"').replace('src="/favicon.ico"', 'src="../favicon.ico"'))


def build(out):
    if out.exists(): shutil.rmtree(out)
    (out / 'panel').mkdir(parents=True)
    (out / 'settings').mkdir()
    shutil.copy(PAGES / 'demo.js', out / 'demo.js')
    (out / 'index.html').write_text(landing_html(), encoding='utf-8')
    for name in ('widgets.json', 'templates.json'): shutil.copy(AGENT / name, out / name)
    shutil.copy(AGENT / 'phonebox.ico', out / 'favicon.ico')
    shutil.copytree(WWW, out / 'panel', dirs_exist_ok=True, ignore=shutil.ignore_patterns('index.html'))
    (out / 'panel' / 'index.html').write_text(panel_html(), encoding='utf-8')
    (out / 'settings' / 'index.html').write_text(settings_html(), encoding='utf-8')
    print(f'web demo written to {out}')


if __name__ == '__main__':
    build(Path(sys.argv[1] if len(sys.argv) > 1 else '_site').resolve())
