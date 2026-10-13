#!/usr/bin/env python3
"""Moves a view's literal UI text into the English catalogue (M23b).

    python3 eng/localize-xaml.py src/Codalus.Shell/Views/PluginsView.axaml [...]

Each literal Text, Content, Header, Title, ToolTip.Tip, PlaceholderText, Watermark and AutomationProperties.Name becomes
{l:T <view>.<words>}, and its text goes into src/Codalus.Shell/Localization/en.json (sorted). Values without
letters (symbols, numbers) stay. LocalizationTests keeps the catalogue and the views in step and stops new literals.
"""
import html
import json
import re
import sys
from pathlib import Path

ATTRIBUTES = r'(?:Text|Content|Header|Title|ToolTip\.Tip|PlaceholderText|Watermark|AutomationProperties\.Name)'
LITERAL = re.compile(r'(?<![\w.])(' + ATTRIBUTES + r')="([^"{][^"]*)"')
CATALOG = Path('src/Codalus.Shell/Localization/en.json')


def camel(words):
    words = [w for w in re.findall(r'[A-Za-z0-9]+', words)][:6]
    if not words:
        return 'text'
    return words[0].lower() + ''.join(w[:1].upper() + w[1:].lower() for w in words[1:])


def view_name(path):
    stem = Path(path).stem
    for suffix in ('View', 'Window', 'Dialog'):
        if stem.endswith(suffix) and stem != suffix:
            stem = stem[: -len(suffix)]
    return stem[:1].lower() + stem[1:]


def main(files):
    catalog = json.loads(CATALOG.read_text(encoding='utf-8')) if CATALOG.exists() else {}
    for file in files:
        path = Path(file)
        text = path.read_text(encoding='utf-8')
        prefix = view_name(file)
        keys_by_text = {}

        def replace(match):
            attribute, value = match.group(1), match.group(2)
            english = html.unescape(value)
            if not re.search(r'[A-Za-z]', english):
                return match.group(0)
            key = keys_by_text.get(english)
            if key is None:
                base = f'{prefix}.{camel(english)}'
                key, n = base, 2
                while key in catalog and catalog[key] != english:
                    key, n = f'{base}{n}', n + 1
                keys_by_text[english] = key
                catalog[key] = english
            return f'{attribute}="{{l:T {key}}}"'

        changed = LITERAL.sub(replace, text)
        if changed != text and 'xmlns:l=' not in changed:
            changed = re.sub(r'(<[A-Za-z:]+\s)', r'\1xmlns:l="using:Codalus.Shell.Infrastructure"\n             ', changed, count=1)
        if changed != text:
            path.write_text(changed, encoding='utf-8', newline='')
            print(f'{file}: {len(keys_by_text)} strings')
    CATALOG.write_text(json.dumps(dict(sorted(catalog.items())), ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')


if __name__ == '__main__':
    main(sys.argv[1:])
