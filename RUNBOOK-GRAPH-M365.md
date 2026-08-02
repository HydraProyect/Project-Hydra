# Runbook — tenant Microsoft 365 dev para la ingesta Graph de Comunicaciones (P2 #26)

Este documento cubre la configuración ya hecha en un tenant Microsoft 365 Developer, preparada para retomar P2 #26 de `docs/business/MATURITY_REVIEW.md`: "Ingesta Graph real de Comunicaciones **o congelar el módulo y no venderlo**" — hoy el módulo está congelado (`ffc145d`, ya en `main`); este tenant existe para poder revertir esa decisión con una integración real, no para usarlo todavía.

**Nada de lo que hay abajo es secreto** salvo donde se indica explícitamente — Tenant ID y Application (client) ID no son credenciales, son identificadores públicos dentro del propio token/request. El secreto de cliente en sí **no vive en este documento ni en el repo**; ver "Dónde vive el secreto" más abajo.

## Tenant

- Dominio: `<DOMINIO_DEV>`
- Tenant (Directory) ID: `<TENANT_ID>`
- Cuenta de Global Admin: `<ADMIN_UPN>`
- Programa: Microsoft 365 Developer (hasta 25 usuarios con licencia)

## App registration

- Nombre: `hydra` (Entra ID → App registrations)
- Application (client) ID: `<CLIENT_ID>`
- Tipo de cuenta: solo este tenant ("Solo mi organización")
- Credencial: un secreto de cliente (no un certificado) — ver expiración en el propio portal antes de que caduque, Entra ID no avisa por email por defecto en el plan Developer.

### Permisos de Graph concedidos

**Application permissions** (no Delegated — la app corre sin usuario interactivo, pensada para un `BackgroundService` igual que `ProcesadorAnalisisDocumentoHostedService`):

- `Mail.Read`
- `Mail.Send`

Con consentimiento de administrador ya otorgado (columna "Status" en verde en API permissions).

### Acotado a un solo buzón — Application Access Policy

Sin esto, `Mail.Read`/`Mail.Send` de Application da acceso a **todos** los buzones del tenant. Se acotó vía Exchange Online PowerShell:

- Grupo de seguridad habilitado para correo: `GrupoAccesoBuzonCae`, con un único miembro: `<BUZON_MONITOREADO>`
- Política aplicada:
  ```powershell
  New-ApplicationAccessPolicy -AppId "<CLIENT_ID>" `
    -PolicyScopeGroupId "GrupoAccesoBuzonCae" `
    -AccessRight RestrictAccess `
    -Description "hydra: solo buzon.cae"
  ```
- Verificado con `Test-ApplicationAccessPolicy`: `Granted` sobre `buzon.cae@...`, `Denied` sobre cualquier otro buzón del tenant.

Si el buzón monitoreado cambia alguna vez, hay que actualizar la membresía de `GrupoAccesoBuzonCae` (no la política en sí).

## Usuarios de prueba (6 en total, dentro del límite de 25 del plan Developer)

| Usuario | Rol en las pruebas |
|---|---|
| `<ADMIN_UPN>@...` | Global Admin del tenant (no participa en los escenarios de prueba en sí) |
| `buzon.cae@...` | El buzón que la integración va a monitorear — target real de `Mail.Read`/`Mail.Send` |
| `cliente.prueba1@...` / `cliente.prueba2@...` | Simulan remitentes externos escribiendo al buzón — para probar creación de `ConversacionCorreo`, threading, adjuntos |
| `gestor.prueba@...` | Simula al Gestor CAE respondiendo desde Hydra — para probar el envío saliente vía Graph |
| `spam.prueba@...` | Remitente "ruidoso" — para probar que la cola de triage (`VisibilidadTriageTests`) separa bien lo que corresponde |

## Dónde vive el secreto

El valor del secreto de cliente **no se guardó en este documento, en el repo, ni en el chat de la sesión que lo generó** (se mostró una sola vez en el portal de Entra ID). Antes de usarlo en código:

1. Debe entrar como variable de entorno, no en `appsettings.json` — mismo criterio que el resto de `DEPLOY.md` (ver tabla de variables ahí).
2. El propio valor en reposo dentro de la app debe pasar por `IDataProtector`, siguiendo el patrón ya establecido para credenciales externas (`CredencialAccesoEmpresa`/`CredencialAccesoSubcontrata`, ver `ARCHITECTURE.md` § cifrado de credenciales) — no un `ClientSecretCredential` construido directo desde una variable de entorno en claro si el flujo del código llega a persistir algo derivado de él.

Nombres de variable propuestos para cuando se implemente (siguiendo la convención `Seccion__Subseccion` ya usada en `DEPLOY.md`):

| Variable | Valor |
|---|---|
| `Comunicaciones__Graph__TenantId` | `<TENANT_ID>` |
| `Comunicaciones__Graph__ClientId` | `<CLIENT_ID>` |
| `Comunicaciones__Graph__ClientSecret` | (el secreto — nunca en claro en el repo) |
| `Comunicaciones__Graph__BuzonMonitoreado` | `<BUZON_MONITOREADO>` |

## Qué falta para que P2 #26 deje de estar congelado

Esto es setup de plataforma, no la implementación — sigue pendiente todo el código:

1. Descongelar el módulo: revertir el ocultamiento de navegación de `ffc145d` (`NavMenu.razor`, `Bandeja.razor.cs`, `Macros.razor.cs`).
2. Cliente de Graph real con `ClientSecretCredential` (Azure.Identity) + Microsoft Graph SDK, autenticando con las variables de arriba.
3. Mecanismo de ingesta: para un tenant dev, más simple empezar por **delta query** (polling) que por suscripción/webhook — un webhook exige un endpoint HTTPS público alcanzable desde Microsoft, que complica el entorno de desarrollo local.
4. Mapear mensajes de Graph a `ConversacionCorreo`/`MensajeCorreo` (el modelo de dominio ya existe, congelado junto con la UI).
5. Probar los 5 escenarios de la tabla de usuarios de prueba de arriba antes de dar por cerrado el hallazgo.
