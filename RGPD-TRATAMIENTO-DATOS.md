# Documento de tratamiento de datos — Project Hydra

Registro factual de qué datos personales trata la aplicación, dónde viven, quién accede y cuánto se conservan — pensado como base de un Registro de Actividades de Tratamiento (RGPD Art. 30), no como el documento legal final. **No sustituye una revisión por un asesor legal** antes de usarse frente a un cliente real o una autoridad de control — está redactado a partir del código y del dominio actual, no de una revisión jurídica.

## 1. Qué datos personales se tratan, y dónde

| Dato | Entidad / campo | Categoría (RGPD) |
|---|---|---|
| Nombre y apellidos | `Trabajador.Nombre`, `Trabajador.Apellidos` | Dato identificativo |
| DNI/NIE/TIE/pasaporte | `Trabajador.Dni` | Dato identificativo |
| Fecha de nacimiento | `Trabajador.FechaNacimiento` (opcional) | Dato identificativo |
| Email | `Trabajador.Email` (opcional) | Dato identificativo |
| Observaciones | `Trabajador.Observaciones` (texto libre, opcional) | Sin categoría fija — puede contener cualquier cosa que un Gestor CAE escriba, incluida potencialmente información sensible; ver punto 4 |
| Documentos de vigilancia de la salud ("Apto médico laboral", ver `TipoDocumentoSeedData`) | Archivo PDF adjunto en `Documento`, más el resultado si se registra | **Categoría especial (Art. 9 RGPD — salud)** |
| Credenciales de acceso a plataformas externas (usuario/contraseña de portales tipo CTAIMA) | `CredencialAccesoEmpresa`/`CredencialAccesoSubcontrata` | Dato de acceso — cifrado en reposo, ver punto 3 |
| Email/nombre de los propios usuarios de la plataforma (Administrador, Gestor CAE, etc.) | `ApplicationUser` (ASP.NET Identity) | Dato identificativo de empleados del propio Project Hydra o del cliente, no de los Trabajadores gestionados |

**No se trata**: datos bancarios, datos biométricos, geolocalización, ni ninguna categoría especial del Art. 9 más allá de la vigilancia de la salud ya mencionada.

## 2. Base legal

- Datos de Trabajadores (identificativos + documentos PRL): ejecución de un contrato/obligación legal — la coordinación de actividades empresariales y la vigilancia de la salud laboral están reguladas por la normativa de Prevención de Riesgos Laborales, que es en sí misma la base legal de por qué estos datos existen en el sistema.
- Credenciales de acceso a plataformas externas: interés legítimo del cliente (Empresa/Subcontrata) para que su Gestor CAE pueda operar en su nombre en esas plataformas.
- Datos de los propios usuarios de la plataforma: ejecución del contrato de servicio (relación laboral o mercantil de cada usuario con la organización que usa Project Hydra).

## 3. Quién accede, y qué se registra de eso

- Acceso por rol: ver `ROADMAP.md` → Fase 31 (`IAlcanceDatosService`) — un Gestor CAE solo ve los Trabajadores de su propia cartera, Consulta ve todo en solo lectura, Cliente ve solo lo suyo.
- Credenciales de acceso a plataformas externas: cifradas en reposo con ASP.NET Core Data Protection (ver `RUNBOOK-CLAVES.md`), nunca en texto plano ni en logs, acceso restringido por policy y **registrado en auditoría como "acceso a dato sensible"** (`ARCHITECTURE.md`).
- Acceso a datos de salud (documentos de vigilancia de la salud) y al resto de datos identificativos del Trabajador: **hoy no tiene el mismo nivel de registro de auditoría que las credenciales** — ver [Issue #12](https://github.com/christopherjp1-jpg/Project-Hydra/issues/12), pendiente de extender el registro de auditoría a "quién vio qué dato sensible de qué Trabajador y cuándo".

## 4. El campo "Observaciones" — el riesgo menos obvio

`Trabajador.Observaciones` es texto libre sin estructura ni validación — nada impide que alguien escriba ahí un diagnóstico médico, una circunstancia personal, o cualquier otra categoría especial de datos sin que el sistema lo sepa ni lo trate como tal (no hay cifrado adicional, no hay el mismo nivel de restricción de acceso que las credenciales). Es responsabilidad operativa, no técnica: instruir a quien lo use de que este campo es para notas operativas del PRL, no un cajón de sastre para cualquier información sensible sobre la persona.

## 5. Retención

- **Documentos: 5 años**, decidido por el usuario (2026-07-18) — implementación del mecanismo real todavía pendiente, ver [Issue #10](https://github.com/christopherjp1-jpg/Project-Hydra/issues/10) para los puntos abiertos (desde qué fecha se cuenta, y si aplica igual a los tres ámbitos de Documento).
- **Trabajador como entidad completa**: sin decidir todavía, ver el mismo Issue #10.
- Hoy, técnicamente, nada se purga — el soft-delete (`EstaEliminado`) oculta filas pero las mantiene indefinidamente en la base de datos. Ver [Issue #11](https://github.com/christopherjp1-jpg/Project-Hydra/issues/11) para el mecanismo real de purga (derecho al olvido).

## 6. Subencargados del tratamiento

| Servicio | Qué trata | Dónde |
|---|---|---|
| Railway | Aloja la aplicación y la base de datos completa (todo lo de la tabla del punto 1) | Ver política de privacidad de Railway |
| AWS S3 (`eu-south-2`) | Backups automáticos de la base de datos completa + claves de cifrado — ver `RUNBOOK-CLAVES.md` | Región España |
| Sentry (si se activa, hoy inerte) | Trazas de error — potencialmente puede incluir datos de la petición que falló | Según configuración cuando se active |
| Microsoft Graph / M365 (cuando se active `IEmailService`, ver `ROADMAP.md` §6) | Correos enviados/recibidos, que pueden incluir datos de Trabajadores adjuntos como documentos | Según el tenant de M365 del cliente |
| Anthropic API (cuando se active la Iniciativa de IA, ver `ROADMAP.md` §7b) | Contenido de PDFs de reconocimientos médicos enviados para extracción — **incluye datos de salud** | Ver política de privacidad/DPA de Anthropic antes de activar esto con datos reales |

Un DPA formal con cada uno de estos subencargados es el [Issue #13](https://github.com/christopherjp1-jpg/Project-Hydra/issues/13) — **sigue aplicando íntegro con el uso interno actual**: son encargados del tratamiento de la empresa, se venda o no el software (ver `ADR-002-single-tenant.md` § 4). La otra mitad original de ese Issue —el DPA entre Project Hydra y sus clientes como proveedor SaaS— **no aplica mientras el uso sea interno** (`ADR-002-single-tenant.md`); volvería a ser bloqueante comercial solo si se retoma la venta a terceros en el fork futuro.

## 7. Cifrado en tránsito

Confirmado: la app fuerza `UseHsts()` y procesa `ForwardedHeaders` (`XForwardedFor`/`XForwardedProto`) desde Fase 21 — Railway termina TLS en su proxy de entrada y reenvía la petición internamente como HTTP, que es el patrón estándar detrás de un balanceador gestionado (ver el comentario en `Program.cs` junto a `UseForwardedHeaders`). No hay ningún tramo de la ruta pública en claro. No se ha hecho una auditoría externa de esto (p. ej. con `testssl.sh` contra el dominio público) — es una confirmación de configuración, no una auditoría de terceros.
