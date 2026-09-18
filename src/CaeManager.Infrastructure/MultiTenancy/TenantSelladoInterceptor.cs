using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CaeManager.Infrastructure.MultiTenancy;

/// <summary>
/// Sella <c>TenantId</c> en toda entidad nueva desde <see cref="ITenantActual"/>
/// (ver docs/MULTITENANCY.md § 4.3) y rechaza cualquier modificación o
/// eliminación de una entidad que pertenezca a otro tenant — defensa en
/// profundidad además del filtro global de lectura (ver
/// <c>CaeManagerDbContext.OnModelCreating</c>), para el caso de una entidad
/// cargada por una vía que no pasó por el filtro (p. ej. un
/// <c>IgnoreQueryFilters()</c> justificado y revisado). Los Commands nunca
/// asignan <c>TenantId</c> directamente — es exclusivo de este interceptor,
/// mismo principio arquitectónico que <c>AuditoriaInterceptor</c> para los
/// campos de auditoría.
/// </summary>
public class TenantSelladoInterceptor(ITenantActual tenantActual) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
            SellarYValidar(context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// La versión síncrona también sella (hallazgo N-15 de
    /// INFORME-AUDITORIA-2.md). Hoy no hay ningún <c>SaveChanges()</c>
    /// síncrono en el código, así que sobrescribir solo la asíncrona era
    /// inocuo — pero el día que aparezca uno, saltarse el sellado no daría
    /// ningún error: guardaría la fila con <c>TenantId</c> vacío o permitiría
    /// modificar la de otro tenant, en silencio. Un agujero que se abre por
    /// omisión no debería depender de que nadie escriba una línea corriente.
    /// </summary>
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is DbContext context)
            SellarYValidar(context);

        return base.SavingChanges(eventData, result);
    }

    private void SellarYValidar(DbContext context)
    {
        var tenantId = tenantActual.TenantId;

        // ToList: la rama de reclasificación de abajo cambia el State de una
        // entrada, y eso no puede hacerse mientras se enumera el ChangeTracker.
        foreach (var entrada in context.ChangeTracker.Entries<EntidadConTenant>().ToList())
        {
            switch (entrada.State)
            {
                case EntityState.Added:
                    var tenantParaEsta = tenantId ?? ResolverTenantDeIdentidadAuditada(context, entrada.Entity);
                    if (tenantParaEsta is null)
                        throw new InvalidOperationException(
                            $"No se puede crear una entidad de tipo {entrada.Entity.GetType().Name} sin un tenant resuelto (ver ITenantActual).");

                    entrada.Property(nameof(EntidadConTenant.TenantId)).CurrentValue = tenantParaEsta.Value;
                    break;

                case EntityState.Modified when (Guid)entrada.Property(nameof(EntidadConTenant.TenantId)).OriginalValue! == Guid.Empty:
                    // Entidad NUEVA que el fixup de DetectChanges clasificó
                    // como Modified: las entidades hijas de un agregado
                    // cargado (p. ej. el segundo Mensaje de un hilo ya
                    // existente, vía Conversacion.AgregarMensaje) las
                    // descubre EF por la navegación, no por un Add explícito,
                    // y como Entity asigna el Guid en el constructor la clave
                    // "ya está puesta" y EF asume que la fila existe. Un
                    // TenantId ORIGINAL vacío no puede venir de la base de
                    // datos (columna NOT NULL, sellada en el insert), así que
                    // es la firma inequívoca de este caso — se reclasifica
                    // como inserción y se sella. Sin esto, el segundo mensaje
                    // de cualquier conversación cargada moría aquí con "otro
                    // tenant" (detectado verificando WhatsApp end-to-end,
                    // 2026-08-04; afectaba igual a la ingesta de correo M365 —
                    // ver AgregarMensajeAConversacionCargadaTests). Una fila
                    // REAL de otro tenant nunca entra por esta rama: su
                    // original viene de BD y no está vacío, y sigue cayendo
                    // al rechazo de abajo.
                    if (tenantId is null)
                        throw new InvalidOperationException(
                            $"No se puede crear una entidad de tipo {entrada.Entity.GetType().Name} sin un tenant resuelto (ver ITenantActual).");

                    entrada.State = EntityState.Added;
                    entrada.Property(nameof(EntidadConTenant.TenantId)).CurrentValue = tenantId.Value;
                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    var tenantOriginal = (Guid)entrada.Property(nameof(EntidadConTenant.TenantId)).OriginalValue!;
                    if (tenantOriginal != tenantId)
                        throw new InvalidOperationException(
                            $"No se puede modificar una entidad de tipo {entrada.Entity.GetType().Name} perteneciente a otro tenant.");
                    break;
            }
        }
    }

    /// <summary>
    /// Solo para <see cref="RegistroAuditoria"/> de las dos excepciones de
    /// Identity que <c>AuditoriaInterceptor</c> audita fuera del namespace de
    /// dominio (<c>ApplicationUser</c>, <c>IdentityUserRole{Guid}</c>, ver su
    /// comentario de clase). A diferencia de una entidad de dominio, cuyo
    /// TenantId solo puede venir de una sesión resuelta —nunca de la propia
    /// entidad, que sería confiar en un dato que el llamante controla—, aquí
    /// el tenant no es una decisión de autorización: es un hecho que ya vive
    /// en la fila que se está auditando (<c>ApplicationUser.TenantId</c>, NOT
    /// NULL). <c>UserManager</c> escribe esa fila en varios caminos sin
    /// sesión ni <c>AmbitoTenantExplicito</c> —un intento de contraseña
    /// incorrecto en <c>Login.razor</c> incrementa <c>AccessFailedCount</c>,
    /// <c>RestablecerContrasena.razor.cs</c> escribe desde un enlace anónimo,
    /// el alta por SSO de <c>IdentityEndpointsExtensions</c> corre antes de
    /// que exista sesión de CAE Manager—; sin esta excepción, el fallo
    /// cerrado de arriba revertía el <c>SaveChanges</c> entero y esos tres
    /// caminos dejaban de funcionar (hallazgo de Codex antes de abrir la PR
    /// que introdujo la auditoría de Identity).
    ///
    /// El <see cref="ApplicationUser"/> referenciado por <c>EntidadId</c>
    /// SIEMPRE está en el mismo <c>ChangeTracker</c> que el
    /// <c>RegistroAuditoria</c> que lo audita —es la entidad que disparó su
    /// creación, en el mismo <c>SaveChanges</c>— así que esto no añade
    /// ninguna consulta a base de datos ni ninguna confianza nueva: lee un
    /// valor que la propia operación ya tenía delante. Si no se encuentra
    /// (no debería ocurrir mientras solo <c>AuditoriaInterceptor</c> cree
    /// estas dos filas), sigue fallando cerrado como cualquier otra entidad.
    /// </summary>
    private static Guid? ResolverTenantDeIdentidadAuditada(DbContext context, object entidad)
    {
        if (entidad is not RegistroAuditoria registro) return null;
        if (registro.EntidadTipo is not ("Usuario" or "RolDeUsuario")) return null;

        var usuario = context.ChangeTracker.Entries<ApplicationUser>()
            .FirstOrDefault(e => e.Entity.Id == registro.EntidadId)?.Entity;

        return usuario?.TenantId is { } tenantIdUsuario && tenantIdUsuario != Guid.Empty ? tenantIdUsuario : null;
    }
}
