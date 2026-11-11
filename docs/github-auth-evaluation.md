# GitHub App authentication evaluation

> Historical evaluation: the user declined GitHub App registration. The latest CLI-based
> authentication decision in GITHUB.md and docs/decisions.md supersedes this proposal.

Decision 2026-11-11: use a GitHub App acting on behalf of the connected user. The user
explicitly authorized Chaos to handle credentials and store them in Windows Credential Manager.
This replaces the CLI and guarded-fork proposals. The App must be installed on the relevant
account/repositories and separately authorized by the user. A user access token has the
intersection of the user's and the App's access; activity is attributed to the user and app.
[GitHub authentication](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/about-authentication-with-a-github-app),
[authorization and installation](https://docs.github.com/en/apps/using-github-apps/authorizing-github-apps).

The app registration and its client ID are not user credentials. Do not embed an app private key
or client secret in the desktop application. Select and verify a public-client authorization flow
with GitHub's current documentation, including PKCE support, callback handling and refresh. A
browser flow with PKCE is preferred if supported for this app; device flow is an alternative to
validate, not an assumed equivalent. The authorization response produces user credentials and
is handled only by the native host. [GitHub App best practices](https://docs.github.com/en/enterprise-cloud%40latest/apps/creating-github-apps/about-creating-github-apps/best-practices-for-creating-a-github-app),
[generating user tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app).

Store access and refresh credentials in Windows Credential Manager, scoped to this app, local
profile, connection, and verified numeric GitHub user ID. Keep them out of all databases,
archives, backups, renderer and RPC, trace logs, exception text, command lines and URLs. User
identity and a nonsecret credential reference may be stored in SQLite. Do not log raw OAuth or
API responses. Test actual Credential Manager behavior under installed MSIX identity, including
reset, disconnect and account isolation. Tokens are device-local: restoring elsewhere requires
explicit authorization before any synchronization.

The native credential provider owns refresh and gives each request one immutable credential
snapshot associated with a verified numeric user ID. Serialize refresh per connection; verify
identity before using a newly authorized or refreshed token. Fail closed on missing, malformed,
mismatched, revoked or ambiguous credentials; never fall back to another account. Design and
prove interrupted refresh recovery because GitHub rotates token pairs: if a rotation succeeds
remotely but the new credentials cannot be persisted, stop and ask for reauthorization. Do not
claim that Windows Credential Manager makes a multi-step remote rotation atomic.

Registration ownership, app publication and exact permissions are still to be established; the
manifest registration flow itself returns secrets and is not necessary for each user. GitHub
App installation approval, user authorization, organization policies and inaccessible repos need
explicit UI and tests. Verify all required REST and GraphQL operations, API limits, proxy,
redirect policy and account/revocation behavior with a throwaway repository and account before
G0 closes. G1 pure domain code can proceed independently; G2 and later wait for G0 and G1.
