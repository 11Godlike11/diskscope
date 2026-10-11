# Security policy

The latest published release is the supported version. DiskScope performs privileged local filesystem operations when run elevated; cleanup bugs should be treated seriously.

For a potential vulnerability, use GitHub's **Report a vulnerability** entry under this repository's Security tab. Please include a minimal fixture reproduction and explain the boundary crossed. Do not include personal disk snapshots, sensitive filenames, production files, or access tokens. Avoid testing deletion against real user data.

If private vulnerability reporting is unavailable, open an issue requesting a private contact channel without disclosing exploit details or sensitive information.

The tool is local stdio/CLI software. Do not expose it through an unauthenticated network bridge. Release binaries are unsigned at present; published SHA-256 hashes check integrity but do not establish a code-signing identity.
