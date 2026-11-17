# fine-grain-volume

FineVolume gives Windows 11 volume steps of 1 at low volume. Windows normally moves the volume in steps of 2, so a keyboard volume knob or mouse volume control can never land on 1, 3, 5 and so on.

- Below a threshold (20 by default) each notch or key press changes the volume by 1.
- Above the threshold Windows' normal step of 2 is left alone.
- The normal Windows volume popup still appears and shows the right number.

It works with anything that sends the standard volume up / volume down keys: keyboard volume keys, a keyboard volume knob, or a mouse volume control.

## Build

No downloads are needed. The C# compiler that ships with Windows is enough. From the folder containing `FineVolume.cs`, run in PowerShell:

```powershell
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:FineVolume.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll FineVolume.cs
```

This produces `FineVolume.exe`.

## Run

```
FineVolume.exe [threshold] [fineStep]
```

| Argument    | Default | Meaning                                         |
|-------------|---------|-------------------------------------------------|
| `threshold` | 20      | Volume below which the fine step is used        |
| `fineStep`  | 1       | Size of each step below the threshold           |

Run it with no arguments for steps of 1 below volume 20.

FineVolume has no window. It shows an icon in the system tray; right-click it and choose **Exit FineVolume** to stop it. Only one copy runs at a time.

## Run at startup

1. Press `Win + R`, type `shell:startup` and press Enter.
2. Put a shortcut to `FineVolume.exe` in the folder that opens.

## Notes

- Tested on Windows 11 with a USB audio device. It follows whichever output device is the Windows default.
- When the volume goes from 0 to 1 while the popup is showing, the popup may briefly show 2 first. Leaving 0 always goes through Windows' own step, because on the test device setting the volume straight from 0 to 1 left the output silent.
