# brief-em3d-61 Q12 -- process start plus library load: `occt_probe noop`, median of 10 (after one warm-up).
# SPIKE MATERIAL. Usage: python3 q12/process-start.py <path-to-occt_probe>
import subprocess, sys, time, statistics
p = sys.argv[1]
subprocess.run([p, 'noop'], capture_output=True)            # warm the file cache
cold = []
for _ in range(10):
    t = time.perf_counter(); subprocess.run([p, 'noop'], capture_output=True); cold.append((time.perf_counter() - t) * 1000)
st = []
for _ in range(10):
    t = time.perf_counter(); subprocess.run([p, 'selftest'], capture_output=True); st.append((time.perf_counter() - t) * 1000)
print('noop (process start + load of 25 OCCT libraries): median %.1f ms (min %.1f, max %.1f)' % (statistics.median(cold), min(cold), max(cold)))
print('selftest (start + boolean + fillet + STEP write/read in memory): median %.1f ms' % statistics.median(st))
