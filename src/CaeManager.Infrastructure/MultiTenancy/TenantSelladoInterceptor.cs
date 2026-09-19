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
    /// <summary>
    /// Valor de <c>app.tenant_id</c> que había en la sesión ANTES de que este
    /// <c>SaveChanges</c> lo pisara para sellar una fila de Identity con un
    /// tenant distinto del de sesión (ver <see cref="SellarYValidarAsync"/>).
    /// <c>null</c> cuando este <c>SaveChanges</c> no tocó la variable de
    /// sesión. El interceptor es <c>AddScoped</c> (una instancia por
    /// <c>DbContext</c>/petición, ver <c>ConfiguracionDeContexto</c>), así que
    /// un campo de instancia es seguro: solo un <c>SaveChanges</c> está en
    /// vuelo a la vez para esa instancia. Se restaura en
    /// <c>SavedChanges(Async)</c>/<c>SaveChangesFailed(Async)</c> —en los DOS
    /// caminos, éxito y fallo—, porque la conexión puede reutilizarse dentro
    /// de la misma petición (p. ej. un reintento de <c>ExecutionStrategy</c>)
    /// y dejarla con el tenant equivocado filtraría el resto de la petición
    /// por el tenant que NO es.
    /// </summary>
    private string? _tenantDeSesionARestaurar;

    /// <summary>
    /// Cuántas veces esta ejecución de <c>SaveChanges</c> abrió explícitamente
    /// la conexión vía <see cref="FijarTenantEnSesionRlsAsync"/> sin cerrarla
    /// todavía (hallazgo P2 de Codex, 7ª ronda). Cada apertura explícita
    /// incrementa el contador de referencia interno de EF Core
    /// (<c>RelationalConnection</c>); si no se empareja con el mismo número de
    /// cierres, ese contador nunca vuelve a 0 y la conexión física de Npgsql
    /// queda retenida fuera del pool durante toda la vida del
    /// <c>DbContext</c> —que en Blazor Server es la vida del circuito, no de
    /// la petición—. Se cierra tantas veces como se abrió en
    /// <see cref="RestaurarTenantDeSesionSiHizoFaltaAsync"/>, el único punto
    /// donde ya no queda ningún comando pendiente de este <c>SaveChanges</c>.
    /// </summary>
    private int _aperturasRlsPendientes;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
            await SellarYValidarAsync(context, cancellationToken);

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
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
            SellarYValidarAsync(context, CancellationToken.None).GetAwaiter().GetResult();

        return base.SavingChanges(eventData, result);
    }

    /// <summary>
    /// Restaura <c>app.tenant_id</c> al valor de sesión que había antes de
    /// este <c>SaveChanges</c>, si <see cref="SellarYValidarAsync"/> lo pisó.
    /// Ver <see cref="_tenantDeSesionARestaurar"/> para el porqué de
    /// restaurar en éxito Y en fallo.
    /// </summary>
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
            await RestaurarTenantDeSesionSiHizoFaltaAsync(context, cancellationToken);

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>Versión síncrona — ver <see cref="SavingChanges"/> para el porqué.</summary>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is DbContext context)
            RestaurarTenantDeSesionSiHizoFaltaAsync(context, CancellationToken.None).GetAwaiter().GetResult();

        return base.SavedChanges(eventData, result);
    }

    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
            await RestaurarTenantDeSesionSiHizoFaltaAsync(context, cancellationToken);

        await base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <summary>Versión síncrona — ver <see cref="SavingChanges"/> para el porqué.</summary>
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is DbContext context)
            RestaurarTenantDeSesionSiHizoFaltaAsync(context, CancellationToken.None).GetAwaiter().GetResult();

        base.SaveChangesFailed(eventData);
    }

    /// <summary>
    /// El parámetro <paramref name="cancellationToken"/> recibido de
    /// <c>SavedChanges(Async)</c>/<c>SaveChangesFailed(Async)</c> se ignora a
    /// propósito (hallazgo P2 de Codex, 8ª ronda): si el <c>SaveChanges</c>
    /// que pisó <c>app.tenant_id</c> se cancela, ese mismo token llegaría ya
    /// cancelado aquí, y una restauración que lanza <c>OperationCanceledException</c>
    /// antes de completar dejaría el tenant RLS de sesión fijado en el
    /// temporal (el propietario de la cuenta auditada) para el resto de
    /// peticiones de este mismo <c>DbContext</c> —de vida larga, un circuito
    /// Blazor Server—, filtrándolas contra el tenant equivocado. Restaurar es
    /// limpieza, no trabajo cancelable: siempre corre con
    /// <see cref="CancellationToken.None"/>.
    /// </summary>
    private async Task RestaurarTenantDeSesionSiHizoFaltaAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (_tenantDeSesionARestaurar is null) return;

        var valorARestaurar = _tenantDeSesionARestaurar;
        try
        {
            await FijarTenantEnSesionRlsAsync(context, valorARestaurar, CancellationToken.None);
            // Solo se borra el centinela cuando la restauración YA completó:
            // si falla, el valor original queda disponible para el próximo
            // intento (ver el ??= en SellarYValidarAsync) en vez de perderse.
            _tenantDeSesionARestaurar = null;
        }
        finally
        {
            // En un finally no cancelable: si FijarTenantEnSesionRlsAsync de
            // arriba lanzó, las aperturas explícitas de conexión que ya
            // acumulamos (sellar + este intento de restaurar) no deben
            // quedar sin cerrar — sería el mismo leak del hallazgo P2 de la
            // 7ª ronda, ahora por la ruta de fallo.
            while (_aperturasRlsPendientes > 0)
            {
                await context.Database.CloseConnectionAsync();
                _aperturasRlsPendientes--;
            }
        }
    }

    private async Task SellarYValidarAsync(DbContext context, CancellationToken cancellationToken)
    {
        var tenantId = tenantActual.TenantId;

        // Solo se propaga una vez por SaveChanges, y solo cuando hace falta:
        // un roundtrip extra a Postgres por cada fila auditada de Identity
        // sería desperdiciado si dos filas del mismo lote resuelven el mismo
        // tenant, que es el caso único que hoy produce este código (todas las
        // filas de Identity de un SaveChanges pertenecen a la MISMA cuenta:
        // UserManager opera sobre un ApplicationUser a la vez, y sus roles,
        // logins y tokens cuelgan de él). Si algún día un mismo SaveChanges
        // auditara cuentas de DOS tenants distintos a la vez, esta variable
        // de sesión solo reflejaría la última y RLS rechazaría las demás con
        // 42501 —la política compara contra un único app.tenant_id por
        // conexión—; el lote entero se revertiría, así que sería un fallo
        // ruidoso, no una fila sellada con el tenant equivocado. Confirmado
        // por la revisión de Codex de la misión N6/V4, que no encontró
        // ningún camino de producción que construya ese lote: queda fuera de
        // alcance ampliarlo sin que aparezca un caso real.
        Guid? tenantYaPropagadoARls = null;

        // ToList: la rama de reclasificación de abajo cambia el State de una
        // entrada, y eso no puede hacerse mientras se enumera el ChangeTracker.
        foreach (var entrada in context.ChangeTracker.Entries<EntidadConTenant>().ToList())
        {
            switch (entrada.State)
            {
                case EntityState.Added:
                    // El tenant PROPIETARIO de la cuenta auditada manda
                    // siempre que exista (Identity, ver
                    // ResolverTenantDeIdentidadAuditada) — nunca el tenant
                    // del CONTEXTO/sesión. Antes este orden estaba invertido
                    // (tenantId ?? fallback): un Gestor CAE de un Operador
                    // CAE externo, operando en el Workspace operativo
                    // derivado de un Tenant beneficiario
                    // (TenantActual.TenantId = clienteActivoSeleccionado.
                    // TenantIdSeleccionado ?? tenantId, ver TenantActual.cs),
                    // que cambiaba su propio tema/teléfono/2FA generaba una
                    // fila de auditoría sellada con el TenantId del
                    // BENEFICIARIO en vez del Tenant propietario real de su
                    // cuenta — el administrador del beneficiario veía en SU
                    // auditoría la actividad de una cuenta ajena: mezcla del
                    // plano de Operación con el de Propiedad (ADR-011),
                    // hallazgo ALTA de sesión coordinadora, confirmado por
                    // PRUEBA DIRECTA antes de corregir (ver
                    // PropagacionTenantRlsSinSesionTests). Para el resto de
                    // entidades (dominio), ResolverTenantDeIdentidadAuditada
                    // siempre devuelve null, así que el comportamiento no
                    // cambia: sigue siendo tenantId (el único caso posible).
                    var tenantParaEsta = ResolverTenantDeIdentidadAuditada(context, entrada.Entity) ?? tenantId;
                    if (tenantParaEsta is null)
                        throw new InvalidOperationException(
                            $"No se puede crear una entidad de tipo {entrada.Entity.GetType().Name} sin un tenant resuelto (ver ITenantActual).");

                    // Hallazgo P1 de Codex (5ª ronda) + hallazgo ALTA de
                    // sesión coordinadora: sin esto, el valor de abajo queda
                    // solo en el objeto .NET — la política RLS de
                    // "RegistrosAuditoria" compara contra
                    // current_setting('app.tenant_id'), que
                    // TenantRlsConnectionInterceptor solo fija UNA VEZ, al
                    // abrir la conexión, con el tenant de SESIÓN (que puede
                    // ser null —sin sesión— o distinto del propietario real
                    // —workspace delegado—). Bajo el rol restringido
                    // cae_app_runtime (confirmado en producción, ver
                    // hydra-rls-fallo-cerrado-prerrequisito-despliegue), el
                    // INSERT lo rechaza Postgres con 42501 si no coinciden.
                    // Se restaura el valor de sesión original en
                    // SavedChanges(Async)/SaveChangesFailed(Async) — ver
                    // _tenantDeSesionARestaurar.
                    if (tenantParaEsta != tenantId && tenantYaPropagadoARls != tenantParaEsta)
                    {
                        await FijarTenantEnSesionRlsAsync(context, tenantParaEsta.Value.ToString(), cancellationToken);
                        tenantYaPropagadoARls = tenantParaEsta;
                        _tenantDeSesionARestaurar ??= tenantId?.ToString() ?? string.Empty;
                    }

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
    /// Actualiza <c>app.tenant_id</c> EN LA MISMA conexión que va a ejecutar
    /// este <c>SaveChanges</c> (o que acaba de ejecutarlo, para restaurar) —
    /// <c>TenantRlsConnectionInterceptor</c> solo la fija una vez, al abrir la
    /// conexión, con el tenant que <see cref="ITenantActual"/> resolvía en
    /// ESE momento. La política <c>aislamiento_tenant</c> de
    /// "RegistrosAuditoria" (migración <c>HabilitarRlsPostgres</c>) compara el
    /// <c>TenantId</c> de la fila contra esa variable de sesión con
    /// <c>WITH CHECK</c>; sin este paso, una fila cuyo tenant difiera del de
    /// sesión (sin sesión, o con un Workspace operativo derivado
    /// seleccionado) la rechaza Postgres con 42501 bajo el rol restringido
    /// <c>cae_app_runtime</c> — el propio interceptor de aplicación (que no ve
    /// RLS) no tiene forma de saberlo por sí solo.
    ///
    /// <c>OpenConnectionAsync</c> usa el contador de referencias de EF Core
    /// (no el <c>ConnectionState</c> crudo): es seguro llamarlo aunque la
    /// conexión ya esté abierta. A diferencia de lo que afirmaba una versión
    /// anterior de este comentario, SÍ hace falta un <c>CloseConnectionAsync</c>
    /// simétrico —hallazgo P2 de Codex (7ª ronda)—: con un <c>DbContext</c>
    /// de vida larga (un circuito Blazor Server, no solo una petición), no
    /// cerrar cada apertura explícita deja el contador de referencia sin
    /// volver nunca a 0, y la conexión física de Npgsql queda retenida fuera
    /// del pool indefinidamente. Este método solo incrementa el contador
    /// propio (<see cref="_aperturasRlsPendientes"/>); quien cierra es
    /// <see cref="RestaurarTenantDeSesionSiHizoFaltaAsync"/>, el único punto
    /// donde ya no queda ningún comando pendiente de este <c>SaveChanges</c>.
    /// <c>valorTenantId</c> acepta cadena vacía a propósito: es el mismo
    /// valor centinela que usa <c>TenantRlsConnectionInterceptor</c> para
    /// "sin tenant" (fallo cerrado, <c>NULLIF(..., '')::uuid</c> da
    /// <c>NULL</c>).
    /// </summary>
    private async Task FijarTenantEnSesionRlsAsync(DbContext context, string valorTenantId, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        _aperturasRlsPendientes++;

        var conexion = context.Database.GetDbConnection();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config('app.tenant_id', @tenantId, false);";
        var parametro = comando.CreateParameter();
        parametro.ParameterName = "tenantId";
        parametro.Value = valorTenantId;
        comando.Parameters.Add(parametro);
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Solo para <see cref="RegistroAuditoria"/> de las excepciones de
    /// Identity que <c>AuditoriaInterceptor</c> audita fuera del namespace de
    /// dominio (<c>ApplicationUser</c>, <c>IdentityUserRole{Guid}</c>,
    /// <c>IdentityUserLogin{Guid}</c>, <c>IdentityUserToken{Guid}</c> — ver
    /// <c>EntidadTipoAuditoria</c>, la única fuente de esos nombres). A
    /// diferencia de una entidad de dominio, cuyo
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
    /// está en el mismo <c>DbContext</c> que el <c>RegistroAuditoria</c> que
    /// lo audita: o es la entidad que disparó su creación (alta/edición de la
    /// cuenta), o es el usuario que <c>UserManager</c> recibió como argumento
    /// y que este mismo contexto cargó antes de escribir su rol, su login
    /// externo o su token (<c>UserStore</c> opera sobre el
    /// <c>CaeManagerDbContext</c> del scope, ver
    /// <c>AddEntityFrameworkStores</c>). Por eso basta con buscarlo entre las
    /// entradas del <c>ChangeTracker</c> —incluidas las <c>Unchanged</c>, que
    /// es como queda un usuario recién cargado por <c>FindByIdAsync</c>—: no
    /// añade una consulta a base de datos ni ninguna confianza nueva, lee un
    /// valor que la propia operación ya tenía delante. Si no se
    /// encuentra, sigue fallando cerrado como cualquier otra entidad: el
    /// tenant lo pondrá la sesión, o no habrá tenant y el <c>SaveChanges</c>
    /// se rechazará en vez de escribir una fila que no se sabe de quién es.
    /// </summary>
    private static Guid? ResolverTenantDeIdentidadAuditada(DbContext context, object entidad)
    {
        if (entidad is not RegistroAuditoria registro) return null;
        if (!EntidadTipoAuditoria.TodosLosDeIdentidad.Contains(registro.EntidadTipo)) return null;

        var usuario = context.ChangeTracker.Entries<ApplicationUser>()
            .FirstOrDefault(e => e.Entity.Id == registro.EntidadId)?.Entity;

        return usuario?.TenantId is { } tenantIdUsuario && tenantIdUsuario != Guid.Empty ? tenantIdUsuario : null;
    }
}
