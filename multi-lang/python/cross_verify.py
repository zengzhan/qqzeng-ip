"""Cross-verify all language implementations against the Python reference."""
import random, sys, os, subprocess

sys.path.insert(0, os.path.dirname(__file__))
from qzdb import QzdbSearcher

DB = os.path.join(os.path.dirname(__file__), '..', 'data', 'qqzeng_ip_max_china.qzdb')
s = QzdbSearcher(DB)

def test_language(lang, ip_str):
    runners = {
        'node': ['node', '-e', f'''
            const Q = require("{os.path.dirname(__file__)}/../nodejs/qzdb");
            const s = new Q("{DB}");
            console.log(s.findStr("{ip_str}"));
        '''],
        'php': ['php', '-r', f'''
            require "{os.path.dirname(__file__)}/../php/QzdbSearcher.php";
            $s = new Qqzeng\\Ip\\QzdbSearcher("{DB}");
            echo $s->findStr("{ip_str}");
        '''],
    }
    try:
        r = subprocess.run(runners[lang], capture_output=True, text=True, timeout=30,
                          cwd=os.path.dirname(__file__)+'/..')
        return r.stdout.strip()
    except:
        return None

tests = ['114.114.114.114', '223.5.5.5', '1.2.4.8', '180.168.4.100', '8.8.8.8']
print('=== Cross-verify ===')
for lang in ['node', 'php']:
    ok = 0
    for ip in tests:
        expected = s.find_str(ip)
        got = test_language(lang, ip)
        if got == expected:
            ok += 1
        else:
            print(f'  {lang} MISMATCH {ip}: expected="{expected[:50]}..." got="{got[:50] if got else "None"}..."')
    print(f'{lang}: {ok}/{len(tests)} ✓')
