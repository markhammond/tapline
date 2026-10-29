# Notices — LiveGrid.Sample

## The alert sounds

`wwwroot/sounds/*.wav` — 22 short recordings, copied from the ChalkQL Blotter sample by its author's leave, with the
conditions they carry there. They are **not** part of Tapline and not under its licence; they were collected more than
fifteen years ago, from sources that can no longer be traced, and are believed to be in the public domain or to carry no
licence; they **may not be used for any commercial purpose**; and one will be removed on request — open an issue.
`wwwroot/sounds/NOTICE.md` says the same beside them. Replace the files, keeping their names, to use sounds of your own.

## CodeMirror — the alerts' condition editor

`wwwroot/condition-editor.js` bundles CodeMirror 6 and the packages it is built from, each under the MIT License (their
notices: `wwwroot/condition-editor.LICENSE.txt`). It is built from `editor/` with esbuild (`npm run build` there, which
needs Node; the bundle is committed, so the sample runs without it), and both are the ChalkQL Blotter sample's, copied
whole.
