# Security

Do not post authentication files, access tokens, passwords, raw account responses, or personal machine paths in public issues.

For a suspected vulnerability, use GitHub's private vulnerability reporting if enabled on this repository. If that option is unavailable, open an issue requesting a private contact channel without including exploit details or secrets.

CodexMeter releases are currently unsigned. Verify the repository and published SHA-256 checksums. Checksums detect mismatches against the published files but do not establish a signed publisher identity.

Self-contained releases include their own .NET runtime. Runtime security fixes require a new CodexMeter release; updating the machine-wide .NET runtime alone does not patch an older self-contained copy.
