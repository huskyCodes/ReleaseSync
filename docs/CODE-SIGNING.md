# Code signing policy

**Status: ReleaseSync's downloads are not code-signed yet.** The project intends to apply to [SignPath Foundation](https://signpath.org)'s free program for open-source software. This page says how signing will work if that's approved.

Once approved, this page will carry the line: Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What gets signed

Only `ReleaseSync.exe`, built from this repository's source by the public GitHub Actions workflow in `.github/workflows/release.yml`. Nothing built on a personal computer is signed.

ReleaseSync helps people download other projects' software from those projects' own release pages. That software is not built, hosted or signed by this project.

## Team

| Role | Who |
|---|---|
| Authors (commit to the repository) | [@huskyCodes](https://github.com/huskyCodes), with [Claude](https://www.anthropic.com/claude), an AI assistant, contributing code under the maintainer's direction |
| Reviewers (review changes from anyone else before they're merged) | [@huskyCodes](https://github.com/huskyCodes) |
| Approvers (approve every signing request) | [@huskyCodes](https://github.com/huskyCodes) |

Everyone with commit or signing access uses multi-factor authentication. Every signed release is approved by hand.

## Privacy

See the [privacy policy](PRIVACY.md).
