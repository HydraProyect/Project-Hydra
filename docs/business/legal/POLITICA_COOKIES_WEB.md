# POLITICA_COOKIES_WEB — Política de Cookies del sitio web comercial

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal. Borrador nº 3 del paquete de Lanzamiento (MVP1) de `LEGAL_FRAMEWORK.md` § 1. No es texto legal final.
**Propósito**: Informar sobre las cookies y tecnologías similares usadas en el sitio web comercial de Hydra, conforme a la Ley 34/2002 (LSSI-CE) y la Guía de Cookies de la AEPD. Documento deliberadamente distinto del de cookies en producto (`POLITICA_COOKIES_PRODUCTO.md`) — decisión de arquitectura legal de `LEGAL_FRAMEWORK.md` § 2.3.

## Qué pertenece aquí

- Categorías de cookies del sitio web público (no de la aplicación SaaS).
- Tabla de cookies con titular, finalidad y duración.
- Mecanismo de consentimiento (banner) y de retirada del consentimiento.

## Qué NO pertenece aquí

- Cookies dentro de la aplicación una vez el usuario ha iniciado sesión → `POLITICA_COOKIES_PRODUCTO.md`.
- El resto del tratamiento de datos personales del sitio → `POLITICA_PRIVACIDAD.md`.

---

## 1. Qué son las cookies

Las cookies son pequeños archivos que un sitio web instala en el dispositivo del usuario para almacenar y recuperar información sobre su navegación.

## 2. Posicionamiento de Hydra frente a las cookies

**Decisión deliberada** (ver `LEGAL_FRAMEWORK.md` § 2.3 y § 3.3): el sitio web de Hydra usa **analítica mínima, sin píxeles publicitarios ni seguimiento cross-device**. No se instalan cookies de redes sociales (Facebook, TikTok) ni de plataformas publicitarias. Esta decisión es coherente con el argumento central de confianza del producto y tiene un coste de oportunidad de marketing asumido conscientemente — revisable en `docs/business/GO_TO_MARKET.md` si demuestra ser un lastre comercial real, no antes.

## 3. Categorías de cookies utilizadas

| Categoría | Finalidad | ¿Requiere consentimiento? |
|---|---|---|
| Técnicas / necesarias | Funcionamiento básico del sitio (navegación, balanceo de carga, seguridad) | No |
| Preferencias | Recordar elecciones del usuario (p. ej. idioma) | No, si no crean perfil |
| Analíticas propias (first-party) | Medir uso agregado del sitio para mejorar contenido | Sí |
| Publicitarias / de terceros | — | **No se usan** |

## 4. Tabla de cookies

> `[PENDIENTE — completar con el inventario técnico real una vez el sitio web esté implementado. Formato mínimo exigido por la Guía de Cookies AEPD: nombre, titular, finalidad, duración, tipo.]`

| Nombre | Titular | Finalidad | Duración | Tipo |
|---|---|---|---|---|
| `[PENDIENTE]` | `[PENDIENTE]` | `[PENDIENTE]` | `[PENDIENTE]` | `[PENDIENTE]` |

## 5. Consentimiento y gestión de preferencias

Al acceder por primera vez al sitio, se muestra un banner que permite aceptar todas las cookies, rechazar las no necesarias, o configurar la preferencia por categoría. Las cookies técnicas/necesarias no requieren consentimiento y se instalan siempre.

El usuario puede modificar o retirar su consentimiento en cualquier momento desde `[PENDIENTE — enlace/mecanismo de gestión de preferencias, a implementar]`, y también configurando su propio navegador para bloquear o eliminar cookies, con la advertencia de que ello puede afectar a la funcionalidad del sitio.

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.3 — decisión de dos documentos separados y posicionamiento de austeridad de rastreo.
- `docs/business/legal/POLITICA_COOKIES_PRODUCTO.md` — cookies dentro de la aplicación.
- `docs/business/legal/POLITICA_PRIVACIDAD.md` — resto del tratamiento de datos del sitio web.
- `docs/business/legal/README.md` — estado del paquete legal completo.
