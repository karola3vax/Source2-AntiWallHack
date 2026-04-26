# LOS Point Editor

Static Three.js editor for tuning the 19 S2AC/S2FOW LOS points against the CT SAS blue model in the `tools_preview_sas blue` pose.

Serve the repository root, then open:

```text
http://localhost:8000/tools/los-point-editor/
```

The editor loads:

- `ct sas/source/sas blue.glb`
- `tools/sas_blue_tools_preview_los_points.json`
- `tools/cs2_player_hitboxes_canonical.json`

After tuning, use **Copy JSON** or **Download JSON** to update `tools/sas_blue_tools_preview_los_points.json`, then run:

```powershell
python .\tools\apply_los_points_to_layout.py
dotnet build .\S2FOW.sln -c Release
```

The JSON uses Source-style local units:

- `X`: forward/back
- `Y`: left/right
- `Z`: up

Use **Automatic GLB Match** to generate a first pass from the GLB skeleton by transforming the canonical T-pose midpoint for each named bone into the `tools_preview_sas blue` pose. Review and adjust the result manually before exporting.

Use **Add Dot** to create extra custom LOS points. Custom dots are included in JSON/C# exports and can be removed with **Delete Dot** while selected.

Open **Calibration** and adjust **Opacity** when you need the dots visible through or around the model.
