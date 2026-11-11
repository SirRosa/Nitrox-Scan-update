# Nitrox Scanner Room Upgrade Restore — test build

Minimal binary patch for the official **Nitrox 1.8.1.0 Windows x64** release.

## Purpose
Fixes a confirmed problem where Scanner Room upgrade chips are serialized on the server but are not respawned on a reconnect. The patch changes only `NitroxClient.dll`. It wraps the existing `RestoreMapRoom` coroutine and runs the original `Entities.SpawnBatchAsync` path on its `InventoryItemEntity` children after the room has been restored.

This is an **experimental** patch. It addresses Scanner Room upgrades only, not vehicle modules or other possible persistence problems.

## Build
See [Actions → Build Nitrox scanner fix](../../actions/workflows/build-scanner-fix.yml).
The workflow downloads the **official** `Nitrox_1.8.1.0_win_x64.zip`, verifies its SHA-256, modifies only the target assembly using dnlib, validates the result, then uploads a Windows test package. No Subnautica game binaries are downloaded or redistributed.

## Installation
1. Back up your Nitrox server save **and** your existing Nitrox launcher directory.
2. Download the latest successful workflow artifact.
3. Extract it to a **separate** folder (do not overwrite the original).
4. Start the patched Nitrox launcher from the extracted folder. Keep server and clients on a matching build for testing.
5. Insert Scanner Room upgrades, save/exit, restart the server, reconnect, and check both the chips and scanner functionality. Also verify a reconnect without restarting the server.

## Rollback
Delete the extracted experimental directory and launch official Nitrox 1.8.1.0. Restore server save backup if testing modified it.

## Source and credits
Original project: https://github.com/SubnauticaNitrox/Nitrox — GPL-3.0.
Related issue: https://github.com/SubnauticaNitrox/Nitrox/issues/2696.
Five-line source-level proposed fix: https://github.com/SubnauticaNitrox/Nitrox/commit/72f685491a097bd5d24f02f2eba55fe051ceb444.

The binary patch reuses the original 1.8.1.0 code path without rebuilding the upstream projects that require locally installed Subnautica assemblies. This repository includes tooling and provenance; the workflow artifact contains a patched copy of the official release for testing.
