"""Print new in-game chat lines (sandbox client) until the deadline.   HUBNER_URL=http://127.0.0.1:8731/mcp python3 chat_watch.py [minutes=60]"""
import sys, time
from hubner import *
end = time.time() + 60 * float(sys.argv[1] if len(sys.argv) > 1 else 60); last = 0
print('chat watch started', flush=True)
while time.time() < end:
    try:
        r = tool('chat_tail', since=last)
        for m in r['messages']: print(f"{m['time']} [{m['type']}] {m['name']}: {m['text']}", flush=True)
        last = r['last']
    except Exception as e:
        print('chat watch error:', str(e)[:100], flush=True); time.sleep(20)
    time.sleep(3)
print('chat watch ended', flush=True)
