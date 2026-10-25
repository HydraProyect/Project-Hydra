using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Domain.Proyectos;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Recálculo en línea del estado materializado con lo que detectó <see cref="DetectorCambiosEstadoInterceptor"/> (diseño
/// § 4.2 a 4.5). Un fallo del recálculo nunca tumba la acción del usuario: dentro de la transacción de la acción va en un
/// <c>SAVEPOINT</c> (sin él, PostgreSQL deja la transacción abortada y no se podría ni confirmar ni encolar), y si falla se
/// vuelve al punto de guardado, se encola el Tenant entero en <c>RecalculosPendientes</c>, se registra el error y la acción
/// se confirma.
///
/// <para>Orden de cada recálculo, siempre el mismo (diseño § 4.4, con el cerrojo del Tenant delante):</para>
/// <list type="number">
/// <item>Solo los cambios del Tenant de la conexión (<c>app.tenant_id</c>): el servicio de recálculo lee y escribe con la
/// RLS de ese Tenant. Un cambio de otro Tenant se registra y no se recalcula aquí (lo repone el cotejo, § 7.2).</item>
/// <item>Cerrojo compartido del Tenant, sin esperar. El relleno y la cola del Tenant entero lo toman exclusivo; si lo tienen
/// (o lo esperan), el cambio va a la cola y la acción sigue (diseño § 13.8).</item>
/// <item>Sin fila en <c>VersionesEstadoTenant</c> el Tenant aún no está relleno: no se recalcula nada (el relleno lo hará
/// entero, y ningún lector toma por completo un Tenant sin esa fila, § 7.3).</item>
/// <item>Cambios de abanico de Tenant entero (Tipo de documento, tolerancia del Cliente empresarial, umbrales): a la
/// cola.</item>
/// <item>Clausura de las raíces. Por encima de <see cref="MaximoCentrosEnLinea"/> Centros o de
/// <see cref="MaximoEntidadesBajoCerrojo"/> entidades, las raíces van a la cola.</item>
/// <item>Cerrojo exclusivo de cada entidad de la clausura (Centros, Trabajadores y Empresas), todos en una sentencia y en
/// orden de clave: dos recálculos que escriben una misma fila derivada tienen en su clausura la misma entidad, así que se
/// serializan, y el segundo calcula cuando el primero ya confirmó.</item>
/// <item>La clausura otra vez, bajo los cerrojos: si creció (otra acción confirmó entretanto una relación nueva), las raíces
/// van a la cola en vez de calcular con una clausura sin cerrar.</item>
/// <item>Cálculo y escritura; la versión del Tenant sube la última (lo hace el escritor).</item>
/// </list>
/// </summary>
public sealed class RecalculoEstadoEnLinea(
    CaeManagerDbContext dbContext, IRecalculoEstadoMaterializado recalculo, ILogger<RecalculoEstadoEnLinea> logger)
{
    /// <summary>Umbral de diferido: más Centros en la clausura van a la cola (diseño § 13.6).</summary>
    public const int MaximoCentrosEnLinea = 8;

    /// <summary>Cerrojos de asesoramiento por transacción como mucho; por encima, a la cola (tabla compartida de bloqueos).</summary>
    public const int MaximoEntidadesBajoCerrojo = 400;

    /// <summary>Raíces que se encolan una a una como mucho; por encima, una entrada del Tenant entero.</summary>
    public const int MaximoRaicesEnCola = 50;

    /// <summary>
    /// Espera máxima por un cerrojo del recálculo (<c>lock_timeout</c>). Al vencer, el recálculo falla, se deshace y el
    /// Tenant va a la cola: la acción del usuario nunca espera más que esto por el estado materializado.
    /// </summary>
    public const string TopeDeEsperaDeCerrojo = "2s";

    private const string PuntoDeRecalculo = "estado_materializado";
    private const string PuntoDeCola = "estado_materializado_cola";

    internal CaeManagerDbContext Contexto => dbContext;

    /// <inheritdoc cref="CerrojosEstadoMaterializado.ClaveDeTenant"/>
    public static string ClaveDeTenant(Guid tenantId) => CerrojosEstadoMaterializado.ClaveDeTenant(tenantId);

    /// <inheritdoc cref="CerrojosEstadoMaterializado.ClaveDeEntidad"/>
    public static string ClaveDeEntidad(Guid tenantId, char ambito, Guid id) => CerrojosEstadoMaterializado.ClaveDeEntidad(tenantId, ambito, id);

    internal void RegistrarContextoAjeno() =>
        logger.LogWarning("Estado materializado: un guardado de un DbContext distinto del del ámbito no se recalcula en línea.");

    /// <summary>Dentro de la transacción explícita de la acción, antes de su COMMIT, en un SAVEPOINT.</summary>
    internal async Task ProcesarEnTransaccionAsync(CambiosEstadoDetectados cambios)
    {
        var transaccion = dbContext.Database.CurrentTransaction
                          ?? throw new InvalidOperationException("El recálculo en transacción necesita la transacción de la acción abierta.");

        try
        {
            await transaccion.CreateSavepointAsync(PuntoDeRecalculo, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // La transacción de la acción ya estaba abortada (25P02) o la conexión rota: sin punto de guardado no se
            // recalcula, y el COMMIT de la envoltura se comporta como sin el detector.
            logger.LogError(ex, "Estado materializado: no se pudo abrir el punto de guardado del recálculo. {Disparadores}", Disparadores(cambios));
            return;
        }

        try
        {
            // El tope de espera de cerrojo es de este recálculo, no de la acción: se restaura antes de liberar el punto
            // de guardado (un ROLLBACK TO SAVEPOINT ya lo deshace solo).
            var anterior = await FijarTopeDeEsperaAsync(TopeDeEsperaDeCerrojo);
            await RecalcularAsync(cambios);
            await FijarTopeDeEsperaAsync(anterior);
            await transaccion.ReleaseSavepointAsync(PuntoDeRecalculo, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Si ni el punto de guardado se puede deshacer, la conexión ya no sirve: el COMMIT fallaría igual y la estrategia
            // de reintentos repite la acción entera (diseño § 4.5). Ese es el único caso en que esto lanza.
            await transaccion.RollbackToSavepointAsync(PuntoDeRecalculo, CancellationToken.None);
            logger.LogError(ex, "Estado materializado: falló el recálculo en línea; la acción se confirma y el Tenant va a la cola. {Disparadores}",
                Disparadores(cambios));
            await EncolarTrasFalloEnTransaccionAsync(transaccion, cambios, ex);
        }
    }

    /// <summary>Tras un guardado sin transacción (ya confirmado), en una transacción propia. Nunca lanza.</summary>
    internal async Task ProcesarFueraDeTransaccionAsync(CambiosEstadoDetectados cambios)
    {
        Exception? fallo;
        try
        {
            fallo = await EnTransaccionPropiaAsync(async () =>
            {
                await FijarTopeDeEsperaAsync(TopeDeEsperaDeCerrojo);
                await RecalcularAsync(cambios);
            });
        }
        catch (Exception ex)
        {
            fallo = ex;
        }

        if (fallo is null)
            return;

        logger.LogError(fallo, "Estado materializado: falló el recálculo tras el guardado; el Tenant va a la cola. {Disparadores}",
            Disparadores(cambios));
        Exception? falloCola;
        try
        {
            falloCola = await EnTransaccionPropiaAsync(() => EncolarTenantesEnterosAsync(cambios, fallo));
        }
        catch (Exception ex)
        {
            falloCola = ex;
        }

        // Si tampoco se encola, lo repone el cotejo (diseño § 7.2); el guardado del usuario ya está confirmado.
        if (falloCola is not null)
            logger.LogError(falloCola, "Estado materializado: tampoco se pudo encolar el recálculo. {Disparadores}", Disparadores(cambios));
    }

    /// <summary>
    /// Una transacción propia por la estrategia de ejecución (la que exige <c>EnableRetryOnFailure</c> para abrirla), con el
    /// fallo del trabajo capturado dentro: la estrategia solo reintenta abrir la transacción, nunca el recálculo, que va a la
    /// cola en vez de repetirse con el usuario esperando.
    /// </summary>
    private async Task<Exception?> EnTransaccionPropiaAsync(Func<Task> trabajo)
    {
        Exception? fallo = null;
        var estrategia = dbContext.Database.CreateExecutionStrategy();
        await estrategia.ExecuteAsync(async () =>
        {
            fallo = null;
            await using var transaccion = await dbContext.Database.BeginTransactionAsync(CancellationToken.None);
            try
            {
                await trabajo();
                await transaccion.CommitAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                fallo = ex;
            }
        });
        return fallo;
    }

    /// <summary>
    /// Fija <c>lock_timeout</c> en la transacción en curso (<c>set_config(…, true)</c>) y devuelve el que había. Sin tope,
    /// un recálculo esperaría un cerrojo tanto como el tiempo de espera del comando (30 s), con la acción del usuario detenida.
    /// </summary>
    private Task<string> FijarTopeDeEsperaAsync(string tope) => CerrojosEstadoMaterializado.FijarTopeDeEsperaAsync(dbContext, tope);

    private async Task EncolarTrasFalloEnTransaccionAsync(IDbContextTransaction transaccion, CambiosEstadoDetectados cambios, Exception fallo)
    {
        await transaccion.CreateSavepointAsync(PuntoDeCola, CancellationToken.None);
        try
        {
            await EncolarTenantesEnterosAsync(cambios, fallo);
            await transaccion.ReleaseSavepointAsync(PuntoDeCola, CancellationToken.None);
        }
        catch (Exception exCola)
        {
            await transaccion.RollbackToSavepointAsync(PuntoDeCola, CancellationToken.None);
            logger.LogError(exCola, "Estado materializado: tampoco se pudo encolar el recálculo. {Disparadores}", Disparadores(cambios));
        }
    }

    private async Task EncolarTenantesEnterosAsync(CambiosEstadoDetectados cambios, Exception fallo)
    {
        // Tenga o no versión el Tenant: si el fallo fue por esperar al relleno, el cambio tiene que seguir en la cola cuando
        // el relleno acabe (diseño § 13.8). Las entradas de un Tenant sin versión las borra el propio relleno.
        if (await TenantDeLaConexionAsync() is not { } tenant || !cambios.PorTenant.ContainsKey(tenant))
            return;
        await EncolarTenantEnteroAsync(tenant, "Fallo del recálculo en línea: " + fallo.GetType().Name);
    }

    private async Task RecalcularAsync(CambiosEstadoDetectados cambios)
    {
        var tenant = await TenantDeLaConexionAsync();
        foreach (var otro in cambios.PorTenant.Keys.Where(t => t != tenant))
            logger.LogWarning("Estado materializado: cambio del Tenant {Tenant} guardado con la conexión en el Tenant {TenantConexion}; no se recalcula en línea.",
                otro, tenant);
        if (tenant is not { } tenantId || !cambios.PorTenant.TryGetValue(tenantId, out var delTenant))
            return;

        var motivo = string.Join(", ", delTenant.Disparadores);
        if (!await CerrojosEstadoMaterializado.IntentarTenantCompartidoAsync(dbContext, tenantId))
        {
            // El relleno o la cola del Tenant entero tienen (o esperan) el exclusivo: la acción no espera ni escribe sobre
            // ellos; deja su cambio en la cola, que lo recoge cuando acaben (diseño § 13.8). Tenga o no versión el Tenant: un
            // cambio durante el primer relleno tampoco se pierde.
            await EncolarSinCerrojoAsync(tenantId, delTenant, motivo);
            return;
        }

        // Sin versión, el relleno aún no ha empezado (si estuviera en marcha, tendría el exclusivo): lo leerá ya confirmado.
        if (!await TieneVersionAsync(tenantId))
            return;

        if (delTenant.TenantEntero is { } motivoTenant)
        {
            await EncolarTenantEnteroAsync(tenantId, motivoTenant);
            return;
        }

        var raices = await ResolverAsync(tenantId, delTenant);
        if (raices.EstaVacia)
            return;
        if (raices.Cuantas > MaximoRaicesEnCola)
        {
            await EncolarTenantEnteroAsync(tenantId, "Muchas raíces: " + motivo);
            return;
        }

        var clausura = await recalculo.CalcularClausuraAsync(raices, CancellationToken.None);
        if (clausura.Centros.Count > MaximoCentrosEnLinea || clausura.Entidades > MaximoEntidadesBajoCerrojo)
        {
            await EncolarRaicesAsync(tenantId, raices, $"Más de {MaximoCentrosEnLinea} Centros: " + motivo);
            return;
        }

        await BloquearAsync(ClavesDe(tenantId, clausura), compartido: false);
        var bajoCerrojo = await recalculo.CalcularClausuraAsync(raices, CancellationToken.None);
        if (!clausura.Contiene(bajoCerrojo))
        {
            await EncolarRaicesAsync(tenantId, raices, "La clausura creció bajo cerrojo: " + motivo);
            return;
        }

        await recalculo.RecalcularClausuraAsync(bajoCerrojo, CancellationToken.None);
    }

    private async Task EncolarSinCerrojoAsync(Guid tenantId, CambiosEstadoDeTenant delTenant, string motivo)
    {
        if (delTenant.TenantEntero is { } motivoTenant)
        {
            await EncolarTenantEnteroAsync(tenantId, motivoTenant);
            return;
        }

        var raices = await ResolverAsync(tenantId, delTenant);
        if (raices.EstaVacia)
            return;
        if (raices.Cuantas > MaximoRaicesEnCola)
            await EncolarTenantEnteroAsync(tenantId, "Tenant en recálculo entero; muchas raíces: " + motivo);
        else
            await EncolarRaicesAsync(tenantId, raices, "Tenant en recálculo entero: " + motivo);
    }

    /// <summary>Las raíces de los cambios, resolviendo contra la base el Centro y el titular que la entidad no lleva.</summary>
    private async Task<RaicesDeRecalculo> ResolverAsync(Guid tenantId, CambiosEstadoDeTenant c)
    {
        var centros = c.Centros.ToHashSet();
        var afectados = c.CentrosAfectados.ToHashSet();
        var trabajadores = c.Trabajadores.ToHashSet();
        var empresas = c.Empresas.ToHashSet();

        // Sin filtros globales para ver también lo borrado en la propia acción; el Tenant, explícito y además por RLS.
        if (c.Acreditaciones.Count > 0)
        {
            var documentoIds = c.Acreditaciones.Select(a => a.DocumentoId).Distinct().ToList();
            var canalIds = c.Acreditaciones.Select(a => a.CanalId).Distinct().ToList();
            var titulares = await dbContext.Set<Documento>().IgnoreQueryFilters()
                .Where(d => d.TenantId == tenantId && documentoIds.Contains(d.Id))
                .Select(d => new { d.TrabajadorId, d.EmpresaId })
                .ToListAsync(CancellationToken.None);
            trabajadores.UnionWith(titulares.Select(t => t.TrabajadorId).OfType<Guid>());
            empresas.UnionWith(titulares.Select(t => t.EmpresaId).OfType<Guid>());
            afectados.UnionWith(await dbContext.Set<CanalGestionDocumental>().IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && canalIds.Contains(x.Id))
                .Select(x => x.CentroId)
                .ToListAsync(CancellationToken.None));
        }

        var proyectoIds = c.Eventos.Concat(c.Participaciones.Select(p => p.ProyectoId)).Distinct().ToList();
        if (proyectoIds.Count > 0)
        {
            var centroDeProyecto = await dbContext.Set<Proyecto>().IgnoreQueryFilters()
                .Where(p => p.TenantId == tenantId && proyectoIds.Contains(p.Id))
                .Select(p => new { p.Id, p.CentroId })
                .ToDictionaryAsync(p => p.Id, p => p.CentroId, CancellationToken.None);
            var eventos = c.Eventos.ToList();
            centros.UnionWith(eventos.Where(centroDeProyecto.ContainsKey).Select(e => centroDeProyecto[e]));
            if (eventos.Count > 0)
                trabajadores.UnionWith(await dbContext.Set<ProyectoTecnico>().IgnoreQueryFilters()
                    .Where(p => p.TenantId == tenantId && eventos.Contains(p.ProyectoId))
                    .Select(p => p.TrabajadorId)
                    .Distinct()
                    .ToListAsync(CancellationToken.None));
            foreach (var (proyectoId, trabajadorId) in c.Participaciones)
            {
                trabajadores.Add(trabajadorId);
                if (centroDeProyecto.TryGetValue(proyectoId, out var centro))
                    afectados.Add(centro);
            }
        }

        afectados.ExceptWith(centros);
        return new RaicesDeRecalculo(centros, trabajadores, empresas) { CentrosAfectados = afectados };
    }

    private static List<string> ClavesDe(Guid tenantId, ClausuraDeRecalculo clausura) =>
        clausura.Centros.Select(id => ClaveDeEntidad(tenantId, 'C', id))
            .Concat(clausura.Trabajadores.Select(id => ClaveDeEntidad(tenantId, 'T', id)))
            .Concat(clausura.Empresas.Select(id => ClaveDeEntidad(tenantId, 'E', id)))
            .ToList();

    private Task BloquearAsync(IReadOnlyCollection<string> claves, bool compartido) =>
        CerrojosEstadoMaterializado.BloquearAsync(dbContext, claves, compartido);

    private Task<Guid?> TenantDeLaConexionAsync() => CerrojosEstadoMaterializado.TenantDeLaConexionAsync(dbContext);

    private Task<bool> TieneVersionAsync(Guid tenantId) => CerrojosEstadoMaterializado.TieneVersionAsync(dbContext, tenantId);

    private Task EncolarTenantEnteroAsync(Guid tenantId, string motivo) =>
        ColaEstadoMaterializado.EncolarTenantEnteroAsync(dbContext, tenantId, motivo);

    private Task EncolarRaicesAsync(Guid tenantId, RaicesDeRecalculo raices, string motivo) =>
        ColaEstadoMaterializado.EncolarRaicesAsync(dbContext, tenantId, raices, motivo);

    private static string Disparadores(CambiosEstadoDetectados cambios) =>
        string.Join("; ", cambios.PorTenant.Select(t => $"{t.Key}: {string.Join(", ", t.Value.Disparadores)}"));
}
