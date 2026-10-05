# Linux: RAPL and amdgpu

**Question.** Which Linux power interfaces can a normal user read?

| Interface | Path | Readable without root |
| --- | --- | --- |
| Battery | `/sys/class/power_supply/*/power_now`, or `current_now` × `voltage_now` | Yes |
| CPU package (Intel and AMD RAPL) | `/sys/class/powercap/intel-rapl:*/energy_uj` | **No**, on most distributions since kernel 5.10 |
| AMD GPU | `/sys/class/drm/card*/device/hwmon/hwmon*/power1_average` or `power1_input` (µW), `gpu_busy_percent` | Yes |
| NVIDIA GPU | NVML (`libnvidia-ml.so.1`) | Yes |
| CPU load | `/proc/stat` | Yes |

**Why RAPL closed.** The Platypus attack (2020) showed that energy counters fine enough to
measure CPU power can also leak secrets, such as AES keys from inside SGX enclaves. Kernel 5.10
made `energy_uj` readable by root only. The meter still lists the RAPL sensor. It reports
unavailable unless an admin has opened the files (README, "What elevated rights would buy").

**amdgpu on an APU.** On an AMD APU the GPU's hwmon power is the whole SoC (CPU and GPU), so the
reading counts as the CPU part, not the GPU, and is not added twice.

**Virtual machines.** On a cloud VM (2 vCPUs on an AMD EPYC host) every sensor correctly reported
unavailable. Without a VM check, the model charged the VM a full 150 W server CPU. The model now
detects a VM (hypervisor flag, DMI vendor) and caps the CPU at 5 W per vCPU. A VM's figure is a
placeholder: the host's draw is hidden from it.

**Unity's Linux player** crashed at start (`PlayerMain`) on that VM, as did an empty Unity
project, so on Linux the core is verified through `watts-probe` rather than a player.

## Open

No Linux laptop or desktop with real sensors has been tested yet.
