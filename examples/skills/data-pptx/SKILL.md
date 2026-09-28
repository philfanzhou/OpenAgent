---
name: data-pptx
description: Generate an editable PowerPoint report from structured JSON data, with configurable native charts and a PPTX layout template.
---

# Data to PPTX

Use this skill when the user supplies report data and wants a PowerPoint deck.
The script runs in the isolated Agent.Matrix Runner. It uses the Runner's
`python-pptx` package and does not need network access.

1. Read `resources/example.json` for the accepted data shape. Prepare one JSON
   object with a report title, content slides, chart categories and series.
2. Choose a `.pptx` template containing slide masters and layouts but no slides.
   The bundled template is `resources/report-template.pptx`. For a user template,
   place the file in the conversation's `/work` directory and set `template`
   to its absolute `/work/...pptx` path. A user-supplied template is loaded as
   the deck base; its page size and layout names are preserved.
3. Set `layouts.title` and `layouts.content` to layout names or indices if the
   template needs non-default layouts. Customize `theme.accent`,
   `theme.palette`, and each chart's type, series colors, legend, data labels,
   number format, and numeric axis range.
4. Call `run_skill_script` on `scripts/generate.py` with an arguments array
   containing exactly one JSON object, or one path to a JSON file in `/work`.
   The script writes `/output/report.pptx`; the Runner registers this as a
   conversation file. Check `exitCode`, then publish the returned file ID with
   `publish_files`.
5. Inspect the generated PPTX or render it with the available file tools before
   delivery. Native PowerPoint charts remain editable in the PPTX.

The example file yields a five-slide quarterly report with column, line and
pie charts. The package stays below Agent.Matrix's 4 MiB upload limit; generated
PPTX files must stay below the Runner's 10 MiB per-file output limit.
