# Flujo de trabajo con Git

Repositorio oficial: `https://github.com/Daniel-dvlp/Open-Auth`

| Rama | Para qué |
|---|---|
| `main` | Versión estable. Solo entra por pull request aprobado por Daniel. |
| `develop` | Base de trabajo con todo lo integrado (Escudo + Billetera + Ollama). Las ramas de trabajo salen de aquí. |
| `feature/<nombre>` | Una rama por tarea (por ejemplo `feature/frontend`). |

## Frontend de la billetera (`feature/frontend`)

```powershell
git clone https://github.com/Daniel-dvlp/Open-Auth.git
cd Open-Auth
git checkout develop
git pull
git checkout -b feature/frontend
```

Trabaja en `frontend/` (consola de analista) y en la nueva app de la billetera. El contrato de la API está en:

- `docs/contrato_billetera_frontend.md` — pantallas, estados, mensajes y errores.
- `docs/openapi-billetera.json` — OpenAPI (también en vivo en `http://localhost:8082/openapi/v1.json`).
- `docs/billetera.http` — peticiones listas para probar.

Para levantar el backend: `README.md`, secciones 7.2 (Docker Compose) y 7.9 (sin Docker).

Al terminar:

```powershell
git add .
git commit -m "Frontend de la billetera"
git push -u origin feature/frontend
```

Luego abre en GitHub un **pull request de `feature/frontend` hacia `main`**. Daniel lo revisa y lo fusiona.

Reglas:
- No subir `.env`, `seed/*.json` (credenciales de prueba) ni carpetas `bin/`, `obj/`, `node_modules/`.
- Antes de abrir el PR, actualiza tu rama con `git pull origin develop` y verifica que el backend siga respondiendo.
