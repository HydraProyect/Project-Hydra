# POLITICA_COOKIES_PRODUCTO — Política de Cookies dentro de la plataforma

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal. Borrador nº 4 del paquete de Lanzamiento (MVP1) de `LEGAL_FRAMEWORK.md` § 1. No es texto legal final.
**Propósito**: Documentar, de forma corta y separada de la política de cookies del sitio web comercial, las cookies estrictamente técnicas y funcionales que usa la plataforma Hydra una vez el usuario ha iniciado sesión — patrón adoptado del área privada de 6conecta (`LEGAL_FRAMEWORK.md` § 2.3).

## Qué pertenece aquí

- Cookies técnicas de sesión y funcionales propias usadas dentro de la aplicación autenticada.

## Qué NO pertenece aquí

- Cookies del sitio web público comercial → `POLITICA_COOKIES_WEB.md`.

---

## 1. Alcance

Este documento cubre únicamente las cookies utilizadas dentro de la plataforma Hydra, una vez el usuario ha iniciado sesión con sus credenciales. Es un documento corto y propio, enlazado desde la propia aplicación (p. ej. desde el pie de página o la pantalla de configuración de cuenta).

## 2. Compromiso

**Dentro de la plataforma, Hydra solo usa cookies técnicas de sesión y funcionales propias — ninguna cookie de terceros, ninguna cookie publicitaria o de analítica de comportamiento.** Esta restricción es deliberada (`LEGAL_FRAMEWORK.md` § 3.3) y aplica a cualquier herramienta de analítica de producto que se considere en el futuro — su incorporación exigiría revisar primero este compromiso, no darlo por superado en silencio.

## 3. Cookies utilizadas

| Nombre | Finalidad | Duración | ¿Necesaria para el funcionamiento? |
|---|---|---|---|
| `[PENDIENTE — cookie de sesión de autenticación, ASP.NET Core Identity]` | Mantener la sesión iniciada | Sesión / según configuración de expiración | Sí |
| `[PENDIENTE — cookie de circuito Blazor Server, si aplica]` | Sostener la conexión interactiva del circuito SignalR | Sesión | Sí |
| `[PENDIENTE — cookie de selección de Cliente activo en Delegated Workspace, si aplica]` | Recordar qué Cliente Delegante está operando un Operador Delegado | Sesión | Sí |

Por ser todas estrictamente necesarias para el funcionamiento del servicio contratado, no requieren consentimiento adicional al ya prestado en el registro/contratación (art. 22.2 LSSI-CE, excepción de cookies técnicas).

## 4. Sin consentimiento adicional dentro de la app

Al no existir cookies no necesarias dentro de la plataforma, no se muestra un banner de consentimiento adicional una vez el usuario ha iniciado sesión.

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.3 — decisión de dos documentos separados.
- `docs/business/legal/POLITICA_COOKIES_WEB.md` — cookies del sitio web comercial.
- `docs/business/legal/TERMINOS_Y_CONDICIONES.md` — condiciones de uso de la plataforma.
- `docs/business/legal/README.md` — estado del paquete legal completo.
