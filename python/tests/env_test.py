"""time_set / weather_set / env_release round trip on the sandbox client (visual override only)."""
import sys, time; sys.path.insert(0, '..')
from hubner import *
ok = True
def check(n, c, x=''):
    global ok; ok &= bool(c); print('OK  ' if c else 'FAIL', n, x)
before = tool('env_state')
tool('time_set', t='night'); time.sleep(2); s = tool('env_state'); check('night held', s['timeHeld'] and s['isNight'], s['dayFraction'])
tool('time_set', t='noon'); time.sleep(2); s = tool('env_state'); check('noon held', s['isDay'] and abs(s['dayFraction'] - 0.5) < 0.02, s['dayFraction'])
tool('weather_set', name='Clear'); s = tool('env_state'); check('weather forced', s['weatherForced'] == 'Clear')
tool('env_release'); time.sleep(2); s = tool('env_state'); check('released', not s['timeHeld'] and s['weatherForced'] == '' and s['autoReleaseSeconds'] is None)
print('ALL OK' if ok else 'FAILURES')
