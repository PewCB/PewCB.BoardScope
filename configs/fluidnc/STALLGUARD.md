# Stage configuration and calibration record

The stage uses two TMC2209 drivers on UART1 (TX GPIO1 through 1 kohm, RX GPIO2 directly to the bus), addresses 0 and 1. GPIO8 is the shared active-high disable signal. Physical motor 1 uses STEP10, DIR6 inverted, DIAG9, address 0; physical motor 2 uses STEP5, DIR4 inverted, DIAG7, address 1. Cables have not been swapped.

The current configuration assigns physical motor 2 to the logical X motor and physical motor 1 to the logical Y motor. On CoreXY this reverses Cartesian Y and preserves Cartesian X. Positive X moves right and positive Y moves up. Both axes home in their negative directions: X left, Y down. The user confirmed the new Y direction with a 2 mm up-and-back test.

Both configurations use `idle_ms: 255`, 0.5 A RMS running current, 0.25 A holding current, 16 microsteps and 80 steps/mm. The idle setting has been saved on the controller.

`config.yaml` is the active configuration. Both driver thresholds are 35, pull-off is 2 mm, and homing uses one approach at 600 mm/min with 0.25 A RMS homing current. Running current remains 0.5 A RMS and holding current 0.25 A RMS. Soft limits are enabled in machine coordinates: X 0-210 mm and Y 0-130 mm, with negative-direction homing establishing zero. `max_travel_mm` is 210 for X and 130 for Y. X `seek_scaler` is 1.1 and Y is 2.0769231, allowing maximum homing searches of 231 mm and approximately 270 mm respectively. Homing is required after restart (`start.must_home: true`); use `$H` to establish both axes before normal movement. Diagnostic logging is off.

With the mechanical fix and the updated order requested on 2026-10-09, run normal two-axis homing with **`$H`**:

1. Home Y down.
2. Home X left, without an intermediate clearance move.

Y uses homing cycle 1 and X uses cycle 2. Individual `$HX`/`$HY` remain enabled and may be used in either order; the user reports the mechanics now allow this. The optional **`$RM=0`** macro runs `$H`, then parks in machine coordinates at X100 Y50 at 600 mm/min. Plain `$H` finishes at the home position without the parking move. Both axes still home separately.

`stallguard-x-test.yaml` retains the older, temporary X-only configuration. Controller read-backs, `HOMING-INVESTIGATION.md` and the `output/` logs mentioned below are local investigation records excluded from Git. `controller-loaded-config.yaml` records the most recent configuration read back after loading. Older read-backs and test files must not be substituted for `config.yaml`.

## Measurements on 2026-10-05

- At 300 mm/min, motor 1 load readings varied considerably (12-144 across the four baseline strokes).
- At 900 mm/min, the user considered movement too fast.
- After manual positioning near the left stop, the 600 mm/min X baseline returned to its start. Motor 1 readings were 142-156 moving right and 146-152 moving left.
- X homing with threshold 60 reported success and pulled off, but the user confirmed it stopped before reaching the mechanical stop. This was a false trigger, not a successful home calibration.
- Threshold 40 also reported success but triggered before contact. The user observed it stopped closer to the stop than threshold 60.
- Threshold 20 produced a reported successful home, and the user confirmed physical contact with the left stop followed by a clean pull-off. The maximum seek distance has been reduced from 8 mm to 5 mm. Repeatability is not yet established.
- The user declined a proposed three-repeat test, then requested threshold 30, a 280 mm X travel setting and a homing test from 100 mm away.
- At threshold 30, X moved +100 mm at 600 mm/min, then homed left. It detected the stop after approximately 102.225 mm of commanded travel, pulled off 2 mm and returned to Idle with `Homed:X`, without an alarm. The user confirmed clean physical contact and pull-off. The 110 mm temporary search cap was then replaced with the 280 mm travel setting, and the final configuration was saved/read back over USB.
- Both driver thresholds were changed to 35, Y was reversed by swapping the complete logical motor assignments, and each maximum homing search was set to 250 mm. The saved macro implements X home, X100 clearance, Y home, then X100/Y50.
- The full native `$RM=0` routine completed: `Homed:X` was reported, the clearance move reached X100/Y0, `Homed:Y` was reported, and the controller finished Idle at X100/Y50. The user confirmed everything went well. Test log: `output/full-home-routine-20261005-212922.txt`.
- An interim progress report was mistakenly treated as a missed X stop. A soft reset was sent after the routine had already completed; the final position remained X100/Y50. Motor holding and the saved configuration were checked afterward. No additional movement was commanded.

Threshold 35 on both drivers has passed one complete observed routine with the current motor assignments. Repeated homing and a search starting 250 mm from the stops have not been tested. Keep speed, motor current and mechanical load consistent while tuning. A reported `Homed:X` alone cannot distinguish a true stop contact from a false trigger.

## Soft limits requested on 2026-10-08

The usable machine ranges were changed to X 0-210 mm and Y 0-130 mm. Homing searches now have a 10% margin (231 mm and 143 mm) instead of the previous fixed 250 mm cap. This configures sufficient search distance from either maximum position; no far-end homing test or boundary motion test has been performed for this change. FluidNC allows approximately one step of tolerance at the software boundaries. Both normal `$H` and the optional `$RM=0` parking macro remain available; X100/Y50 is inside the new limits.

The file was uploaded to COM11, verified against the local source and loaded after restart. The active dump confirms both soft limits, axis ranges, seek scalers, home-zero settings, cycles 1/2, threshold 35, idle_ms 255 and the parking macro. Both driver checks passed. Startup now reports the expected homing-required alarm (ALARM:14); use `$H` to establish position before normal moves. No movement was commanded during this update, and COM11 was released.

During CoreXY homing, FluidNC status reports motor coordinates; equal X/Y values in a `Home` report do not mean the stage moved diagonally. Idle/Jog reports use Cartesian coordinates.

## Configuration repair on 2026-10-09

The controller's saved `config.yaml` lacked the root `uart1` and `macros` sections and the `stepping.idle_ms` and `stepping.pulse_us` settings. Startup failed with `TMC Driver missing uart1 section`. Both saved homing currents were still 0.5 A. The file was captured as `output/controller-config-before-repair-20261009.yaml`, then the missing settings were restored and both homing currents set to the user's requested 0.25 A. The controller's existing `r_sense_ohms: 0.1` was preserved on both drivers; the older calibration used a configured 0.11 ohm value.

The repaired file was uploaded to COM11 and verified against the local source. After restart, both driver checks passed and the active dump confirmed UART1, homing currents, soft limits, search margins, motor holding and macro. The expected homing-required alarm remained; no motion was commanded. Sensitivity 35 has not been motion-tested at 0.25 A homing current or the updated sense-resistor setting, and may require retuning.

The selected startup file is `config.yaml`. On 2026-10-05, the controller was restarted to load the new motor assignments and Y homing section, and the full routine then established both updated coordinate references. On 2026-10-08, the revised native `$H` configuration was uploaded to COM11, verified against the local file, selected and loaded after restart. Both driver tests passed; the loaded cycles and macro were checked and the controller reported Idle. No movement was commanded, so the revised homing sequence has not been motion-tested. `controller-saved-config.yaml` remains the older 2026-10-05 read-back; use `controller-loaded-config.yaml` for the latest loaded settings. No firmware reflash was required.

## Firmware cleanup and repeated homing verified on 2026-10-09

Firmware `FluidNC v4.1.1 (codex/corexy-homing-cleanup-daccb8a5-dirty)` was built and flashed at application offset 0x10000, with checksum verification. The filesystem and UI were preserved. The patch refreshes both coupled CoreXY limit inputs and clears TCOOLTHRS when returning a TMC2209 to ordinary StealthChop; see `HOMING-INVESTIGATION.md` for the observed stale-limit failure and source changes.

The active configuration was uploaded, read back and verified: Y cycle 1, X cycle 2, Y search approximately 270 mm, X search 231 mm, unchanged soft bounds X 0-210 / Y 0-130, threshold 35, homing current 0.25 A, and configured sense resistance 0.1 ohm. Both startup driver checks passed and stationary status showed no latched limit inputs.

Two consecutive watched `$H` cycles then completed in Y-before-X order, each finishing Idle at machine X0/Y0 without an alarm, reboot or saved-file change. The user confirmed both axes physically contacted their correct stops and pulled off cleanly in both cycles. Log: `output/repeat-homing-20261009-141932.txt`. These were near-home repeat tests; the complete 270 mm search allowance and far-end homing have not been exercised. COM11 was released after testing. No pull request was created or reopened.

Reference: [TMC2209 datasheet, StallGuard4](https://www.analog.com/media/en/technical-documentation/data-sheets/TMC2209_datasheet_rev1.09.pdf).
