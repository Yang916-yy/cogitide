# Settings, skill management and PowerPoint output validation

This record covers the changes after `9ca036e`. It separates functional and source-package acceptance from actual desktop-device or rendered-slide acceptance. Tests use generated projects and local model fixtures; the explicit public-catalog check downloads only immutable source data and never calls a paid model.

## Implemented behavior

- Settings use Avalonia's existing left-positioned `TabControl`, with General, Models and accounts, Skills, Connections and Advanced categories. Existing account/API controls remain in the model category. Connection and task-budget actions route to their existing dialogs. Fresh onboarding selects Models and accounts. English and Simplified Chinese resources remain paired; preference changes retain in-progress provider inputs.
- Manual skill import copies an endpoint-local folder into the selected project's `.agents/skills`. Public catalog search defaults to the pinned `anthropics/skills` snapshot. The kernel can call `search_skills` and approval-required `install_skill`; a public GitHub repository, full commit and skill path can also be specified. A newly installed valid skill is discovered at the next model request boundary in the same run.
- Installation reuses the existing YamlDotNet parser, BCL anonymous pooled HTTPS/ZIP/JSON, workspace checks and ordinary approval/rule checks. It refuses existing destinations, unsafe paths, links and oversized packages, records hashes/source provenance, retains applicable upstream notices, and never executes downloaded scripts or installs their dependencies. Reimporting a previously installed skill regenerates its receipt from the actual copied files.
- Catalog pagination distinguishes `nextOffset` from `catalogIncomplete`. Omitted incompatible/oversized entries do not make the model keep paginating empty results. The default snapshot's oversized manifests remain omitted under existing Core bounds rather than bypassing that parser.
- `create_presentation` generates an editable `.pptx` through Open XML SDK 3.5.1, with titles, bullet paragraphs, local images, notes and 16:9/4:3 layouts. Workspace rules and normal write approval apply; existing files require their current hash. The SDK validates the complete package before a single-file commit.

## Review corrections

The first compiler checks exposed an Open XML SDK property-name mismatch and an Avalonia property placed on `ListBox` rather than its item containers. Those were corrected without replacing either library.

Independent review found that opening a Unix FIFO could block before a cancellable file read. Skills import and presentation input reads now share a bounded regular-file preflight through the existing argv runner and the OS's `test -f` implementation (`/usr/bin/test` on Linux, `/bin/test` on macOS). No shell, native ABI or second process runner was introduced. These checks do not prevent a hostile same-user process from changing a path after inspection.

The first complete run passed 790 tests and failed two. Additional unconditional tool guidance had pushed a protected raw-history compaction fixture over its context budget; that duplicate guidance was removed because the tool contracts already contain it. A provider deadline fixture could exhaust its 250 ms deadline during concurrent SDK cold initialization before reaching the response body. The fixture retains a real five-second request deadline, requires an uncancelled body read to begin, verifies timeout cancellation and verifies no retry. Production provider timeout behavior was not changed. These failures are retained as earlier attempts, not counted as successful validation.

The tool integration peer also had historical exact catalog-count assertions. They were replaced with required-capability, uniqueness and read-only writer-exclusion checks, so adding the new tools does not make a valid request look like a model service failure. Observed complete/read-only catalogs contained 27/16 tools in this run; optional integrations may add more.

Headless rendered frames exposed a selected model-connection label that retained the previous UI language. Its selected-item template now follows the same live resource binding as the existing language selector. Fixture catalog labels were aligned with the actual default source. Visual inspection used only generated data and no native visible windows.

## Acceptance scope

The initial focused batch passed 52 tests, including a real anonymous download of the pinned public catalog, installation of its `pptx` package into an isolated generated workspace, and discovery through the existing Core parser. No package code was executed. A subsequent complete Release run passed 791 tests, failed none and conditionally skipped four before the final pagination regression additions.

The feature's strict **Release** `scripts/build.ps1` completed with zero warnings/errors and **793 passed, 0 failed, 4 conditional skips out of 797**. Real TypeScript/Python server tests, the real public skill catalog test and native Windows isolation were explicitly enabled. The Windows writable native-worker fixture also created and hash-checked a presentation through the restricted broker, with its JSON result retaining the explicit `not_rendered` status. The four skips were three separately gated real remote tests and the macOS-only alias fixture; no new remote or macOS-device acceptance is inferred.

The strict Debug build passed. All seven local Host fixtures passed: `verify-host`, `verify-lifecycle`, `verify-responses`, `verify-continuity`, `verify-tools-upgrade`, `verify-research` and `verify-integrations`. They used generated workspaces and local model/protocol servers. The public skill archive check is the only new external-data acceptance, not a model benchmark.

English and Simplified Chinese resources contain **447 matching, unique keys**. Five fixture-only headless frames were captured and visually inspected, including compact-width settings, model controls and the skill catalog. Those images, package files and raw logs remain under ignored `artifacts/settings-skills-ppt-*`. Module inventory and whitespace checks passed. The underlying [development-workflow record](development-workflow-validation-2026-11-02.md) retains its earlier counts and platform scope.

The Windows x64 self-contained Host package passed release verification across **625 files**, including pinned Open XML/framework/packaging assemblies, source locks and unchanged retained notices. Its actual Host smoke ran with the bundled runtime, performed Unicode session/command operations, made zero model calls and exited cleanly. Package verification does not certify native GUI rendering or other platforms' native isolation.

### Streamed-download follow-up

Feature commit `b089b19` passed [Windows, Linux and macOS CI](https://github.com/Yang916-yy/cogitide/actions/runs/41386857705). Subsequent review found that `HttpClient.Timeout` with `ResponseHeadersRead` does not bound later archive-body reads. A single BCL linked deadline now covers headers and the entire streamed download, defaults to 90 seconds, and permits only shorter test overrides. Caller cancellation remains cancellation; an independent deadline reports a fixed known pre-installation failure without retry or copied files.

Two additional deterministic fixtures return successful headers and then stall the archive body. They verify whole-download timeout, original caller cancellation, stream disposal, one request and no installed output. After this correction the final strict Release run passed **795 tests, failed none and conditionally skipped four out of 799**, with the same native Windows, real language-server and public-catalog opt-ins. The earlier counts and package acceptance above retain their original feature-build scope.

The strict Debug build and all seven local Host integration fixtures also passed again after this download correction. Module inventory and whitespace checks remained current.

## Product limits

Skill search covers the configured pinned catalogs rather than arbitrary internet-wide search. A downloaded skill is not an installed program: external executables, script dependencies, provider-specific instructions and compatibility may require separate explicit work. Native isolated workers retain their denied-network policy and provide no privileged installation fallback. Local-folder import on an SSH/WSL connection uses the remote Host's path; it is not a desktop-to-remote upload.

PowerPoint output is a bounded fixed-theme generator, not an editor or importer for arbitrary existing decks. Image dimensions/aspect ratios are caller-supplied, with existing signature checks rather than full decoding. SDK validation does not render slides, measure fonts or establish visual quality. No slide viewer or direct artifact-open UI was added; users can open the resulting workspace file externally. Actual native GUI/device behavior and Linux/macOS native isolation remain separate acceptance work.

See [Desktop](../src/Workbench.Desktop/README.md), [skills](../src/Workbench.Tools/Skills/README.md), [presentations](../src/Workbench.Tools/Presentations/README.md), and [third-party notices](../THIRD_PARTY_NOTICES.md) for configuration, component reuse, fixed sources and license boundaries.
