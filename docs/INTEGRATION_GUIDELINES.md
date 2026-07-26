# Guía práctica — Cómo incorporar un nuevo proveedor de integración

**Este documento no es arquitectura** (eso vive en `ARQUITECTURA-INTEGRACIONES.md`, léelo primero). Es la receta paso a paso para que cualquier desarrollador — o cualquier sesión de IA — construya un conector nuevo siguiendo siempre el mismo patrón, sin reabrir decisiones ya tomadas. **No aplicable todavía**: la Plataforma de Integraciones es diseño de backlog, no código existente (ver `ROADMAP.md` § Épico — Plataforma de Integraciones). Esta guía queda lista para cuando exista el primer proveedor real priorizado.

## Antes de escribir código

Confirma estos tres datos con quien priorizó el proveedor — si falta alguno, no empieces:

1. **Qué capacidades** ofrece de verdad esta versión de su API (`CapacidadesIntegracion`, `ARQUITECTURA-INTEGRACIONES.md` § 3.1) — no asumas que soporta todo lo que Hydra sabe sincronizar.
2. **Qué versión de API** vas a integrar, y si el proveedor tiene un ciclo de deprecación conocido.
3. **Si ofrece webhooks** o solo hay que ir a buscar los datos (polling programado).

## Pasos

1. **Declarar el catálogo** (una vez, no por tenant): insertar `ProveedorIntegracion` (si es la primera vez que se integra este proveedor) y `VersionApiProveedor` con sus `Capacidades` reales — nunca "todas por si acaso". Si más adelante el proveedor añade capacidades nuevas en una v4, se añade una fila `VersionApiProveedor` nueva, no se edita la de v3.

2. **Implementar `IIntegrationProvider`** en `src/CaeManager.Infrastructure/Integraciones/Providers/{Proveedor}{Version}IntegrationProvider.cs`. Una clase por (proveedor, versión) — si el mismo proveedor tiene v2 y v3 activas a la vez, son dos clases, no una con un `if (version == "v3")` por dentro.

3. **Declarar las capacidades** en la propia clase (`Capacidades => CapacidadesIntegracion.Trabajadores | ...`) — deben coincidir exactamente con lo declarado en `VersionApiProveedor` del paso 1; si no coinciden, es un bug de este proveedor, no del orquestador.

4. **Registrar el provider en DI** (`InfrastructureServiceCollectionExtensions`), añadido al diccionario que consulta `IIntegrationProviderFactory` por `(Codigo, Version)`. Nunca un `switch`/`if` nuevo en Application o Presentation comparando nombres de proveedor — si sientes la tentación de escribir `if (proveedor == "dokify")` fuera de la carpeta `Providers/`, es la señal de que algo se está acoplando donde no debe.

5. **Configurar autenticación**: `CredencialIntegracion` cifrada (mismo patrón que `CredencialAccesoEmpresa` — reutilizar el `ValueConverter` de Data Protection API existente, no inventar uno nuevo). Sin credenciales configuradas, la conexión queda "inerte por defecto" (mismo criterio que `AzureAd`/`Graph` — nunca falla, simplemente no hace nada).

6. **Implementar sincronización** (`SincronizarAsync`): traduce `EntidadIntegracionDto` (la forma universal de Hydra) al formato del proveedor. El mapeo de campos vive **solo** en esta clase — si necesitas tocar `Domain` o `Application` para mapear un campo, el diseño de `EntidadIntegracionDto` se ha quedado corto y hay que ampliarlo ahí, no crear un atajo específico de este proveedor.

7. **Implementar webhooks** (`ManejarWebhookAsync`), si el proveedor los ofrece (verifica la capacidad `Webhooks` del paso 1): registra `SuscripcionWebhook` con su secreto, y la firma se verifica en el endpoint genérico (`ARQUITECTURA-INTEGRACIONES.md` § 6.4) — el adaptador del proveedor nunca decide por sí mismo si confía en el payload, eso ya pasó antes de que su código se ejecute.

8. **El resultado se publica, nunca se invoca directamente**: al terminar `SincronizarAsync`/`ManejarWebhookAsync`, el Orquestador es quien publica el Evento de Integración correspondiente (`ARQUITECTURA-INTEGRACIONES.md` § 6.5) — el adaptador del proveedor no llama a ningún módulo de negocio (IA, Alertas, Notificaciones) directamente. Si tu conector necesita "avisar a Alertas cuando pasa X", la respuesta es publicar un evento, no añadir una dependencia nueva al conector.

9. **Health checks**: implementa `ComprobarSaludAsync` de forma barata (una llamada ligera, no una sincronización completa) — alimenta `SaludConexionIntegracion` (`ARQUITECTURA-INTEGRACIONES.md` § 8), que es lo que ve el panel de monitorización.

10. **Pruebas**: unitarias del mapeo de campos y de `Capacidades` (sin red real); de integración contra un doble/fake del cliente HTTP del proveedor (nunca contra la API real del proveedor en CI); y al menos un test del Orquestador verificando que **rechaza** una operación fuera de las capacidades declaradas (§ 3.1) sin llegar a llamar al proveedor.

11. **Observabilidad**: nada adicional que inventar — `SincronizacionIntegracion` (auditoría de qué pasó) y `SaludConexionIntegracion` (estado en vivo) ya cubren logging/métricas de este conector si los pasos anteriores se hicieron bien. Si sientes que necesitas un log paralelo específico de este proveedor, es señal de que el modelo genérico se quedó corto — vuelve a `ARQUITECTURA-INTEGRACIONES.md`, no lo resuelvas solo en este conector.

## Señales de alarma (para el revisor de código, no solo para quien construye el conector)

- Un `if`/`switch` sobre el nombre de un proveedor fuera de `Infrastructure/Integraciones/Providers/`.
- Un conector que llama directamente a un handler de Application ajeno a integraciones (debería publicar un evento).
- Capacidades declaradas "por si acaso" que no corresponden a lo que la API del proveedor realmente soporta.
- Credenciales sin pasar por el `ValueConverter` de cifrado existente.
- Una tabla nueva sin `TenantId` que no sea `ProveedorIntegracion`/`VersionApiProveedor` (los dos únicos catálogos globales de este subsistema, ver `docs/MULTITENANCY.md` § 7).
