# Releasing PhoneBox

1. Update `agent/PhoneBox.Agent/PhoneBox.Agent.csproj`, Android `versionName` and its increasing `versionCode`.
2. Add dated release notes to `CHANGELOG.md`; update README and demo catalog counts when widgets/templates change.
3. Run `python tools/check-release.py`, `./gradlew lintDebug assembleDebug` in `android`, the agent tests, `npm test` in `tools/ui-tests`, and
   `pwsh tools/test-install-apk.ps1` on Windows. Build and verify the release-signed APK.
4. Commit the reviewed scope, push `main`, and wait for the build and Pages workflows to pass.
5. Run the **release** workflow on that commit. Use a draft for artifact review, or publish after the gates pass.

The release workflow reuses the build checks, then tests and smoke-runs the agent on native Windows x64,
Linux x64/ARM64, and macOS ARM64/Intel runners. It builds the signed APK, signs macOS apphosts ad hoc, creates
provenance attestations and SHA-256 checksums, and publishes the release notes from the changelog.
The reusable artifact contract also checks the real upload/download boundary with transfer fixtures: exact release
filenames and hashes, exclusion of debug intermediates, and the actual demo's Pages tar format. It never publishes
a release or deploys Pages. Changes to Pages deployment still need a successful Pages workflow on `main`.
Published releases update the Scoop bucket and generate winget manifests in a follow-up commit.
Winget manifests are prepared for submission; generation alone does not mean the package is accepted into winget-pkgs.

Required GitHub Actions secrets are `PHONEBOX_KEYSTORE_BASE64`, `PHONEBOX_STORE_PASSWORD`, `PHONEBOX_KEY_ALIAS`
and `PHONEBOX_KEY_PASSWORD`. Keep the original Android signing key: an incompatible APK must fail the update
while preserving the installed app and its data. Never commit the key or its passwords.

After publishing, verify the tag points to the intended commit, every asset matches `SHA256SUMS-<version>.txt`,
and `gh attestation verify <asset> --repo ersinkoc/phonebox` succeeds for the APK and agent archives.
Extract downloaded archives and run `python tools/smoke-agent.py <executable> --version <version>` on available
native machines. Separate CI proof, downloaded-binary checks and physical-device tests in the release report.

`scripts/build.ps1` is a local packaging helper. `scripts/release.ps1` dispatches the gated GitHub release workflow.
This release process does not submit packages to external registries automatically.
