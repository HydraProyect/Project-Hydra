using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Tenants;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Configuracion;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Visitas;

/// <summary>
/// Fase F: avisa por hora (no por correo en v1 — solo la campana,
/// <see cref="NotificacionUsuario"/>) cuando un tenant tiene gestiones
/// urgentes de visita pendientes (Visitas dentro de la ventana mínima de
/// validación, o sugerencias de visita sorpresa sin confirmar) — mismo
/// patrón de elección de líder que <see cref="Alertas.EnvioAlertasVencimientoHostedService"/>,
/// reutilizando el criterio de <see cref="GestionesUrgentesDeVisita"/>, el mismo de la cola de Mi
/// trabajo, en vez de reimplementarlo aquí.
///
/// Alcance: el Tenant entero, ya acotado por <see cref="AmbitoTenantExplicito"/>. Visitas y
/// sugerencias se calculan con <c>CalcularAsync</c> y <c>centroIdsVisibles: null</c>, sin pasar por
/// la Query de la cola ni por <see cref="IAlcanceDatosService"/>: ese servicio depende del usuario
/// de la sesión, inexistente en un job de fondo, y sin usuario el alcance es vacío, así que el aviso
/// nunca saldría y el trabajo se registraría «exitoso, 0 evaluados».
///
/// Destinatarios: quienes ejercen Administrador o DireccionCae en el Tenant, porque el recuento es
/// del Tenant entero (ver <see cref="DestinatariosDelAviso"/>). Mismos roles (y misma
/// limitación documentada) que el resumen de alertas por correo, que aún mira el rol de
/// origen del Operador Delegado — atribuir
/// una gestión urgente a "el" Gestor CAE responsable exigiría resolver
/// Trabajador/Centro → Empresa/Subcontrata → Cliente, y esa relación puede
/// ser N:M (ver el comentario de EnvioAlertasVencimientoHostedService).
/// Sin dedupe entre avisos: es deliberadamente un aviso por hora mientras la
/// condición se mantenga, no una notificación única por gestión — el propio
/// nombre del pedido ("avisos por hora") lo pide así.
/// </summary>
public class VigilanciaVisitasUrgentesHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<VigilanciaVisitasUrgentesHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // P41c: todo lo que haga este servicio de fondo lo hace la propia
        // plataforma, y la auditoría lo conserva como tal. Se declara aquí, en el
        // punto de entrada, y no alrededor de cada guardado: así ningún camino
        // interno —ni uno añadido después— se queda fuera por olvido.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        await AvisarATodosLosTenantsAsync(stoppingToken);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await AvisarATodosLosTenantsAsync(stoppingToken);
    }

    private async Task AvisarATodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("vigilancia-visitas-urgentes", ProcesarTodosLosTenantsAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de vigilancia de gestiones urgentes de visita.");
        }
    }

    /// <summary>Un ciclo sobre todos los Tenants activos, sin la elección de líder ni la espera.</summary>
    internal async Task ProcesarTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        List<Guid> tenantsActivos;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenantsActivos = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Where(t => t.Estado == EstadoTenant.Activo)
                .Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        await ProcesarTenantsAsync(tenantsActivos, stoppingToken);
    }

    internal async Task ProcesarTenantsAsync(IEnumerable<Guid> tenantIds, CancellationToken stoppingToken)
    {
        foreach (var tenantId in tenantIds)
        {
            stoppingToken.ThrowIfCancellationRequested();

            // Aislado igual que EnvioAlertasVencimientoHostedService: el fallo de un
            // Tenant no deja sin ciclo a los siguientes. Aquí llega lo que falla fuera
            // del registro de la ejecución: el interruptor o el propio registro del fallo.
            try
            {
                await ProcesarTenantAsync(tenantId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló la vigilancia de gestiones urgentes de visita del tenant {TenantId}; se continúa con el resto de tenants en este ciclo.",
                    tenantId);
            }
        }
    }

    private async Task ProcesarTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);

        var registroAutomatizaciones = ambito.ServiceProvider.GetRequiredService<IRegistroAutomatizacionesService>();
        if (!await registroAutomatizaciones.EstaActivoAsync(CatalogoAutomatizaciones.VigilanciaVisitasUrgentes, stoppingToken))
            return;

        try
        {
            var gestionesUrgentes = await AvisarTenantAsync(ambito, stoppingToken);
            await registroAutomatizaciones.RegistrarEjecucionAsync(
                CatalogoAutomatizaciones.VigilanciaVisitasUrgentes, exitosa: true, stoppingToken,
                elementosEvaluados: gestionesUrgentes);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Una parada del proceso no es una ejecución fallida.
            throw;
        }
        catch (Exception ex)
        {
            // No se relanza: el fallo queda registrado y este Tenant ya ha terminado su ciclo.
            logger.LogError(ex, "Falló la vigilancia de gestiones urgentes de visita del tenant {TenantId}.", tenantId);
            await RegistrarFalloAsync(ex.Message, stoppingToken);
        }
    }

    /// <summary>
    /// En un ámbito nuevo, no en el que ha fallado: su DbContext conserva las notificaciones que no
    /// se pudieron guardar, y registrar con él reintentaría ese guardado y perdería el fallo real.
    /// </summary>
    private async Task RegistrarFalloAsync(string mensajeError, CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        await ambito.ServiceProvider.GetRequiredService<IRegistroAutomatizacionesService>().RegistrarEjecucionAsync(
            CatalogoAutomatizaciones.VigilanciaVisitasUrgentes, exitosa: false, stoppingToken, mensajeError: mensajeError);
    }

    /// <summary>
    /// Cuántas gestiones urgentes hay en el Tenant entero. Recorre todas las páginas: las Visitas en
    /// curso son candidatas, van delante por fecha y no cuentan, así que una sola página podría
    /// quedarse sin ninguna urgente de verdad.
    /// </summary>
    protected virtual async Task<int> ContarGestionesUrgentesAsync(IServiceScope ambito, CancellationToken stoppingToken)
    {
        var dbContext = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var alcanceDatos = ambito.ServiceProvider.GetRequiredService<IAlcanceDatosService>();

        // Instanciados directos, sin pasar por MediatR: igual que en
        // EnvioAlertasVencimientoHostedService, CalcularAsync es el punto de
        // entrada explícito para esto, y el alcance solo lo usaría Handle.
        var visitasHandler = new ObtenerVisitasQueryHandler(
            dbContext, dbContext, dbContext, dbContext, dbContext, dbContext, alcanceDatos, dbContext);
        var sugerenciasHandler = new ObtenerSugerenciasVisitaCorreoPendientesQueryHandler(dbContext, dbContext, dbContext, alcanceDatos);

        var visitas = new List<VisitaListaDto>();
        var numeroPagina = 1;
        ResultadoPaginado<VisitaListaDto> pagina;
        do
        {
            pagina = await visitasHandler.CalcularAsync(
                GestionesUrgentesDeVisita.ConsultaCandidatas(numeroPagina++), centroIdsVisibles: null, stoppingToken);
            visitas.AddRange(pagina.Elementos);
        }
        while (pagina.Elementos.Count > 0 && visitas.Count < pagina.TotalElementos);

        var sugerencias = await sugerenciasHandler.CalcularAsync(centroIdsVisibles: null, stoppingToken);
        var parametros = await dbContext.ParametrosSistema.SingleAsync(stoppingToken);
        var hoy = DiaDeNegocio.Hoy();

        return visitas.Count(GestionesUrgentesDeVisita.EsUrgente)
            + sugerencias.Count(s => GestionesUrgentesDeVisita.EsUrgente(s, hoy, parametros.HorasAvisoVisita, parametros.HorasCriticasVisita));
    }

    /// <summary>
    /// <c>ObtenerVisiblesEnRolAsync</c> mira el rol de origen, y el de un Operador Delegado es el de
    /// otra organización: un Administrador del Operador CAE externo sin Asignación de Operación del
    /// Tenant entero, o el personal de Soporte TALVEG en su ventana, no ejercen ese rol aquí y no deben recibir el
    /// recuento del Tenant entero. El rol ejercido solo estrecha a quién se avisa: no concede acceso.
    /// </summary>
    internal static IReadOnlyList<CuentaVisibleEnRol> DestinatariosDelAviso(
        IEnumerable<CuentaVisibleEnRol> administradores,
        IEnumerable<CuentaVisibleEnRol> direccionCae,
        IReadOnlyDictionary<Guid, string> rolesEjercidosPorDelegados) =>
        administradores.Concat(direccionCae)
            .DistinctBy(u => u.Id)
            .Where(u => !rolesEjercidosPorDelegados.TryGetValue(u.Id, out var rolEjercido)
                        || rolEjercido is Roles.Administrador or Roles.DireccionCae)
            .ToList();

    /// <returns>Cuántas gestiones urgentes había, avisadas o no.</returns>
    private async Task<int> AvisarTenantAsync(IServiceScope ambito, CancellationToken stoppingToken)
    {
        var gestionesUrgentes = await ContarGestionesUrgentesAsync(ambito, stoppingToken);
        if (gestionesUrgentes == 0) return 0;

        var directorio = ambito.ServiceProvider.GetRequiredService<DirectorioUsuariosTenant>();
        var administradores = await directorio.ObtenerVisiblesEnRolAsync(Roles.Administrador, stoppingToken);
        var direccionCae = await directorio.ObtenerVisiblesEnRolAsync(Roles.DireccionCae, stoppingToken);

        var rolesEjercidosPorDelegados = await directorio.ObtenerRolesDeOperadoresDelegadosAsync(stoppingToken);

        var destinatarios = DestinatariosDelAviso(administradores, direccionCae, rolesEjercidosPorDelegados);
        if (destinatarios.Count == 0) return gestionesUrgentes;

        var repositorio = ambito.ServiceProvider.GetRequiredService<INotificacionUsuarioRepository>();
        var mensaje = gestionesUrgentes == 1
            ? "Hay 1 gestión urgente de visita pendiente (ventana mínima de validación)."
            : $"Hay {gestionesUrgentes} gestiones urgentes de visita pendientes (ventana mínima de validación).";

        foreach (var destinatario in destinatarios)
            repositorio.Agregar(new NotificacionUsuario(destinatario.Id, "Gestiones urgentes de visita", mensaje, "/bandeja", "Ver Mi trabajo"));

        await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);

        return gestionesUrgentes;
    }
}
