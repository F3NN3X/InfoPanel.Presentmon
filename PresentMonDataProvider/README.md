# PresentMonDataProvider quick start

The strings dump exposed the exact switches that `PresentMonDataProvider.exe` passes into the standalone PresentMon console. The new helper script `run-presentmon-session.ps1` lets you exercise the same flow without needing the MFC wrapper.

## Launching a capture

```powershell
# Capture 20 seconds of frame metrics for the newest Battlefield process
pwsh ./run-presentmon-session.ps1 -ProcessName bf2042 -DurationSeconds 20 -Verbose

# Target a process by pid, save a CSV next to the script, append future captures
pwsh ./run-presentmon-session.ps1 -ProcessId 12345 -DurationSeconds 10 -OutputFile sample.csv -AppendCsv
```

What happens behind the scenes:

1. Terminates any leftover `PresentMonDataProvider` session (`--terminate_existing_session`).
2. Re-launches `PresentMon-2.3.1-x64.exe` with the arguments we discovered:
   - `--session_name PresentMonDataProvider`
   - `--stop_existing_session`
   - `--process_id <pid>`
  - `--output_stdout --qpc_time --no_console_stats`
  - optional `--no_track_gpu`, `--no_track_input`, `--output_file`, and `--csv_append`
   - `--timed <seconds>` so the run auto-finishes.
3. Streams stdout back to the console and leaves the CSV (if requested).

Tip: make sure the script runs elevated when the game requires ETW privileges.

## Verifying data flow into InfoPanel

1. Run the helper against a simple fullscreen app first (e.g. `-ProcessName notepad`). You should see frame rows in stdout and, if you pointed at an output file, a CSV updating in place.
2. Launch InfoPanel with the PresentMon plugin enabled. The bridge can now be pointed at the same CSV or stdout channel for quick smoke tests.
3. For games that hide their ETW session (Battlefield titles), keep the helper running and attach the bridge service to the spawned session name `PresentMonDataProvider`. The metrics should surface through `PMDPSharedMemory` just like the original wrapper.
4. If stdout is still empty, fall back to the CSV output and load the file in `PresentMonService` to confirm values travel through the pipeline.

### Alternative checks

- Nuke stale sessions without starting a capture (run from this folder):
  ```powershell
  pwsh -NoProfile -Command "& './PresentMon-2.3.1-x64.exe' --session_name PresentMonDataProvider --terminate_existing_session"
  ```
- Invoke the standalone binary directly with the same argument list to compare behavior against the helper script.
- Use `PresentMonDataProvider.strings.txt` as a reference when new switches land in future builds.

With the launch arguments codified and a repeatable script in place, you can iterate on InfoPanel integration without reverse engineering the MFC front-end each time.
