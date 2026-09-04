# Architecture overview

AegisScan is planned as a Go operator/API with PostgreSQL as the historical query store and a React web UI served alongside the API where practical. Kubernetes resources express bounded desired state; they are not used as a database.

The application layer owns use cases such as repository discovery, execution scheduling, and findings normalization. Domain packages do not depend on HTTP, PostgreSQL, Kubernetes, or provider SDKs. Adapters implement narrow interfaces and handlers translate stable DTOs.

The first delivered slice is intentionally frontend-only. It uses local fixtures to make the core dashboard experience runnable while the backend vertical slices are added. This is a deliberate implementation sequence, not a change to the target architecture.
