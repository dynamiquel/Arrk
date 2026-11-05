## What changed

<!-- Describe the user-visible behaviour and the smallest useful design summary. -->

## Verification

- [ ] `./scripts/ci.sh` passes.
- [ ] `./scripts/ci.sh --docker` passes, or Docker-backed tests were intentionally skipped (explain why).
- [ ] New or changed behaviour has tests.
- [ ] The matching `docs/features/` note is updated (if behaviour or configuration changed).
- [ ] `Directory.Build.props` version is untouched; the release workflow owns it.
- [ ] No secrets, generated config, local paths or build artifacts are included.
