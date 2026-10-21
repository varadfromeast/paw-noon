#!/usr/bin/env python3
"""Cheap static checks for Paw Noon, runnable without Unity (no compiler, standard library only).

    python3 Tools/Lint/check_cs.py              # whole repo, errors + warnings
    python3 Tools/Lint/check_cs.py --errors     # errors only
    python3 Tools/Lint/check_cs.py --strict     # warnings fail too
    python3 Tools/Lint/check_cs.py path/a.cs .. # only these C# files (asset checks still run)
    python3 Tools/Lint/check_cs.py --self-test  # the checker's own fixtures

Exit code 0 = no errors (1 = errors, or warnings with --strict). It is a tripwire, not a compiler: it catches the
mistakes that cost a round trip to the Mac (a brace lost in an edit, CS0136 / CS0128, a dangling `using`, a sprite
key or an audio clip that silently does nothing), and it flags per-frame allocations in Update / Tick bodies.

C# checks (per file under Assets/):
  E brace   balanced {} () [] (strings, verbatim / interpolated strings, chars and comments skipped), checked
            once with the #if branch and once with the #else branch of every preprocessor block
  E scope   a local declared in an inner block with the name of a local or parameter of an enclosing block of the
            same method (CS0136), or twice in one block (CS0128). Lambda parameters are not checked.
  E using   `using X;` where X is neither declared in the repo nor in the common Unity / .NET set
  W alloc   inside Update / LateUpdate / FixedUpdate / Tick* bodies: `new` of a class or array, string
            concatenation or interpolation, ToString, LINQ, closures, GetComponent, Find*, Resources.Load.
            Append `// lint: alloc-ok (why)` to a line that only runs on a rare transition.
Asset checks:
  E sprite  ui.json / fx.json entries whose PNG is missing; Ui.Get / kit.Sprite / kit.Panel / PixelButton /
            fx.Fx literals that name no entry (W for TryGet, which is optional by design)
  W sprite  PNGs in Art/UI and Art/FX with no entry
  E audio   an "sfx_*" key used in code that PawAudio never preloads (Play would be silent) or that has no file
  W audio   a preloaded clip nobody plays, or a preload entry with no file
"""
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
ASSETS = os.path.join(ROOT, 'Assets')
CODE = os.path.join(ASSETS, 'PawNoon', 'Code')
ART = os.path.join(ASSETS, 'PawNoon', 'Resources', 'Art')
AUDIO = os.path.join(ASSETS, 'PawNoon', 'Resources', 'Audio')

KEYWORDS = set('''abstract as base bool break byte case catch char checked class const continue decimal default delegate do
double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is
lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed
short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using
virtual void volatile while yield await var when nameof get set add remove value partial where async'''.split())
TYPE_KEYWORDS = set('bool byte char decimal double float int long object sbyte short string uint ulong ushort var dynamic'.split())
NOT_A_TYPE = KEYWORDS - TYPE_KEYWORDS

COMMON_NAMESPACES = ('System', 'UnityEngine', 'UnityEditor', 'Unity', 'NUnit', 'TMPro', 'JetBrains', 'Microsoft',
                     'AOT', 'AppleAuth')
VALUE_TYPES = set('''Vector2 Vector3 Vector4 Vector2Int Vector3Int Color Color32 Rect RectInt Quaternion Matrix4x4 Bounds
BoundsInt Ray Ray2D Plane KeyValuePair DateTime TimeSpan Keyframe RaycastHit RaycastHit2D LayerMask Touch TimedPress
DuelEvent RoundStartMsg DrawCue ShotReport Glyph CultureInfo'''.split())


class Report:
    def __init__(self):
        self.errors = []
        self.warnings = []

    def err(self, kind, path, line, msg):
        self.errors.append((kind, rel(path), line, msg))

    def warn(self, kind, path, line, msg):
        self.warnings.append((kind, rel(path), line, msg))


def rel(p):
    try:
        return os.path.relpath(p, ROOT)
    except ValueError:
        return p


# ------------------------------------------------------------------------------------------------ lexing

class Tok:
    __slots__ = ('kind', 'text', 'line', 'pos')

    def __init__(self, kind, text, line, pos):
        self.kind, self.text, self.line, self.pos = kind, text, line, pos

    def __repr__(self):
        return '%s:%r@%d' % (self.kind, self.text, self.line)


def preprocess(src, branch):
    """Blanks preprocessor lines and keeps one branch of every #if chain: 'if' = the first, 'else' = the last
    (see _else_pass). Line numbers are kept (dropped lines become empty)."""
    if branch == 'else':
        return _else_pass(src)
    out = []
    stack = []   # branch index per open #if
    for line in src.split('\n'):
        s = line.strip()
        if s.startswith('#'):
            d = s[1:].strip().split(None, 1)[0] if s[1:].strip() else ''
            if d == 'if':
                stack.append(0)
            elif d in ('elif', 'else') and stack:
                stack[-1] += 1
            elif d == 'endif' and stack:
                stack.pop()
            if d in ('if', 'elif', 'else', 'endif', 'region', 'endregion', 'define', 'undef', 'pragma', 'warning',
                     'error', 'line', 'nullable'):
                out.append('')
                continue
        out.append(line if all(idx == 0 for idx in stack) else '')
    return '\n'.join(out)


def _else_pass(src):
    """Keeps the LAST branch of every #if/#elif/#else chain (a chain without #else keeps its #if body too, as
    that is what a build without the symbol would not see; blanking it would hide nothing worth checking)."""
    lines = src.split('\n')
    # first find, for every #if, how many branches it has
    branches = {}
    stack = []
    for i, line in enumerate(lines):
        s = line.strip()
        if not s.startswith('#'):
            continue
        d = s[1:].strip().split(None, 1)[0] if s[1:].strip() else ''
        if d == 'if':
            stack.append([i, 1, False])
        elif d == 'elif' and stack:
            stack[-1][1] += 1
        elif d == 'else' and stack:
            stack[-1][1] += 1
            stack[-1][2] = True
        elif d == 'endif' and stack:
            start, n, has_else = stack.pop()
            branches[start] = (n, has_else)
    out = []
    stack = []
    for i, line in enumerate(lines):
        s = line.strip()
        if s.startswith('#'):
            d = s[1:].strip().split(None, 1)[0] if s[1:].strip() else ''
            if d == 'if':
                n, has_else = branches.get(i, (1, False))
                stack.append([0, n if has_else else 1])
                out.append('')
                continue
            if d in ('elif', 'else') and stack:
                stack[-1][0] += 1
                out.append('')
                continue
            if d == 'endif' and stack:
                stack.pop()
                out.append('')
                continue
            if d in ('region', 'endregion', 'define', 'undef', 'pragma', 'warning', 'error', 'line', 'nullable'):
                out.append('')
                continue
        keep = all(idx == want - 1 for idx, want in stack)
        out.append(line if keep else '')
    return '\n'.join(out)


def lex(src):
    """Tokens: id, num, str, char, op (single char or => ++ -- etc.). Comments are dropped."""
    toks = []
    i, n, line = 0, len(src), 1
    while i < n:
        c = src[i]
        if c == '\n':
            line += 1
            i += 1
            continue
        if c in ' \t\r\f\v':
            i += 1
            continue
        if src.startswith('//', i):
            j = src.find('\n', i)
            i = n if j < 0 else j
            continue
        if src.startswith('/*', i):
            j = src.find('*/', i + 2)
            j = n if j < 0 else j + 2
            line += src.count('\n', i, j)
            i = j
            continue
        start, sline = i, line
        # strings: "..", @"..", $"..", $@"..", @$".."
        m = re.match(r'(\$@|@\$|\$|@)?"', src[i:i + 3])
        if m:
            pre = m.group(1) or ''
            verbatim = '@' in pre
            interp = '$' in pre
            i += len(m.group(0))
            depth = 0
            while i < n:
                ch = src[i]
                if ch == '\n':
                    line += 1
                if interp and ch == '{':
                    if src.startswith('{{', i) and depth == 0:
                        i += 2
                        continue
                    depth += 1
                    i += 1
                    continue
                if interp and ch == '}' and depth > 0:
                    depth -= 1
                    i += 1
                    continue
                if depth > 0:
                    # inside an interpolation hole: skip nested strings simply
                    if ch == '"':
                        j = i + 1
                        while j < n and src[j] != '"':
                            j += 2 if src[j] == '\\' else 1
                        i = j + 1
                        continue
                    i += 1
                    continue
                if verbatim:
                    if ch == '"':
                        if src.startswith('""', i):
                            i += 2
                            continue
                        i += 1
                        break
                    i += 1
                    continue
                if ch == '\\':
                    i += 2
                    continue
                if ch == '"':
                    i += 1
                    break
                i += 1
            toks.append(Tok('str', src[start:i], sline, start))
            continue
        if c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == '\\' else 1
            i = j + 1
            toks.append(Tok('char', src[start:i], sline, start))
            continue
        if c.isalpha() or c == '_' or (c == '@' and i + 1 < n and (src[i + 1].isalpha() or src[i + 1] == '_')):
            j = i + 1
            while j < n and (src[j].isalnum() or src[j] == '_'):
                j += 1
            toks.append(Tok('id', src[i:j].lstrip('@'), sline, start))
            i = j
            continue
        if c.isdigit() or (c == '.' and i + 1 < n and src[i + 1].isdigit()):
            m = re.match(r'0[xX][0-9a-fA-F_]+[uUlL]*|[0-9_]*\.?[0-9_]+(?:[eE][+-]?[0-9]+)?[fFdDmMuUlL]*', src[i:])
            j = i + max(1, len(m.group(0)))
            toks.append(Tok('num', src[i:j], sline, start))
            i = j
            continue
        for op in ('=>', '++', '--', '&&', '||', '==', '!=', '<=', '>=', '+=', '-=', '*=', '/=', '??', '?.', '::'):
            if src.startswith(op, i):
                toks.append(Tok('op', op, sline, start))
                i += len(op)
                break
        else:
            toks.append(Tok('op', c, sline, start))
            i += 1
    return toks


# ------------------------------------------------------------------------------------------------ braces

PAIRS = {'{': '}', '(': ')', '[': ']'}
CLOSE = {v: k for k, v in PAIRS.items()}


def check_braces(path, toks, rep, label):
    stack = []
    for t in toks:
        if t.kind != 'op':
            continue
        if t.text in PAIRS:
            stack.append(t)
        elif t.text in CLOSE:
            if not stack:
                rep.err('brace', path, t.line, "unmatched '%s'%s" % (t.text, label))
                return False
            o = stack.pop()
            if PAIRS[o.text] != t.text:
                rep.err('brace', path, t.line, "'%s' closes '%s' opened at line %d%s" % (t.text, o.text, o.line, label))
                return False
    if stack:
        o = stack[-1]
        rep.err('brace', path, o.line, "'%s' never closed%s" % (o.text, label))
        return False
    return True


# ------------------------------------------------------------------------------------------------ structure

class Block:
    __slots__ = ('open', 'close', 'parent', 'code', 'method_root', 'decls', 'kind', 'header', 'children', 'name')

    def __init__(self, open_i, parent):
        self.open = open_i
        self.close = None
        self.parent = parent
        self.code = False
        self.method_root = None
        self.decls = {}       # name -> line
        self.kind = ''
        self.header = None    # (start, end) token range of the parenthesised header that owns this block
        self.children = []
        self.name = ''


CODE_PRE = {'else', 'try', 'finally', 'do', 'get', 'set', 'add', 'remove', 'init', 'unchecked', 'checked', 'unsafe'}
TYPE_DECL = {'class', 'struct', 'interface', 'enum', 'namespace', 'record'}


def matching(toks, i, open_c, close_c, step=1):
    depth = 0
    j = i
    while 0 <= j < len(toks):
        t = toks[j]
        if t.kind == 'op' and t.text == open_c:
            depth += 1
        elif t.kind == 'op' and t.text == close_c:
            depth -= 1
            if depth == 0:
                return j
        j += step
    return -1


def build_blocks(toks):
    """Every { } pair, marked as code (statement block) or not (type / namespace body, initializer)."""
    root = Block(-1, None)
    stack = [root]
    blocks = []
    for i, t in enumerate(toks):
        if t.kind != 'op':
            continue
        if t.text == '{':
            parent = stack[-1]
            b = Block(i, parent)
            parent.children.append(b)
            prev = toks[i - 1] if i > 0 else None
            # walk back to see what owns this brace
            if prev is not None and prev.kind == 'op' and prev.text == ')':
                j = matching(toks, i - 1, ')', '(', -1)
                b.header = (j, i - 1)
                kw = toks[j - 1] if j > 0 else None
                if kw is not None and kw.kind == 'id' and kw.text in ('if', 'while', 'for', 'foreach', 'using', 'lock',
                                                                       'catch', 'switch', 'fixed'):
                    b.kind = kw.text
                    b.code = True
                elif kw is not None and kw.kind == 'id' and kw.text in ('new', 'base', 'this'):
                    b.kind = 'init'
                    b.code = parent.code if kw.text == 'new' else True
                    if kw.text == 'new':
                        b.kind = 'init'
                else:
                    # method / constructor / local function / lambda with parenthesised params, or a call followed by
                    # an initializer (rare: `Foo() {` is not valid C#)
                    b.kind = 'method'
                    b.code = True
            elif prev is not None and prev.kind == 'op' and prev.text == '=>':
                b.kind = 'lambda'
                b.code = True
            elif prev is not None and prev.kind == 'id' and prev.text in CODE_PRE:
                b.kind = prev.text
                b.code = True
            elif prev is not None and prev.kind == 'op' and prev.text in ('{', '}', ';', ':') and parent.code:
                b.kind = 'block'
                b.code = True
            elif prev is not None and prev.kind == 'op' and prev.text in ('=', ',', '(', '[', 'return', '?', '??'):
                b.kind = 'init'
                b.code = False
            else:
                # type body, namespace body, enum body, property body `int X {`, object initializer `new T {`
                k = i - 1
                while k >= 0 and not (toks[k].kind == 'op' and toks[k].text in (';', '{', '}')):
                    if toks[k].kind == 'id' and toks[k].text in TYPE_DECL:
                        break
                    k -= 1
                if k >= 0 and toks[k].kind == 'id' and toks[k].text in TYPE_DECL:
                    b.kind = toks[k].text
                elif prev is not None and prev.kind == 'id' and toks[i - 2].kind == 'id' and toks[i - 2].text == 'new':
                    b.kind = 'init'
                elif prev is not None and prev.kind in ('id',) or (prev is not None and prev.text in ('>', ']', '?')):
                    b.kind = 'property' if not parent.code else 'init'
                else:
                    b.kind = 'other'
                b.code = False
            if b.code:
                b.method_root = parent.method_root if (parent.code and parent.method_root is not None) else b
            stack.append(b)
            blocks.append(b)
        elif t.text == '}':
            if len(stack) > 1:
                stack[-1].close = i
                stack.pop()
    return root, blocks


def enclosing(blocks_by_open, toks, i, root):
    """Innermost block containing token index i."""
    best = root
    for b in blocks_by_open:
        if b.open < i and (b.close is None or i < b.close):
            if best is root or b.open > best.open:
                best = b
    return best


# ------------------------------------------------------------------------------------------------ CS0136

def type_end(toks, i):
    """If a type starts at toks[i], return the index after it, else -1. Accepts qualified names, generics,
    arrays, nullable and tuples `(int a, int b)` are not handled (returns -1)."""
    n = len(toks)
    t = toks[i]
    if t.kind != 'id' or t.text in NOT_A_TYPE:
        return -1
    j = i + 1
    while j + 1 < n and toks[j].kind == 'op' and toks[j].text == '.' and toks[j + 1].kind == 'id':
        j += 2
    if j < n and toks[j].kind == 'op' and toks[j].text == '<':
        depth = 0
        k = j
        while k < n:
            tk = toks[k]
            if tk.kind == 'op' and tk.text == '<':
                depth += 1
            elif tk.kind == 'op' and tk.text == '>':
                depth -= 1
                if depth == 0:
                    break
            elif tk.kind == 'op' and tk.text in (';', '{', '}', '(', ')', '=', '&&', '||', '=>'):
                return -1
            k += 1
        if k >= n:
            return -1
        j = k + 1
        while j + 1 < n and toks[j].kind == 'op' and toks[j].text == '.' and toks[j + 1].kind == 'id':
            j += 2
    while j < n and toks[j].kind == 'op' and toks[j].text == '?':
        j += 1
    while j + 1 < n and toks[j].kind == 'op' and toks[j].text == '[' and toks[j + 1].kind == 'op' and toks[j + 1].text in (']', ','):
        k = j + 1
        while k < n and toks[k].kind == 'op' and toks[k].text == ',':
            k += 1
        if k < n and toks[k].text == ']':
            j = k + 1
        else:
            break
    return j


def stmt_start(toks, i):
    p = toks[i - 1] if i > 0 else None
    return p is None or (p.kind == 'op' and p.text in (';', '{', '}', ')', ':')) or (p.kind == 'id' and p.text in ('else', 'do'))


def find_decls(toks, blocks, root):
    """Yields (scope Block, name, line, token index). Scopes: the innermost code block, or the block a for /
    foreach / using / catch / while header belongs to."""
    opens = sorted([b for b in blocks], key=lambda b: b.open)
    header_owner = {}
    for b in blocks:
        if b.header is not None and b.kind in ('for', 'foreach', 'using', 'catch', 'while', 'fixed', 'method', 'lock'):
            for k in range(b.header[0], b.header[1] + 1):
                header_owner.setdefault(k, b)
    # brace-less for / foreach / using / catch headers: their variables get a private scope
    res = []
    n = len(toks)

    def scope_at(i):
        if i in header_owner:
            return header_owner[i]
        return enclosing(opens, toks, i, root)

    for i in range(n):
        t = toks[i]
        if t.kind != 'id':
            continue
        sc = scope_at(i)
        if not sc.code:
            continue
        # method parameters are declared in the method's own block (header of a 'method' block)
        if sc.header is not None and sc.kind == 'method' and sc.header[0] <= i <= sc.header[1]:
            # parameter: TYPE name followed by , ) or =
            e = type_end(toks, i)
            if e > 0 and e < n and toks[e].kind == 'id' and toks[e].text not in KEYWORDS and e + 1 < n and \
                    toks[e + 1].kind == 'op' and toks[e + 1].text in (',', ')', '='):
                pj = i - 1
                if toks[pj].kind == 'op' and toks[pj].text in ('(', ',') or (toks[pj].kind == 'id' and toks[pj].text in ('out', 'ref', 'in', 'params', 'this')):
                    res.append((sc, toks[e].text, toks[e].line, e))
            continue
        # out var x / out T x / is T x
        if t.text in ('out', 'is') and i + 1 < n:
            e = type_end(toks, i + 1)
            if e > 0 and e < n and toks[e].kind == 'id' and toks[e].text not in KEYWORDS:
                nxt = toks[e + 1] if e + 1 < n else None
                if nxt is not None and nxt.kind == 'op' and nxt.text in (')', ',', '&&', '||', ';', '?', ':', '='):
                    if not (t.text == 'is' and toks[e].text in ('null',)):
                        res.append((pattern_scope(toks, i, sc, header_owner, opens, root), toks[e].text, toks[e].line, e))
            continue
        # foreach (T x in ...), for (T x = ...), using (T x = ...), catch (T x)
        if t.text in TYPE_KEYWORDS or (t.kind == 'id' and t.text not in KEYWORDS):
            prev = toks[i - 1] if i > 0 else None
            in_header = i in header_owner
            starts = stmt_start(toks, i) or (in_header and prev is not None and prev.kind == 'op' and prev.text == '(')
            if not starts:
                continue
            if prev is not None and prev.kind == 'op' and prev.text == ')' and not in_header:
                # `(T)x` cast, `if (...) x = 1;` etc: a declaration never follows a ')' except after a header
                hj = matching(toks, i - 1, ')', '(', -1)
                kw = toks[hj - 1] if hj > 0 else None
                if not (kw is not None and kw.kind == 'id' and kw.text in ('if', 'while', 'for', 'foreach', 'using', 'lock')):
                    continue
            # var (a, b) = ...
            if t.text == 'var' and i + 1 < n and toks[i + 1].text == '(':
                k = matching(toks, i + 1, '(', ')')
                for m in range(i + 2, k):
                    if toks[m].kind == 'id' and toks[m + 1].text in (',', ')'):
                        res.append((sc, toks[m].text, toks[m].line, m))
                continue
            e = type_end(toks, i)
            if e < 0 or e >= n:
                continue
            nm = toks[e]
            if nm.kind != 'id' or nm.text in KEYWORDS:
                continue
            nxt = toks[e + 1] if e + 1 < n else None
            if nxt is None:
                continue
            if nxt.kind == 'op' and nxt.text in ('=', ';', ','):
                pass
            elif in_header and ((nxt.kind == 'id' and nxt.text == 'in') or (nxt.kind == 'op' and nxt.text == ')')):
                pass
            else:
                continue   # includes `T name(` (a local function: its name rarely clashes)
            # local declarations: T a = 1, b = 2;
            declared = [(nm.text, nm.line, e)]
            if nxt.text in ('=', ','):
                k = e + 1
                depth = 0
                while k < n:
                    tk = toks[k]
                    if tk.kind == 'op' and tk.text in ('(', '[', '{'):
                        depth += 1
                    elif tk.kind == 'op' and tk.text in (')', ']', '}'):
                        if depth == 0:
                            break
                        depth -= 1
                    elif tk.kind == 'op' and tk.text == ';' and depth == 0:
                        break
                    elif tk.kind == 'op' and tk.text == ',' and depth == 0:
                        if k + 2 < n and toks[k + 1].kind == 'id' and toks[k + 2].kind == 'op' and toks[k + 2].text in ('=', ',', ';'):
                            declared.append((toks[k + 1].text, toks[k + 1].line, k + 1))
                    k += 1
            for d in declared:
                res.append((sc, d[0], d[1], d[2]))
    # lambda parameters are not collected on purpose (C# 9 forbids shadowing there too, but the pattern is rare
    # and the heuristics would be noisy)
    return res


def pattern_scope(toks, i, sc, header_owner, opens, root):
    """`out var x` / `is T x`: inside an if / expression statement the variable leaks into the enclosing block;
    inside a for / foreach / while / using header it belongs to that statement."""
    if i in header_owner:
        hb = header_owner[i]
        if hb.kind in ('for', 'foreach', 'while', 'using', 'catch', 'fixed', 'lock'):
            return hb
        if hb.kind == 'method':
            return hb
    return sc


def check_scopes(path, toks, rep):
    root, blocks = build_blocks(toks)
    decls = find_decls(toks, blocks, root)
    seen = {}
    for sc, name, line, idx in decls:
        if sc.method_root is None:
            continue
        seen.setdefault(id(sc.method_root), []).append((sc, name, line, idx))
    for _, lst in seen.items():
        reported = set()
        for a in range(len(lst)):
            sa, na, la, ia = lst[a]
            for b in range(a + 1, len(lst)):
                sb, nb, lb, ib = lst[b]
                if na != nb or ia == ib:
                    continue
                if sa is sb:
                    key = (na, la, lb)
                    if key not in reported:
                        reported.add(key)
                        rep.err('scope', path, lb, "'%s' declared twice in one block (also line %d): CS0128" % (nb, la))
                elif is_ancestor(sa, sb) or is_ancestor(sb, sa):
                    inner, outer = (lb, la) if is_ancestor(sa, sb) else (la, lb)
                    key = (na, min(la, lb), max(la, lb))
                    if key not in reported:
                        reported.add(key)
                        rep.err('scope', path, inner, "'%s' shadows the '%s' of an enclosing scope (line %d): CS0136" % (na, na, outer))


def is_ancestor(a, b):
    """True when block a encloses block b."""
    p = b.parent
    while p is not None:
        if p is a:
            return True
        p = p.parent
    return False


# ------------------------------------------------------------------------------------------------ using

def declared_namespaces(files):
    ns = set()
    for f in files:
        for m in re.finditer(r'^\s*namespace\s+([\w\.]+)', read(f), re.M):
            parts = m.group(1).split('.')
            for k in range(1, len(parts) + 1):
                ns.add('.'.join(parts[:k]))
    return ns


def check_usings(path, src, known, rep):
    for m in re.finditer(r'^\s*using\s+(static\s+)?([\w\.]+)\s*(=\s*([\w\.<>, ]+))?\s*;', src, re.M):
        line = src.count('\n', 0, m.start()) + 1
        target = m.group(4) if m.group(3) else m.group(2)
        target = target.strip()
        if m.group(1) or m.group(3):
            # using static X.Y / alias = X.Y.Z: check the namespace part loosely
            head = target.split('.')[0]
            if head in known or head in COMMON_NAMESPACES:
                continue
            rep.err('using', path, line, "'%s' does not start with a known namespace" % target)
            continue
        if target in known or target.split('.')[0] in COMMON_NAMESPACES:
            continue
        rep.err('using', path, line, "namespace '%s' is declared nowhere in the repo and is not a common Unity / .NET one" % target)


# ------------------------------------------------------------------------------------------------ per-frame allocs

HOT = re.compile(r'^(Update|LateUpdate|FixedUpdate|Tick\w*)$')
LINQ = {'Where', 'Select', 'SelectMany', 'ToList', 'ToArray', 'Any', 'All', 'OrderBy', 'OrderByDescending', 'First',
        'FirstOrDefault', 'Last', 'Count', 'Sum', 'Max', 'Min', 'Aggregate', 'Distinct', 'GroupBy', 'ToDictionary'}
FINDERS = {'GetComponent', 'GetComponents', 'GetComponentInChildren', 'GetComponentsInChildren', 'FindObjectOfType',
           'FindObjectsOfType', 'FindObjectsByType', 'FindFirstObjectByType', 'FindAnyObjectByType', 'Find', 'FindWithTag'}


def check_hot_allocs(path, toks, src_lines, rep):
    root, blocks = build_blocks(toks)
    for b in blocks:
        if b.kind != 'method' or b.header is None or b.close is None:
            continue
        name_tok = toks[b.header[0] - 1] if b.header[0] > 0 else None
        if name_tok is None or name_tok.kind != 'id' or not HOT.match(name_tok.text):
            continue
        # a method, not a call: the token before the name is a type / modifier
        before = toks[b.header[0] - 2] if b.header[0] > 1 else None
        if before is None or before.kind != 'id':
            continue
        meth = name_tok.text
        for k in range(b.open + 1, b.close):
            t = toks[k]
            line = src_lines[t.line - 1] if t.line - 1 < len(src_lines) else ''
            if 'lint: alloc-ok' in line:
                continue
            why = None
            if t.kind == 'id' and t.text == 'new':
                nx = toks[k + 1] if k + 1 < len(toks) else None
                if nx is not None and nx.kind == 'id':
                    e = type_end(toks, k + 1)
                    tn = nx.text
                    after = toks[e] if 0 < e < len(toks) else None
                    if after is not None and after.text == '[':
                        why = "new %s[] (array)" % tn
                    elif tn not in VALUE_TYPES:
                        why = "new %s (class?)" % tn
                elif nx is not None and nx.text == '[':
                    why = 'new[] (array)'
            elif t.kind == 'str' and t.text.startswith('$'):
                why = 'string interpolation'
            elif t.kind == 'str':
                p = toks[k - 1]
                q = toks[k + 1] if k + 1 < len(toks) else None
                if (p.kind == 'op' and p.text == '+') or (q is not None and q.kind == 'op' and q.text in ('+', '+=')):
                    why = 'string concatenation'
            elif t.kind == 'id' and t.text == 'ToString' and toks[k - 1].text == '.':
                why = 'ToString()'
            elif t.kind == 'id' and t.text in LINQ and toks[k - 1].text == '.' and k + 1 < len(toks) and toks[k + 1].text == '(' \
                    and toks[k - 2].text not in ('Mathf', 'Math'):
                why = 'LINQ .%s()' % t.text
            elif t.kind == 'id' and t.text in FINDERS and k + 1 < len(toks) and toks[k + 1].text in ('(', '<'):
                why = '%s per frame' % t.text
            elif t.kind == 'id' and t.text == 'Load' and toks[k - 1].text == '.' and toks[k - 2].text == 'Resources':
                why = 'Resources.Load per frame'
            elif t.kind == 'op' and t.text == '=>':
                why = 'lambda / closure'
            if why:
                rep.warn('alloc', path, t.line, '%s in %s()' % (why, meth))


# ------------------------------------------------------------------------------------------------ assets

def read(p):
    with open(p, encoding='utf-8', errors='replace') as f:
        return f.read()


def strip_comments(src):
    src = re.sub(r'/\*.*?\*/', lambda m: '\n' * m.group(0).count('\n'), src, flags=re.S)
    return re.sub(r'//[^\n]*', '', src)


def check_catalog(name, folder, code_srcs, rep, get_re, try_re):
    jpath = os.path.join(ART, folder, name + '.json')
    if not os.path.exists(jpath):
        rep.err('sprite', jpath, 1, 'catalog missing')
        return set()
    try:
        cat = json.loads(read(jpath))
    except ValueError as e:
        rep.err('sprite', jpath, 1, 'not valid JSON: %s' % e)
        return set()
    files = set()
    for key, v in cat.items():
        if key.startswith('_') or not isinstance(v, dict):
            continue
        f = v.get('file', key)
        files.add(f)
        if not os.path.exists(os.path.join(ART, folder, f + '.png')):
            rep.err('sprite', jpath, 1, "entry '%s' -> %s/%s.png does not exist" % (key, folder, f))
    for png in sorted(os.listdir(os.path.join(ART, folder))):
        if png.endswith('.png') and png[:-4] not in files:
            rep.warn('sprite', os.path.join(ART, folder, png), 1, 'PNG has no %s.json entry (unused?)' % name)
    keys = set(k for k in cat if not k.startswith('_'))
    for path, src in code_srcs:
        for rx, hard in ((get_re, True), (try_re, False)):
            for m in rx.finditer(src):
                for key in m.groups():
                    if key is None or key in keys:
                        continue
                    line = src.count('\n', 0, m.start()) + 1
                    msg = "%s key '%s' is not in %s.json" % ('sprite' if folder == 'UI' else 'fx', key, name)
                    if hard:
                        rep.err('sprite', path, line, msg)
                    else:
                        rep.warn('sprite', path, line, msg + ' (TryGet: optional art)')
    return keys


def check_audio(code_srcs, rep):
    pa = os.path.join(CODE, 'Runtime', 'PawAudio.cs')
    if not os.path.exists(pa):
        return
    src = strip_comments(read(pa))
    m = re.search(r'string\[\]\s+sfx\s*=\s*\{(.*?)\};', src, re.S)
    if not m:
        rep.warn('audio', pa, 1, 'preload list (string[] sfx = {...}) not found')
        return
    preload = re.findall(r'"([\w]+)"', m.group(1))
    pre_line = src.count('\n', 0, m.start()) + 1
    sfx_dir = os.path.join(AUDIO, 'SFX')
    have = set(os.path.splitext(f)[0] for f in os.listdir(sfx_dir) if not f.endswith('.meta')) if os.path.isdir(sfx_dir) else set()
    used = {}
    for path, s in code_srcs:
        body = s
        if os.path.abspath(path) == os.path.abspath(pa):
            body = s[:m.start()] + ' ' * (m.end() - m.start()) + s[m.end():]
        for mm in re.finditer(r'"(sfx_[\w]+)"', body):
            used.setdefault(mm.group(1), (path, body.count('\n', 0, mm.start()) + 1))
    pre = set(preload)
    for key, (path, line) in sorted(used.items()):
        if key in pre:
            continue
        if any(p.startswith(key + '_') and p[len(key) + 1:].isdigit() for p in pre):
            continue   # PlayVariant prefix
        if key.startswith('sfx_meow_'):
            continue   # PlayMeow loads on demand
        if key in have:
            rep.err('audio', path, line, "'%s' exists in Audio/SFX but PawAudio never preloads it: Play() is silent" % key)
        else:
            rep.err('audio', path, line, "'%s' is played but neither preloaded nor present in Audio/SFX" % key)
    used_keys = set(used)
    for key in preload:
        if key not in have:
            rep.warn('audio', pa, pre_line, "preloaded '%s' has no file in Audio/SFX" % key)
        variant_used = any(key.startswith(u + '_') and key[len(u) + 1:].isdigit() for u in used_keys)
        if key not in used_keys and not variant_used:
            rep.warn('audio', pa, pre_line, "preloaded '%s' is never played" % key)
    for key in sorted(have - pre):
        if key not in used_keys and not key.startswith('sfx_meow_'):
            rep.warn('audio', os.path.join(sfx_dir, key), 1, 'clip is neither preloaded nor played (unused file?)')


# ------------------------------------------------------------------------------------------------ main

def cs_files(paths):
    if paths:
        return [os.path.abspath(p) for p in paths if p.endswith('.cs')]
    out = []
    for r, dirs, fs in os.walk(ASSETS):
        dirs[:] = [d for d in dirs if d not in ('Plugins',) or r != ASSETS]
        for f in fs:
            if f.endswith('.cs'):
                out.append(os.path.join(r, f))
    return sorted(out)


def lint_cs(path, src, known_ns, rep):
    if not check_braces(path, lex(preprocess(src, 'if')), rep, ''):
        return
    if re.search(r'^\s*#\s*(else|elif)', src, re.M) and not check_braces(path, lex(_else_pass(src)), rep, ' (#else branches)'):
        return
    toks = lex(preprocess(src, 'if'))
    check_scopes(path, toks, rep)
    if os.sep + 'Editor' + os.sep not in path:   # editor tools are not frame-budgeted
        check_hot_allocs(path, toks, src.split('\n'), rep)
    check_usings(path, strip_comments(src), known_ns, rep)


def run(paths, rep):
    all_files = cs_files([])
    files = cs_files(paths) if paths else all_files
    known = declared_namespaces(all_files)
    for f in files:
        lint_cs(f, read(f), known, rep)
    code_srcs = [(f, strip_comments(read(f))) for f in all_files if f.startswith(CODE)]
    ui_get = re.compile(r'(?:Ui\.Get|kit\.Sprite|kit\.Panel|\.Panel)\(\s*"([\w]+)"|new PixelButton\([^,]+,[^,]+,\s*"([\w]+)",\s*"([\w]+)"')
    ui_try = re.compile(r'Ui\.TryGet\(\s*"([\w]+)"')
    fx_get = re.compile(r'\.Fx\(\s*"([\w]+)"')
    fx_try = re.compile(r'\.TryFx\(\s*"([\w]+)"')
    check_catalog('ui', 'UI', code_srcs, rep, ui_get, ui_try)
    check_catalog('fx', 'FX', code_srcs, rep, fx_get, fx_try)
    check_audio(code_srcs, rep)


SELF_TESTS = [
    ('balanced', 'class A { void F() { int x = (1 + 2); var s = "}"; var c = \'{\'; var v = @"a""}"; var i = $"{x}}}"; } }', []),
    ('unbalanced', 'class A { void F() { if (x) { } }', ['brace']),
    ('cs0136', 'class A { void F() { int i = 0; for (int k = 0; k < 2; k++) { int i = 1; } } }', ['scope']),
    ('cs0136 foreach', 'class A { void F() { var s = 1; foreach (var s in xs) { } } }', ['scope']),
    ('cs0136 param', 'class A { void F(int a) { if (a > 0) { float a = 2; } } }', ['scope']),
    ('cs0136 later outer', 'class A { void F() { { int t = 1; } int t = 2; } }', ['scope']),
    ('cs0128', 'class A { void F() { int q = 1; int q = 2; } }', ['scope']),
    ('siblings ok', 'class A { void F() { { int t = 1; } { int t = 2; } for (int i = 0; ; ) {} for (int i = 0; ; ) {} } }', []),
    ('fields ok', 'class A { int x; void F() { int x = 1; } void G() { int x = 2; } }', []),
    ('out var leaks from if', 'class A { void F() { if (d.TryGetValue(k, out var v)) { } int v = 1; } }', ['scope']),
    ('cast not a decl', 'class A { void F() { var a = (int)b; c = (float)a; Foo(x); } }', []),
    ('generic decl', 'class A { void F() { List<int> xs = null; { List<int> xs = null; } } }', ['scope']),
    ('using bad', 'using Not.A.Thing;\nclass A { }', ['using']),
    ('using ok', 'using System.Collections.Generic;\nusing UnityEngine;\nclass A { }', []),
    ('if-else branches', 'class A {\n#if X\n void F() {\n#else\n void F() {\n#endif\n } }', []),
    ('alloc warn', 'class A { void Update() { var s = "a" + b; var l = new List<int>(); var v = new Vector2(1, 2); } }', ['alloc', 'alloc']),
    ('alloc ok marker', 'class A { void Update() { var s = "a" + b; // lint: alloc-ok rare\n } }', []),
]


def self_test():
    bad = 0
    for name, src, want in SELF_TESTS:
        rep = Report()
        lint_cs('<%s>' % name, src, {'PawNoon'}, rep)
        got = sorted([e[0] for e in rep.errors] + [w[0] for w in rep.warnings])
        if got != sorted(want):
            bad += 1
            print('SELF-TEST FAIL %-24s want %s got %s %s' % (name, want, got, rep.errors + rep.warnings))
    print('self-test: %d/%d ok' % (len(SELF_TESTS) - bad, len(SELF_TESTS)))
    return 1 if bad else 0


def main(argv):
    if '--self-test' in argv:
        return self_test()
    errors_only = '--errors' in argv
    strict = '--strict' in argv
    paths = [a for a in argv if not a.startswith('--')]
    rep = Report()
    run(paths, rep)
    for kind, path, line, msg in sorted(rep.errors):
        print('ERROR   %s:%d [%s] %s' % (path, line, kind, msg))
    if not errors_only:
        for kind, path, line, msg in sorted(rep.warnings):
            print('warning %s:%d [%s] %s' % (path, line, kind, msg))
    print('check_cs: %d error(s), %d warning(s)' % (len(rep.errors), len(rep.warnings)))
    if rep.errors or (strict and rep.warnings):
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
