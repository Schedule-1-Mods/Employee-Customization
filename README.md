# Employee Customization

A MelonLoader mod for Schedule I that lets you recolour your employees.

- **Edit an employee:** look at one and press **E**. A panel on the left lets you change their hair colour and each piece of clothing (mask, hazmat suit, gloves, boots, hats and the rest) with a colour slider, brightness and a hex code.
- **Kept across saves:** the game only saves which preset look an employee has, not colours, so the mod keeps its own side file (keyed by employee) and puts your colours back after every load. It never edits your save.
- **Restore to Default:** undoes everything the mod changed on that employee.
- **Umbrellas:** every employee caught out in the rain gets an umbrella, instead of only some of them.
- **Main menu outfit:** the character on the main menu wears your whole outfit (apron, gloves and so on), not just the pieces the game's menu remembers.

## Install

1. Install MelonLoader 0.7.x for Schedule I.
2. Put `Employee Customization.dll` in the game's `Mods` folder.

## Building

`dotnet build` builds straight into the game's `Mods` folder. If the game isn't installed at
`C:\Games 2\Steam Library\steamapps\common\Schedule I`, pass your path:

```
dotnet build -p:GameDir="D:\Games\Schedule I"
```

## Compatibility

Works on Default Public (0.4.6, avatar settings) and the beta (0.4.7+, avatar objects), IL2CPP.
The main menu outfit is Default Public only.
