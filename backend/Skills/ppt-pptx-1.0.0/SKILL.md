---
name: PPT / Pptx
slug: ppt-pptx
version: 1.0.1
description: Read PowerPoint presentation slides (file path or current document). Use correct slide order via SlideIdList; support .pptx and .pptm.
metadata: {"clawdbot":{"emoji":"📊","os":["linux","darwin","win32"]}}
---

## When to Use

User needs to read or list slides from a PowerPoint file (.pptx, .pptm) or from the **current** presentation when in Office PowerPoint or WPS 演示 task pane. Use file-path tools (Ppt plugin) in Chrome; use CurrentDocument current_ppt_* when in PowerPoint/WPS 演示.

## Structure

- PPTX is a ZIP containing XML; slide order is defined by **SlideIdList** in the presentation part—do not enumerate SlideParts directly or order may be wrong.
- Each slide has CommonSlideData with shapes; text is in `a:t` (Drawing namespace) under shapes. Extract text via `Slide.Descendants<Drawing.Text>()` or equivalent.
- Slide index in tools is **1-based** and follows playback order (SlideIdList order).

## File vs Current Document

| Client        | List/read slides from file     | List/read current presentation   |
|---------------|--------------------------------|-----------------------------------|
| Chrome        | `ppt_slides_list`, `ppt_slide_read` (file path) | Not available                     |
| Office PPT    | Not exposed                    | `current_ppt_slides_list`, `current_ppt_slide_read` |
| WPS 演示      | Not exposed                    | Same current_ppt_* RPC            |

## Format Limits

- **PPTX / PPTM**: Open XML format; supported. Use `.pptx` or `.pptm` only.
- **PPT**: Legacy binary format; not supported. Return a clear error asking user to save as .pptx/.pptm.

## Common Pitfalls

- Assuming slide order from part enumeration—always use SlideIdList order.
- Using 0-based slide index—tools use 1-based (slide 1 = first slide).
- In task pane, current document is the open presentation; no file path is passed.
