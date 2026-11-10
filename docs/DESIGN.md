# Pick-It product and design brief

## Product intent

Pick-It is a small, focused colour picker for Revit. It belongs to the same Designs/OS add-in family as Gantt-It.

The initial interaction is deliberately narrow:

1. The user clicks the **Pick-It** button on Revit's built-in **Add-Ins** tab.
2. The user picks a colour.
3. The ribbon button changes its icon to show the picked colour and its RGB values.

The initial scope does not include a broader colour-management system or unrelated Revit editing features.

Implementation still needs to settle the picking mechanism, the legible RGB layout at ribbon-icon sizes, and whether the picked colour persists beyond the current Revit session. These are intentionally not decided by the scaffold.

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
