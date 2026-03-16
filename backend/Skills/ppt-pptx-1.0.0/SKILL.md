---
name: PPT / Pptx
slug: ppt-pptx
version: 1.0.1
description: Read and write PowerPoint presentation slides (file path or current document). List, read, write title/body, insert, and delete slides. Use correct slide order via SlideIdList; support .pptx and .pptm.
metadata: {"clawdbot":{"emoji":"📊","os":["linux","darwin","win32"]}}
---

## When to Use

User needs to read, write, insert, or delete slides in a PowerPoint file (.pptx, .pptm) or in the **current** presentation when in Office PowerPoint or WPS 演示 task pane. Use file-path tools (Ppt plugin) in Chrome; use CurrentDocument current_ppt_* when in PowerPoint/WPS 演示.

## Structure

- PPTX is a ZIP containing XML; slide order is defined by **SlideIdList** in the presentation part—do not enumerate SlideParts directly or order may be wrong.
- Each slide has CommonSlideData with shapes; text is in `a:t` (Drawing namespace) under shapes. Extract text via `Slide.Descendants<Drawing.Text>()` or equivalent.
- Slide index in tools is **1-based** and follows playback order (SlideIdList order).

## File vs Current Document

| Client        | File path (Chrome/backend)     | Current presentation (task pane) |
|---------------|--------------------------------|-----------------------------------|
| Chrome        | `ppt_slides_list`, `ppt_slide_read`, `ppt_slide_write`, `ppt_slide_insert`, `ppt_slide_delete` | Not available |
| Office PPT    | Not exposed                    | `current_ppt_slides_list`, `current_ppt_slide_read`, `current_ppt_slide_write`, `current_ppt_slide_insert`, `current_ppt_slide_delete` |
| WPS 演示      | Not exposed                    | Same current_ppt_* RPC            |

## Format Limits

- **PPTX / PPTM**: Open XML format; supported. Use `.pptx` or `.pptm` only.
- **PPT**: Legacy binary format; not supported. Return a clear error asking user to save as .pptx/.pptm.

## Common Pitfalls

- Assuming slide order from part enumeration—always use SlideIdList order.
- Using 0-based slide index—tools use 1-based (slide 1 = first slide).
- In task pane, current document is the open presentation; no file path is passed.
