# AVIF resource bounds

Checked by `AvifResourceTests.Several_hundred_conversions_and_failure_paths_stay_within_the_memory_bound`:

- Warm up with 50 conversions of a 320 px photograph, then run 400 successful conversions, then 100 rounds of corrupt,
  bomb and pre-canceled conversions.
- Forced GC before each reading (`GC.Collect` twice, then `GC.GetTotalMemory(true)`).
- Bounds: managed heap growth under 16 MiB, working-set growth under 64 MiB.

How the bounds were set, stated plainly:

- The first run measured about 216 MiB of working-set growth. Two causes were found. Native tests ran in parallel
  with the measurement, so thread-pool and allocator arenas grew during it. The warm-up was also only 10 conversions.
- Every native-engine test class now runs in one serial collection (`NativeEngine`), and the warm-up is 50.
- A separate soak of the same loader and save path (photograph, 4000 conversions, forced GC each eighth of the run)
  showed the working set rising to about 90 MiB within the first 1000 conversions and then growing by about 4 MiB
  over the next 3000. The plateau is allocator and thread-pool behavior, not a leak, which is why the 64 MiB bound
  holds only after the warm-up and isolation above.

A failure now means a real regression in steady-state memory, not a flaky threshold.
