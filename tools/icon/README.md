# Plugin icon generator

`make_icon.py` draws the plugin icon: a terminal whose dark screen shows a shell prompt and a
glowing line of readings, on a graphite tile with a soft top light. It shares the shading and
accent of the other plugin icons, but neither the chip nor the night blue tile of the HWiNFO
icon, so the two are easy to tell apart. The icon is original artwork, no third-party source;
neither Tux nor any other Linux logo is used.

```bash
pip install pillow numpy
python tools/icon/make_icon.py tools/icon/out
cp tools/icon/out/icon_256.png icon.png
```

`BG_HUE` at the top of the script sets the background tint; `HISTORY` sets the line of readings.

The script writes `icon_{256,128,64,32,16}.png` into the given folder; only the 256 px file is
used, as `icon.png` in the repo root. The `out/` folder is not committed.
