# Security

Security fixes target the latest stable 1.x release. Version 1.0.0 is the first supported stable release. Older previews are not supported.

Do not post exploit details or personal data in public issues. Contact the [maintainer](https://github.com/Rahul2749) privately through an established channel. A dedicated private vulnerability-reporting route has not yet been configured.

Security tests currently cover protected properties, update allow-lists, nullable bindings, and invalid configuration. Runtime fuzzing, recursion limits and collection-size limits belong to future engines/features. See `docs/binding-spec.md` for the current trust boundary and shallow reference semantics.
