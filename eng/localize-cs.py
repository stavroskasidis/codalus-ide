#!/usr/bin/env python3
"""Moves user-facing messages built in C# into the catalogue's keys (M23c).

    python3 eng/localize-cs.py src/DaedalusStudio.Shell/ShellHost.Git.cs [...]

The messages of Notifications.Info/Warning/Error (the message, not the buttons: code compares the chosen button's text),
StatusBar.ShowMessage, Dialogs.ConfirmAsync (title, message, action) and Dialogs.InputAsync (title, prompt) become
Localizer.Get("msg.<file>.<words>", "English") or, for interpolated strings, Localizer.Format("msg.<file>.<words>",
"English {0}", values). Only plain or interpolated string literals are rewritten; anything else (verbatim and raw
strings, holes with strings or nested quotes, concatenations) stays as it is. LocalizationCatalogTests reads the
English from these calls into Localization/en.json.
"""
import re
import sys
from pathlib import Path

# API -> indexes of the arguments that are user-facing text
APIS = {
    'Notifications.Info': [0], 'Notifications.Warning': [0], 'Notifications.Error': [0],
    'StatusBar.ShowMessage': [0], 'Dialogs.ConfirmAsync': [0, 1, 2], 'Dialogs.InputAsync': [0, 1],
}
CALL = re.compile(r'\b(' + '|'.join(re.escape(a) for a in APIS) + r')\(')


def camel(text):
    words = re.findall(r'[A-Za-z0-9]+', re.sub(r'\{[^}]*\}', ' ', text.replace("'", '')))[:6]
    if not words:
        return 'text'
    return words[0].lower() + ''.join(w[:1].upper() + w[1:].lower() for w in words[1:])


def scan_string(s, i):
    """s[i] starts a string literal ("..." or $"..."); returns the index after it, or None if unsupported."""
    interpolated = s[i] == '$'
    j = i + 1 if interpolated else i
    if s[j] != '"' or s[j:j + 3] == '"""' or (j > 0 and s[j - 1] == '@'):
        return None
    j += 1
    depth = 0
    while j < len(s):
        c = s[j]
        if c == '\\':
            j += 2
            continue
        if interpolated and c == '{':
            if s[j + 1] == '{' and depth == 0:
                j += 2
                continue
            depth += 1
        elif interpolated and c == '}':
            if depth == 0 and s[j + 1] == '}':
                j += 2
                continue
            depth -= 1
        elif c == '"':
            if depth == 0:
                return j + 1
            return None  # a string inside a hole: leave it
        elif c == '\n':
            return None
        j += 1
    return None


def split_args(s, start):
    """s[start] is just after '('; returns ([(argStart, argEnd)], closeIndex) at top level, or None."""
    args, depth, j, arg_start = [], 0, start, start
    while j < len(s):
        c = s[j]
        if c == '"' or (c == '$' and s[j + 1] == '"') or (c == '@' and s[j + 1] == '"'):
            if c == '@' or s[j:j + 3] == '"""' or s[j + 1:j + 4] == '"""':
                return None
            end = scan_string(s, j)
            if end is None:
                return None
            j = end
            continue
        if c == "'":
            j = s.index("'", j + 2 if s[j + 1] == '\\' else j + 1) + 1
            continue
        if c in '([{':
            depth += 1
        elif c in ')]}':
            if depth == 0:
                args.append((arg_start, j))
                return args, j
            depth -= 1
        elif c == ',' and depth == 0:
            args.append((arg_start, j))
            arg_start = j + 1
        j += 1
    return None


def holes(literal):
    """An interpolated literal's text as a Format pattern and its expressions, or None."""
    body, pattern, values, i = literal[2:-1], [], [], 0
    while i < len(body):
        c = body[i]
        if c == '{' and body[i + 1] == '{':
            pattern.append('{{'); i += 2; continue
        if c == '}' and body[i + 1:i + 2] == '}':
            pattern.append('}}'); i += 2; continue
        if c == '{':
            depth, j = 0, i + 1
            while True:
                if body[j] == '{':
                    depth += 1
                elif body[j] == '}':
                    if depth == 0:
                        break
                    depth -= 1
                j += 1
            hole = body[i + 1:j]
            expression, fmt, level, question = hole, '', 0, False
            for k, ch in enumerate(hole):
                if ch in '([':
                    level += 1
                elif ch in ')]':
                    level -= 1
                elif ch == '?' and level == 0:
                    question = True
                elif ch == ':' and level == 0 and not question:
                    expression, fmt = hole[:k], hole[k:]
                    break
            if ',' in expression and '(' not in expression:
                return None  # alignment
            pattern.append('{' + str(len(values)) + fmt + '}')
            values.append(expression.strip())
            i = j + 1
            continue
        pattern.append(c)
        i += 1
    return '"' + ''.join(pattern) + '"', values


def main(files):
    total = 0
    for file in files:
        path = Path(file)
        s = path.read_text(encoding='utf-8')
        prefix = 'msg.' + path.stem.replace('ShellHost.', '').replace('.', '')
        prefix = prefix[:4] + prefix[4:5].lower() + prefix[5:]
        used, edits = {}, []
        for m in CALL.finditer(s):
            parsed = split_args(s, m.end())
            if parsed is None:
                continue
            args, _ = parsed
            for index in APIS[m.group(1)]:
                if index >= len(args):
                    continue
                a, b = args[index]
                raw = s[a:b]
                literal = raw.strip()
                if not (literal.startswith('"') or literal.startswith('$"')) or scan_string(literal, 0) != len(literal):
                    continue
                english = literal[2:-1] if literal.startswith('$') else literal[1:-1]
                if not re.search(r'[A-Za-z]', re.sub(r'\{[^}]*\}', '', english)):
                    continue
                base = f'{prefix}.{camel(english)}'
                key, n = base, 2
                while key in used and used[key] != english:
                    key, n = f'{base}{n}', n + 1
                used[key] = english
                if literal.startswith('$'):
                    converted = holes(literal)
                    if converted is None:
                        continue
                    pattern, values = converted
                    replacement = f'Localizer.Format("{key}", {pattern}' + ''.join(', ' + v for v in values) + ')' if values else f'Localizer.Get("{key}", {pattern})'
                else:
                    replacement = f'Localizer.Get("{key}", {literal})'
                lead = raw[:len(raw) - len(raw.lstrip())]
                edits.append((a, b, lead + replacement))
        if not edits:
            continue
        for a, b, text in sorted(edits, reverse=True):
            s = s[:a] + text + s[b:]
        if 'using DaedalusStudio.Workbench.Localization;' not in s:
            usings = list(re.finditer(r'^using [^;]+;\r?\n', s, re.M))
            at = usings[-1].end() if usings else 0
            s = s[:at] + 'using DaedalusStudio.Workbench.Localization;\n' + s[at:]
        path.write_text(s, encoding='utf-8', newline='')
        total += len(edits)
        print(f'{file}: {len(edits)} messages')
    print(f'{total} messages')


if __name__ == '__main__':
    main(sys.argv[1:])
