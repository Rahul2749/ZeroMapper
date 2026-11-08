# Security

Security fixes target the latest stable 1.x release. Version 1.0.0 is the first supported stable release. Older previews are not supported.

Report suspected vulnerabilities privately through [ZeroMapper's Security Advisories page](https://github.com/Rahul2749/ZeroMapper/security/advisories) using **Report a vulnerability**. Include the affected package and version, the impact, and a minimal reproduction. Do not include credentials or post exploit details or personal data in a public issue.

Security tests currently cover protected properties, update allow-lists, nullable bindings, and invalid configuration. Runtime fuzzing, recursion limits and collection-size limits belong to future engines/features. See `docs/binding-spec.md` for the current trust boundary and shallow reference semantics.
