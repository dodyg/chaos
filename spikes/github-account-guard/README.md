# Account guard experiment

This standalone Node experiment models the proposed CLI-side identity guard. Run it with
`node --test spikes/github-account-guard/guard.test.mjs`. The loopback server receives only
synthetic fixture values. Do not use this prototype with real credentials or GitHub.

The tests exercise one credential resolution per operation, an account switch between identity
check and write, failed and malformed identity responses, revoked credentials, redirect refusal,
and destination validation. The injected transport rewrites the approved URL to a loopback test
server. This is not an implementation or test of GitHub CLI, its credential store, refresh,
proxy handling, TLS, production redirects, or MSIX execution. It must not be used by Chaos.
Those checks remain prerequisites to G0 completion.
