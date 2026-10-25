# Client-side libraries (`wwwroot/lib`)

Third-party, pristine upstream files (Bootstrap 5.1.0, jQuery 3.6.0,
jQuery Validation 1.19.5, jQuery Validation Unobtrusive 4.0.0). They are
**not** project source: do not hand-edit them. This folder is intentionally
**not committed** to git.

## Restoring

After cloning, run once (Windows PowerShell 5.1+):

```powershell
powershell -ExecutionPolicy Bypass -File src/HrPayroll.Web/wwwroot/lib/restore-lib.ps1
```

Or run each script individually:

```powershell
powershell -ExecutionPolicy Bypass -File src/HrPayroll.Web/wwwroot/lib/restore-bootstrap-css.ps1
powershell -ExecutionPolicy Bypass -File src/HrPayroll.Web/wwwroot/lib/restore-bootstrap-js.ps1
powershell -ExecutionPolicy Bypass -File src/HrPayroll.Web/wwwroot/lib/restore-vendor-js.ps1
powershell -ExecutionPolicy Bypass -File src/HrPayroll.Web/wwwroot/lib/restore-font.ps1
```

Each script downloads its files from pinned, immutable URLs
(jsDelivr `npm/...@x.y.z/...`, `code.jquery.com` for the jQuery sourcemap,
one font file from a pinned `google/fonts` commit) and fails loudly if any
SHA256 mismatches. `restore-font.ps1` restores the Cairo font to
`../fonts/Cairo.ttf` and the PDF-embedded copy.

Notes on line endings: a few files in the original tree use different line
endings than their upstream release (CRLF-normalized `LICENSE` texts and
`additional-methods*`, LF-normalized unobtrusive 4.0.0 whose upstream ships
CRLF). The scripts restore canonical upstream bytes, which are functionally
identical. Only two small license texts with local whitespace variants are
committed directly: `lib/jquery/LICENSE.txt` and
`lib/jquery-validation-unobtrusive/LICENSE.txt`.

The Cairo font is `ofl/cairo/Cairo[slnt,wght].ttf` at `google/fonts`
commit `e7db6ca6` (SIL Open Font License 1.1, see the `OFL.txt` files next
to the fonts).
