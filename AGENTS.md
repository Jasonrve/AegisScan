# AegisScan agent guide

The repository follows the prescribed monorepo shape. Product behavior belongs in `internal/app` and `internal/domain`; adapters belong in `internal/database`, `internal/operator`, `internal/discovery`, and `internal/plugins`; HTTP translation belongs in `internal/server`; reusable UI primitives belong in `web/src/components`; feature-specific UI belongs in `web/src/features`.

The current dashboard is a frontend vertical slice with local fixture data. Do not move database or Kubernetes access into React components when the API is introduced. Scanner plugins consume prepared workspaces owned by Aegis; they never clone repositories themselves.

## Commands

- `cd web; npm run dev` — local development
- `cd web; npm run build` — production build
- `cd web; npm run test` — frontend tests when added
