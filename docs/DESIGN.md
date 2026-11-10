# Pick-It product and design brief

## Product intent

Pick-It is a small, focused colour picker for Revit. It belongs to the same Designs/OS add-in family as Gantt-It.

The initial interaction is deliberately narrow:

1. The user clicks the **Pick-It** button on Revit's built-in **Add-Ins** tab.
2. The user picks a colour.
3. The ribbon button changes its icon to show the picked colour and its RGB values.

The initial scope does not include a broader colour-management system or unrelated Revit editing features.

## Agreed behaviour

**Plain click: pick.** The eyedropper works anywhere on screen. The screen is frozen into a snapshot shown full screen on every monitor, with a magnifier and RGB readout; left click picks, Esc or right click cancels and keeps the previous colour. The mechanism is ported from Gantt-It's "Pick from screen" (`src/GanttIt.Revit/UI/Swatches/ScreenColorPicker.cs`, `NativeScreen.cs`, `ScreenPickOverlay.xaml(.cs)`). Picking from a shaded Revit view gives the displayed colour, not the material colour. The button works with no document open.

**Icon.** After a pick the 32 px icon is filled with the colour and shows the R, G and B values stacked, in black or white by contrast; the 16 px icon shows the colour only. The tooltip reads `RGB r, g, b · #HEX`. The colour stays on the icon until it is cleared or another colour is picked, across Revit sessions. With no colour, the idle icon follows Revit's light or dark theme and switches live.

**Shift-click: menu at the pointer.** A small popup in Revit's light or dark theme, headed by the current colour. Esc, a click outside, or switching away closes it; arrows, Tab, Enter and Space work it from the keyboard.

- **Recent colours**: the last 10 picks, newest first, in 2 rows of 5; empty cells are shown as quiet placeholders. Every pick is remembered automatically; picking a colour already in the list moves it to the front. Clicking a swatch makes it the current colour and closes the menu. Tooltips read `RGB r, g, b · #HEX`.
- **Clear**: resets the icon only; recent colours stay. **Clear history** empties the recent colours.
- **Add to palette…**: switches the popup to the 16 custom-colour slots of Revit's Color dialog, laid out as that dialog shows them: 2 rows of 8, slot = row × 8 + column. Clicking a slot writes the current colour into it; an occupied slot is replaced without confirmation. The popup reports the result: on success the slot shows the colour and the menu closes after a moment, on failure it stays with the reason.
  - Revit has no API for the custom colours, so the write drives Revit's own Color dialog (`ColorSelectionDialog`, the Windows common colour dialog) out of sight: select the slot, set R, G and B, press Add, press OK. The palette lasts for the session and Revit saves it to Revit.ini on a clean exit. If the dialog can't be recognised or doesn't respond it is cancelled and the write is reported as failed; it is never left open unseen.
  - Revit can't be asked what the slots hold either. The grid shows the `[Colors]` `CustomColor1..16` values read from the user's Revit.ini at startup, updated with Pick-It's own writes. A slot with no value there is drawn as unknown ("?"), not guessed; colours the user changes in Revit's dialog during the session aren't seen until the next start. Pick-It never writes Revit.ini.
- Items that need a colour are disabled when none is picked.

**Not included.** No clipboard copy: Revit's colour dialog takes separate R, G and B fields, so there is nothing to paste into.

**Storage.** Current colour and recent colours live in a JSON file under `%APPDATA%\Designs-OS\PickIt`.

**Versions.** Revit 2025, 2026 and 2027. 2026 and 2027 are tested live; 2025 shares 2026's build target and is compile-checked.

## Family design language

Pick-It should reuse Gantt-It's visual conventions without taking on Gantt-It's feature complexity:

- Follow Revit's light and dark UI themes, including ribbon icon variants.
- Use neutral window and surface colours, restrained borders, and the family blue accent.
- Use Segoe UI for window text and Segoe MDL2 Assets for interface glyphs.
- Keep windows compact and information-dense, with clear headings and muted secondary text.
- Use flat controls, one-pixel borders, three-pixel control corner radii, and four-pixel panel corner radii.
- Avoid gradients, heavy shadows, decorative illustration, and unnecessary chrome.

### Core palette

| Role | Light theme | Dark theme |
|---|---|---|
| Window | `#F4F5F7` | `#1F2329` |
| Surface | `#FFFFFF` | `#2A2F37` |
| Alternate surface | `#F9FAFB` | `#252A31` |
| Border | `#D3D7DE` | `#525B67` |
| Primary text | `#1F2328` | `#E6E9ED` |
| Muted text | `#5E6672` | `#AEB6C1` |
| Accent fill | `#2F6AD0` | `#2F6AD0` |
| Accent foreground | `#2F6AD0` | `#79AEFF` |
| Hover | `#EEF0F3` | `#353B44` |
| Pressed | `#E2E5EA` | `#3E454F` |
| Divider | `#E6E8EC` | `#3A414B` |
| Error text | `#C42B1C` | `#FF8F85` |

Body text and essential iconography should retain WCAG AA contrast. A dynamic picked-colour icon needs a contrasting RGB text colour rather than assuming that dark or light text always works.

### Ribbon icons

Provide 16×16 and 32×32 assets for light and dark Revit themes. The idle icon should follow the family style: a full-bleed rounded tile with transparent anti-aliased corners and a simple flat glyph, without outlines, gradients, or decorative text.

- Light tile: `#D9D9D9`; light glyph: `#4F8EF7`.
- Dark tile: `#2A303A`; dark glyph: `#6FA3FA`.

After a pick, the sampled colour and RGB readout become the icon's primary content. The exact layout is an implementation design task, but the RGB values must remain part of the icon as requested.

## References

The source conventions come from Gantt-It:

- [Window theme and control styles](https://github.com/jedbjorn/gantt-it/blob/main/src/GanttIt.Revit/UI/Theme.xaml)
- [Light and dark palette values](https://github.com/jedbjorn/gantt-it/blob/main/src/GanttIt.Core/Theming/UiPalette.cs)
- [Ribbon icon style](https://github.com/jedbjorn/gantt-it/blob/main/tools/make_icons.py)

Implementation should mirror these conventions within Pick-It, not reference files in a sibling checkout.
