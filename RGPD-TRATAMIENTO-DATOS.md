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

- **Documentos: 5 años**, decidido por el usuario (2026-07-18), por coincidir con la prescripción de responsabilidades en el orden social.
- **Desde qué fecha se cuenta**: decidido por el usuario (2026-07-31) — desde el **evento más temprano** entre el fin de vigencia del documento y el cese de la relación (baja del trabajador, cierre del proyecto, fin de relación con la empresa). Razonamiento: un documento subido el último día de su vigencia hay que conservarlo su plazo desde ahí, y contar desde la emisión haría que dos documentos con la misma vigencia se purgasen en momentos distintos según cuándo se hubieran subido. **Los documentos sin fecha de vencimiento cuentan desde la emisión**, que es lo que evita tener documentos perpetuos.
- **Trabajadores dados de baja y su información asociada: 5 años desde el día de la baja**, decidido por el usuario (2026-07-31) y configurable (`RetencionDatos:AniosRetencionTrabajadores`). A diferencia del Documento aquí no hay dos eventos que comparar: la baja es el único hito. Un trabajador de alta no tiene fecha de purga. Es la categoría más sensible porque incluye datos de salud (reconocimientos médicos), y por eso el plazo puede desactivarse por separado del de Documentos si la revisión legal lo pide.
- **Qué significa purgar**: **anonimización**, no borrado físico — decidido por el usuario (2026-07-31). Rompe de forma irreversible el vínculo con la persona física y conserva el histórico de auditoría CAE, que deja de ser dato personal.
- **La destrucción NO es automática** — decidido por el usuario (2026-07-31). Vencer el plazo solo genera una `SolicitudPurga` en estado *pendiente de revisión*: nada se destruye hasta que alguien la autoriza expresamente y fija fecha de ejecución. El motivo es poder **comunicárselo antes al tenant**, para que extraiga sus datos si su política interna exige conservarlos más años que la de la plataforma. La solicitud registra cuándo se avisó al tenant, quién autorizó y cuándo se ejecutó, y puede cancelarse —dejando dicho por qué— incluso después de programada.
- **Estado de la implementación** (completo 2026-07-31, Fase 60): el ciclo entero está construido y cubierto por 28 tests — cálculo de plazos (`CalculadoraRetencionDocumento`), detección (`DeteccionPurgaService`), flujo de autorización (`SolicitudPurga`, con la invariante de que no hay camino a "ejecutada" sin pasar por una autorización con fecha), anonimización (`Trabajador.Anonimizar` / `Documento.Anonimizar`, que además borra el PDF porque el dato personal vive dentro del archivo) y la pantalla `/retencion`. Los plazos y el criterio son configurables fuera del código (`RetencionDatos`). La ejecución usa la **fecha de corte guardada en la solicitud**, no "hoy": lo que se destruye es exactamente el conjunto que se revisó y autorizó.
- **El barrido es manual**, desde un botón de `/retencion` — no hay temporizador. La primera vez que el sistema propone destruir datos conviene que sea porque alguien lo pidió. Convertirlo en periódico es añadir un `BackgroundService` sobre el mismo `BuscarDatosPurgablesCommand`; **decisión abierta**, no cerrada por falta de trabajo.
- La política sigue **apagada por defecto** (`RetencionDatos:Activa = false`): mientras no se active no se detecta ni se purga nada, y el soft-delete (`EstaEliminado`) mantiene las filas indefinidamente. Ver [Issue #11](https://github.com/christopherjp1-jpg/Project-Hydra/issues/11).
- **Quién autoriza una purga**: hoy, el rol `Administrador` — `/retencion` no es accesible para ningún otro rol. Esto resuelve el flujo pero **no crea la figura de administración de plataforma** que el usuario describía: los seis roles siguen siendo de negocio dentro de un tenant (§ 7 de `docs/MULTITENANCY.md`) y ninguno cruza la frontera entre organizaciones. Queda abierto para cuando haya más de un tenant operativo con datos que cumplan plazo.
- **Verificación**: comprobado en navegador que la pantalla carga, que el barrido responde y que el mensaje es correcto cuando no hay nada que cumpla plazo. **No verificado en navegador el camino con datos realmente purgables** — los datos de prueba son de este año y el tenant de plataforma no tiene datos operativos. Ese camino está cubierto por tests, no por una pasada real de la pantalla.
- Los plazos y criterios de este apartado **no sustituyen una revisión por un asesor legal** (ver encabezado de este documento): están recogidos como decisión del propietario del producto, no como dictamen jurídico.

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
