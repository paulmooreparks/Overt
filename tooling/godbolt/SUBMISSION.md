# Submitting Overt to Compiler Explorer (godbolt.org): Step by Step

This is the playbook for getting Overt listed on godbolt.org. It assumes you've
never worked with Compiler Explorer before. Every step has a clear success
signal before you move to the next.

---

## Stage 1: Cut a release of Overt (≈10 minutes of your time)

The release workflow does the actual work. You just push a tag.

### 1.1 Push a version tag

From the repo root:

```
git tag v0.1.0-preview
git push origin v0.1.0-preview
```

That's it. This triggers
[`.github/workflows/release.yml`](../../.github/workflows/release.yml), which:

- Checks out the repo on a Linux runner
- Runs the full test suite (if a test fails, the release aborts)
- Builds `overt` as a Linux x64 self-contained single-file binary via
  `dotnet publish`
- Packages it as `overt-0.1.0-preview-linux-x64.tar.xz`
- Creates a GitHub Release with the tarball attached

### 1.2 Watch the build

1. Open <https://github.com/paulmooreparks/Overt/actions>.
2. Find the run for your tag (named after the commit message, but you can
   spot it by the tag ref on the side).
3. Wait for the green check, about 2–3 minutes.

If anything goes red, click through to the logs and fix whatever it complains
about. Then delete the tag and retry:

```
git tag -d v0.1.0-preview
git push --delete origin v0.1.0-preview
# also delete the (partial) draft release on GitHub's Releases page
```

Fix, commit, retag.

### 1.3 Verify the release artifact

1. Open <https://github.com/paulmooreparks/Overt/releases>.
2. Find the `v0.1.0-preview` release.
3. You should see `overt-0.1.0-preview-linux-x64.tar.xz` attached.
4. **Copy the download URL.** You need it for the CE PRs. It should look like:
   `https://github.com/paulmooreparks/Overt/releases/download/v0.1.0-preview/overt-0.1.0-preview-linux-x64.tar.xz`

**Sanity check (optional but recommended):** on a Linux or WSL machine:

```
curl -L -o overt.tar.xz 'https://.../overt-0.1.0-preview-linux-x64.tar.xz'
tar xf overt.tar.xz
./overt-0.1.0-preview/bin/overt --version
# -> overt 0.1.0-dev
./overt-0.1.0-preview/bin/overt --emit=csharp examples/hello.ov
# -> prints the transpiled C#
```

Once that works, the release is good. Move on.

---

## Stage 2: PR to `compiler-explorer/infra` (≈15 minutes)

This PR tells CE's build farm where to download Overt and how to lay it out
under `/opt/compiler-explorer/`.

### 2.1 Fork and clone infra

1. Open <https://github.com/compiler-explorer/infra>. Click **Fork**.
2. Clone your fork:

   ```
   git clone https://github.com/<your-username>/infra.git ce-infra
   cd ce-infra
   git checkout -b add-overt
   ```

### 2.2 Add the install recipe

Copy our staged file into place:

```
# From inside ce-infra
cp /path/to/Overt/tooling/godbolt/infra/overt.yaml bin/yaml/overt.yaml
```

Verify it looks right by opening `bin/yaml/overt.yaml`. If CE's maintainers
ask for edits during review, edit this file and push again.

### 2.3 Commit and push

```
git add bin/yaml/overt.yaml
git commit -m "Add Overt 0.1.0-preview install recipe"
git push origin add-overt
```

### 2.4 Open the PR

1. Go to <https://github.com/compiler-explorer/infra/pulls>.
2. Click **New pull request**, then **compare across forks**.
3. base: `compiler-explorer/infra:main`, compare: `<you>/infra:add-overt`.
4. Title: `Add Overt`.
5. Description. Paste this, adjusting the URL and PR number:

   ```markdown
   Adds an install recipe for Overt, an agent-first programming language
   that transpiles to C#.

   - **Repo:** https://github.com/paulmooreparks/Overt
   - **License:** Apache-2.0
   - **Release artifact:** https://github.com/paulmooreparks/Overt/releases/download/v0.1.0-preview/overt-0.1.0-preview-linux-x64.tar.xz
   - **Tarball layout:**
     ```
     overt-0.1.0-preview/
       bin/overt                 (single-file self-contained Linux x64 CLI)
       runtime/Overt.Runtime.dll (for downstream consumers compiling
                                  the emitted C#; not loaded by the
                                  CLI itself)
       LICENSE, README.md, VERSION
     ```
     `check_exe`: `bin/overt --version`. The binary is self-sufficient;
     no runtime installation needed on the farm.
   - **Companion PR:** compiler-explorer/compiler-explorer#<NUMBER>
     (to be filled in after that PR is opened)

   This adds a `tarballs`-type installer for the 0.1.0-preview release.
   Future versions append to the `targets:` list.

   Happy to adjust `url_pattern`, `dir_pattern`, `strip_components`, or
   `check_exe` if any of those don't match your infra conventions; just
   let me know and I'll push an update.
   ```

6. Submit. **Leave the companion-PR line as-is with the `<NUMBER>`
   placeholder.** When you open the compiler-explorer PR in Stage 3,
   come back and edit this PR's description to fill in its number.

### 2.5 What happens next

- A CE maintainer reviews (usually within a few days).
- They'll comment if the recipe needs adjustments: url patterns, strip
  components, check_exe paths, target format.
- When merged, CE's automated installer will fetch the tarball and expand it
  to `/opt/compiler-explorer/overt-0.1.0-preview/` on their build farm.

You can't test this part locally because it runs on CE's infrastructure.
Trust the reviewer; most issues are fixable in one or two comment rounds.

---

## Stage 3: PR to `compiler-explorer/compiler-explorer` (≈30 minutes)

This PR makes Overt appear in the language dropdown on godbolt.org and
configures how the compiler is invoked.

### 3.1 Fork and clone compiler-explorer

1. Fork <https://github.com/compiler-explorer/compiler-explorer>.
2. Clone:

   ```
   git clone https://github.com/<you>/compiler-explorer.git ce
   cd ce
   git checkout -b add-overt
   ```

### 3.2 Three required files, one optional

#### Required: `etc/config/overt.defaults.properties`

Copy from our staged version:

```
cp /path/to/Overt/tooling/godbolt/config/overt.defaults.properties \
   etc/config/overt.defaults.properties
```

#### Required: `examples/overt/default.ov`

```
mkdir -p examples/overt
cp /path/to/Overt/tooling/godbolt/examples/default.ov \
   examples/overt/default.ov
```

#### Required: register the language in `lib/languages.ts`

Open `lib/languages.ts`. You'll see a big object with entries like:

```ts
c: {
    name: 'C',
    id: 'c',
    // ...
},
```

Add a new entry, alphabetically ordered, using the snippet at
[`tooling/godbolt/config/languages-entry.ts.snippet`](config/languages-entry.ts.snippet).
Overt sorts between `objectivec` and `pascal` alphabetically; check what's
actually in the file.

#### Optional but recommended: `static/modes/overt-mode.ts`

```
cp /path/to/Overt/tooling/godbolt/monaco/overt-mode.ts \
   static/modes/overt-mode.ts
```

Without this, the editor shows Overt source as plain text (no syntax
highlighting). The file we staged is a complete Monaco mode definition.

You may also need to register it in CE's Monaco mode loader; grep for where
other languages register their modes (look for `overt` in `static/main.ts`
or similar; pattern varies by CE internals). The maintainer will point at
the exact spot during review if it's not obvious.

### 3.3 Verify locally (optional but recommended)

From the `ce` directory:

```
npm ci          # install deps
make dev        # runs a local CE instance on port 10240
```

Open <http://localhost:10240>, pick Overt from the language dropdown, paste
any example from `examples/` in this repo, select the `Overt 0.1.0-preview`
compiler in the right pane. Even without the infra PR merged, local CE tries
to invoke the compiler at the configured path, so you can temporarily point
`compiler.overt010preview.exe` at your locally-built `overt` binary in
`overt.defaults.properties` to smoke-test.

### 3.4 Commit and push

```
git add etc/config/overt.defaults.properties
git add examples/overt/default.ov
git add lib/languages.ts
git add static/modes/overt-mode.ts
git commit -m "Add Overt language"
git push origin add-overt
```

### 3.5 Open the PR

1. <https://github.com/compiler-explorer/compiler-explorer/pulls> → **New**.
2. base: `compiler-explorer/compiler-explorer:main`, compare: your branch.
3. Title: `Add Overt language`.
4. Description. Paste this, filling in the infra PR number from stage 2:

   ```markdown
   Adds Overt as a new language. Overt is an agent-first, transpile-to-C#
   language: source in `.ov` files, output is C# source text (the
   compiler-output pane shows transpiled C#, not assembly; `supportsAsm`
   is false for now).

   - **Repo:** https://github.com/paulmooreparks/Overt
   - **License:** Apache-2.0
   - **Infra PR:** compiler-explorer/infra#<NUMBER>
   - **Release artifact:** https://github.com/paulmooreparks/Overt/releases/download/v0.1.0-preview/overt-0.1.0-preview-linux-x64.tar.xz

   What the diff contains:

   - `etc/config/overt.defaults.properties`: compiler config for the
     0.1.0-preview and a `trunk` placeholder. Default flags are
     `--emit=csharp --no-color`.
   - `examples/overt/default.ov`: the hello-world shown when a user
     first lands on the language.
   - `lib/languages.ts`: new `overt` entry, alphabetical.
   - `static/modes/overt-mode.ts`: Monaco syntax mode.

   Compiler behavior notes that might matter for review:

   - The emitted C# carries `#line` directives so runtime errors and
     stack traces resolve to the original `.ov` file via portable PDB,
     the same mechanism F#/Razor/Blazor use.
   - Diagnostics follow `path:line:col: severity: CODE: message` with
     `help:`/`note:` follow-up lines. Codes are stable (`OV00xx` lexer,
     `OV01xx` parser, `OV02xx` resolver, `OV03xx` type checker).
   - `overt --version` prints a single line; `overt --emit=<stage> <file>`
     is the full CLI surface. Stages: tokens, ast, resolved, typed,
     csharp, go (go is not yet implemented and exits 1 with a clear
     message).

   No logo for v0; happy to add one if that's required or if you'd like
   to request it as a post-merge follow-up.
   ```

5. Submit. **Then immediately go back to your infra PR (stage 2.4) and
   edit its description, replacing `compiler-explorer/compiler-explorer#<NUMBER>`
   with the actual number from this PR's URL.**

### 3.6 Review loop

CE reviews tend to be responsive, a few days to a week. Common review
comments:

- **"Add a logo."** Optional. If requested, put an SVG at
  `static/logos/overt.svg` and reference it via `logoUrl: 'overt.svg'` in
  the language entry. If you don't want to bother, drop the `logoUrl` line.
- **"Adjust the Monaco mode."** Small syntax tweaks are common. Edit
  `static/modes/overt-mode.ts` and push again.
- **"Where's the infra PR?"** Link it in a comment. Usually both PRs are
  merged together.

---

## Stage 4: Once merged

Both PRs merged typically land on godbolt.org within 24 hours of the next CE
deploy. The URL you'll share is:

```
https://godbolt.org/?language=overt
```

Test a round trip on the live site:

1. Open that URL.
2. Paste hello.ov from `examples/`.
3. Right pane shows transpiled C#.
4. Share the URL with people.

---

## Keeping things in sync after the first release

Future Overt releases just need the infra PR (or its automated variant) to
add the new version to `bin/yaml/overt.yaml`'s `targets:` list. The
compiler-explorer PR's properties file also grows by one entry per version
(`compiler.overtXYZ.exe=...`).

A CE maintainer may bulk-update these once your language is established and
you've shown a few healthy release cycles. Until then, each version is a
small PR.

---

## Troubleshooting

### "The release workflow failed"

Click into the run on <https://github.com/paulmooreparks/Overt/actions>.
The most common failure is a test regression (something broke since the
last time tests were green). Fix, delete the tag, retag.

### "The tarball downloads but `overt --version` prints nothing"

The self-contained publish might have missed native libraries. The workflow
sets `IncludeNativeLibrariesForSelfExtract=true` which covers this; if
something changed, re-enable it.

### "CE maintainer says the url_pattern doesn't resolve"

The URL pattern is string-formatted with the version. If your release tag
is `v0.1.0-preview` and the asset is `overt-0.1.0-preview-linux-x64.tar.xz`,
the pattern substitutes `{0}` for `0.1.0-preview`:

```
https://.../releases/download/v{0}/overt-{0}-linux-x64.tar.xz
```

Confirm your tag and asset names match the pattern exactly. If CE parses
semver strictly, `preview` as a suffix may need adjustment; reviewers
will tell you.
