# Research notes

What was found while building Watts, with the evidence behind each choice. Each note says what
the question was, what was tried, what the numbers were and what was decided. This is work in
progress: most findings rest on one or two machines, and most notes end with what is still open.
The raw reports and scripts are in [eval](../../eval).

| Note | In one line |
| --- | --- |
| [Design constraints](design.md) | Why the code is pure C#, never asks for admin, never touches the network, and always returns a number. |
| [Apple Silicon: SMC `PSTR`](apple-smc-pstr.md) | The Mac's whole-system power reads without sudo, follows load within two seconds, and leaves out battery charging. |
| [Windows: the Energy Meter Interface](windows-emi.md) | EMI gave live CPU package power without admin on an AMD laptop, and refused on an Intel desktop. Package channels must not be added to rail channels. |
| [Windows: battery figures](windows-battery.md) | The power manager's figure can freeze for 20 s or more, the battery driver's is fresher, and both lag a load change by 30 s or more. |
| [GPU load is not GPU power](gpu-load-is-not-gpu-power.md) | An idle card can be 50% busy at its lowest clock. Hybrid laptops power the card off. Load has to come from the adapter the game renders on. |
| [Linux: RAPL and amdgpu](linux.md) | CPU power is root-only since kernel 5.10. amdgpu reports GPU power to anyone. |
| [The model and the supply factor](model.md) | Where the estimate's numbers come from, and which of them are still assumptions. |
