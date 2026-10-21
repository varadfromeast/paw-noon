#!/usr/bin/env python3
"""Lead bridge driver: submit a command file to the Unity file bridge and WAIT on real signals instead of fixed
sleeps. Runs on the machine that can see the project folder (the Mac, or the Linux VM that mounts it).

  python3 lead.py run NAME  "cmd1" "cmd2" ...   -> writes Lead/cmd/NAME.txt, polls Lead/out/NAME.out until DONE
  python3 lead.py build NAME                    -> stop, refresh, recompile; wait for compile.log to settle; summary
  python3 lead.py test NAME                     -> build + run EditMode tests; wait for tests.log; summary
  python3 lead.py shots NAME wait1 [wait2 ...]  -> play, start a seeded auto-tap duel, screenshots at the given times
  python3 lead.py check NAME [ASSEMBLY|all]     -> Roslyn compile check inside the Editor, no domain reload (~1-3 s)

Prints a compact summary: compile errors (if any), exceptions in console.log, last status line, elapsed.
Exit code 0 ok, 1 compile error, 2 tests failed, 3 timeout, 4 exceptions in play.
"""
import os, re, sys, time

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
LEAD = os.path.join(ROOT, 'Lead')
P = lambda *a: os.path.join(LEAD, *a)

def read(p):
    try: return open(p, errors='replace').read()
    except FileNotFoundError: return ''

def submit(name, cmds):
    os.makedirs(P('cmd'), exist_ok=True)
    out = P('out', name + '.out')
    if os.path.exists(out): open(out, "w").close()   # no delete permission in the VM: truncate instead
    open(P('cmd', name + '.txt'), 'w').write('\n'.join(cmds) + '\n')
    return out

def wait_done(out, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        s = read(out)
        if 'DONE' in s and 'BEGIN' in s: return s, time.time() - t0
        time.sleep(0.5)
    return read(out), -1

def wait_compile(timeout=150):
    """compile.log gets '=== compile started' then one '[X.dll] errors=' line per assembly; settled = no new line for 6 s
    after at least one errors= line, and state.txt reports compiling=False."""
    t0 = time.time(); last = ''; last_change = time.time(); seen = False
    while time.time() - t0 < timeout:
        s = read(P('compile.log')); st = read(P('state.txt'))
        if s != last: last = s; last_change = time.time()
        if 'errors=' in s: seen = True
        if seen and 'compiling=False' in st and time.time() - last_change > 6: return s
        time.sleep(1)
    return read(P('compile.log'))

def compile_summary(log):
    errs = [l.strip() for l in log.splitlines() if 'error CS' in l]
    asm = re.findall(r'\[(\S+)\] errors=(\d+) warnings=(\d+)', log)
    bad = [a for a in asm if a[1] != '0']
    return errs, asm, bad

def exceptions():
    s = read(P('console.log'))
    ex = [l for l in s.splitlines() if 'Exception' in l and 'ReplaceFile' not in l and 'FileNotFoundException' not in l]
    return ex

def cmd_build(name, also_tests=False):
    cmds = ['stop', 'wait 1', 'clearlog', 'refresh']
    out = submit(name, cmds)
    s, _ = wait_done(out, 60)
    if 'compiling=False' in s:
        out = submit(name + '_rc', ['recompile'])   # refresh queued nothing: force it
        wait_done(out, 30)
    log = wait_compile()
    errs, asm, bad = compile_summary(log)
    for a in asm: print('  %s errors=%s warnings=%s' % a)
    for e in errs[:12]: print('  ' + e)
    if bad:
        print('COMPILE FAILED'); return 1
    print('compile ok')
    if not also_tests: return 0
    out = submit(name + '_t', ['call PawNoonLead.LeadTests.Run'])
    wait_done(out, 30)
    t0 = time.time()
    while time.time() - t0 < 300:
        tl = read(P('tests.log'))
        if tl.startswith('running') or not tl: time.sleep(1); continue
        if '=== ' in tl.splitlines()[-1]: break
        time.sleep(1)
    tl = read(P('tests.log'))
    print('  ' + (tl.splitlines()[-1] if tl else 'no tests.log'))
    for l in tl.splitlines():
        if l.startswith('Failed'): print('  ' + l)
    return 0 if 'failed=0' in tl else 2

def cmd_check(name, asm='all'):
    """Roslyn in-memory compile inside the Editor (no domain reload, ~1-3 s). Prints the errors from Lead/check.log."""
    out = submit(name, ['check ' + asm])
    s, el = wait_done(out, 90)
    if el < 0: print('TIMEOUT (is Unity focused / bridge alive?)'); return 3
    res = [l for l in s.splitlines() if 'ok ' in l or 'FAILED' in l]
    for l in res: print('  ' + l[:400])
    log = read(P('check.log'))
    for l in log.splitlines():
        if '): CS' in l: print('  ' + l[:300])
    print('check %s in %.1fs' % ('ok' if not any('FAILED' in l for l in res) else 'FAILED', el))
    return 0 if not any('FAILED' in l for l in res) else 1

def cmd_run(name, cmds, timeout=180):
    out = submit(name, cmds)
    s, el = wait_done(out, timeout)
    tail = [l for l in s.splitlines() if ' -> ' in l or 'error' in l.lower()]
    for l in tail[-8:]: print('  ' + l[:300])
    ex = exceptions()
    if ex: print('EXCEPTIONS: %d (first: %s)' % (len(ex), ex[0][:200]))
    if el < 0: print('TIMEOUT'); return 3
    print('done in %.0fs' % el)
    return 4 if ex else 0

def cmd_shots(name, waits, seed='2024', tap='240', w='1179', h='2556'):
    cmds = ['play', 'wait 5', 'unpause', 'focusgame', 'gamesize %s %s' % (w, h), 'wait 1', 'clearlog',
            'call PawNoon.Runtime.ReviewDirector.SetIntro off', 'call PawNoon.Runtime.ReviewDirector.Seed ' + seed,
            'call PawNoon.Runtime.ReviewDirector.SetAutoTap ' + tap, 'call PawNoon.Runtime.ReviewDirector.StartDuel']
    for i, wt in enumerate(waits):
        cmds += ['wait ' + wt, 'shot Lead/shots/%s_%d.png' % (name, i)]
    cmds += ['call PawNoon.Runtime.ReviewDirector.Status']
    return cmd_run(name, cmds)

if __name__ == '__main__':
    a = sys.argv[1:]
    if not a: print(__doc__); sys.exit(2)
    if a[0] == 'build': sys.exit(cmd_build(a[1]))
    if a[0] == 'test': sys.exit(cmd_build(a[1], True))
    if a[0] == 'check': sys.exit(cmd_check(a[1], a[2] if len(a) > 2 else 'all'))
    if a[0] == 'run': sys.exit(cmd_run(a[1], a[2:]))
    if a[0] == 'shots': sys.exit(cmd_shots(a[1], a[2:]))
    print(__doc__); sys.exit(2)
