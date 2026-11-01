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
/// Fase F: comprueba cada hora y avisa (no por correo en v1 — solo la campana,
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
/// Destinatarios: quienes ejercen Administrador o DireccionCae en el Tenant, porque se vigila el
/// Tenant entero (ver <see cref="DestinatariosDelAviso"/>). Mismos roles (y misma
/// limitación documentada) que el resumen de alertas por correo, que aún mira el rol de
/// origen del Operador Delegado — atribuir
/// una gestión urgente a "el" Gestor CAE responsable exigiría resolver
/// Trabajador/Centro → Empresa/Subcontrata → Cliente, y esa relación puede
/// ser N:M (ver el comentario de EnvioAlertasVencimientoHostedService).
///
/// Un único aviso agregado por Tenant y destinatario, con el número de gestiones urgentes y enlace a
/// Mi trabajo: <see cref="EventoAvisoGestionesUrgentes"/> es fijo, y el índice único de
/// <see cref="NotificacionUsuario"/> sobre Tenant, destinatario y evento impide una segunda fila. Sin
/// leer se actualiza el número; leído y con urgencias, el ciclo siguiente reabre la misma fila; a cero
/// se retira (ver <see cref="SincronizarAvisos"/>).
/// </summary>
public class VigilanciaVisitasUrgentesHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<VigilanciaVisitasUrgentesHostedService> logger)
    : BackgroundService
{
    /// <summary>Identifica el aviso agregado, no un evento concreto: es la tercera coordenada de su índice único.</summary>
    public static readonly Guid EventoAvisoGestionesUrgentes = new("5c1e9f0a-7b3d-4a62-9e84-2d6f1c3ab075");

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
    /// Cuántas gestiones urgentes hay en el Tenant entero. Lee todas las candidatas: las Visitas en
    /// curso lo son, van delante por fecha y no cuentan, así que una sola página podría quedarse sin
    /// ninguna urgente de verdad. Un cero aquí retira el aviso (ver <see cref="SincronizarAvisos"/>), de
    /// ahí que <c>LeerTodasAsync</c> no pueda saltarse una fila aunque otras cambien durante la lectura.
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

        var visitas = await visitasHandler.LeerTodasAsync(
            GestionesUrgentesDeVisita.ConsultaCandidatas(), centroIdsVisibles: null, stoppingToken);

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

    /// <summary>
    /// Deja el aviso de cada destinatario en el número de gestiones: el sin leer se actualiza sin
    /// reabrirlo, el leído se reabre y el que falta se crea. A cero, o para quien ya no es destinatario,
    /// el aviso sin leer se retira de la campana y conserva su fila para reabrirse si vuelve a tocar.
    /// <paramref name="existentes"/> ya viene acotado al Tenant, por eso la clave es solo el destinatario.
    /// </summary>
    /// <returns>Los avisos que no existían, por añadir.</returns>
    internal static IReadOnlyList<NotificacionUsuario> SincronizarAvisos(
        int gestiones,
        IReadOnlyCollection<Guid> destinatarios,
        IReadOnlyCollection<NotificacionUsuario> existentes)
    {
        foreach (var aviso in existentes.Where(n => !n.Leida && (gestiones == 0 || !destinatarios.Contains(n.UsuarioDestinatarioId))))
            aviso.MarcarComoLeida();

        if (gestiones == 0) return [];

        var mensaje = MensajeDelAviso(gestiones);
        var porDestinatario = existentes.ToDictionary(n => n.UsuarioDestinatarioId);
        var nuevas = new List<NotificacionUsuario>();

        foreach (var destinatario in destinatarios)
        {
            if (!porDestinatario.TryGetValue(destinatario, out var existente))
                nuevas.Add(new NotificacionUsuario(
                    destinatario, "Gestiones urgentes de visita", mensaje, "/bandeja", "Ver Mi trabajo", EventoAvisoGestionesUrgentes));
            else if (existente.Leida)
                existente.Reabrir(mensaje);
            else
                existente.ActualizarMensaje(mensaje);
        }

        return nuevas;
    }

    /// <summary>
    /// Escribe también con cero gestiones: ese ciclo es el que retira los avisos que quedaron sin leer, y
    /// por eso los destinatarios solo se resuelven si hay algo que avisar.
    /// </summary>
    protected virtual async Task SincronizarAvisosAsync(IServiceScope ambito, int gestiones, CancellationToken stoppingToken)
    {
        var repositorio = ambito.ServiceProvider.GetRequiredService<INotificacionUsuarioRepository>();
        var existentes = await repositorio.ObtenerPorEventoOrigenAsync(EventoAvisoGestionesUrgentes, stoppingToken);

        IReadOnlyCollection<Guid> destinatarios = [];
        if (gestiones > 0)
        {
            var directorio = ambito.ServiceProvider.GetRequiredService<DirectorioUsuariosTenant>();
            var administradores = await directorio.ObtenerVisiblesEnRolAsync(Roles.Administrador, stoppingToken);
            var direccionCae = await directorio.ObtenerVisiblesEnRolAsync(Roles.DireccionCae, stoppingToken);
            var rolesEjercidosPorDelegados = await directorio.ObtenerRolesDeOperadoresDelegadosAsync(stoppingToken);

            destinatarios = DestinatariosDelAviso(administradores, direccionCae, rolesEjercidosPorDelegados)
                .Select(d => d.Id).ToList();
        }

        foreach (var aviso in SincronizarAvisos(gestiones, destinatarios, existentes))
            repositorio.Agregar(aviso);

        await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);
    }

    /// <returns>Cuántas gestiones urgentes había, avisadas o no.</returns>
    private async Task<int> AvisarTenantAsync(IServiceScope ambito, CancellationToken stoppingToken)
    {
        var gestiones = await ContarGestionesUrgentesAsync(ambito, stoppingToken);
        await SincronizarAvisosAsync(ambito, gestiones, stoppingToken);

        return gestiones;
    }

    private static string MensajeDelAviso(int gestiones) => gestiones == 1
        ? "Hay 1 gestión urgente de visita pendiente (ventana mínima de validación)."
        : $"Hay {gestiones} gestiones urgentes de visita pendientes (ventana mínima de validación).";
}
