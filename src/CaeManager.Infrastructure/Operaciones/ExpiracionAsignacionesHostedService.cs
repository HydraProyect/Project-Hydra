using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Operaciones;

/// <summary>
/// Cierra las asignaciones operativas cuya vigencia ya pasó, y activa las
/// programadas cuya vigencia ya empezó.
///
/// <b>No es una comodidad, es un requisito del esquema.</b> Los índices únicos
/// parciales que garantizan "un solo responsable vigente" filtran por
/// <c>Estado = 'Vigente'</c>, no por fechas: una asignación vigente con
/// <c>VigenciaHasta</c> ya pasada seguiría ocupando su hueco e impediría dar de
/// alta a su sustituta con un 23505. El mecanismo anterior no tenía este
/// problema porque evaluaba la caducidad en cada consulta en vez de guardarla
/// en una columna de estado; al pasar a estado explícito, alguien tiene que
/// moverlo.
///
/// Envuelto en elección de líder, igual que el resto de jobs: dos réplicas
/// cerrando la misma fila a la vez es justo lo que el token de concurrencia
/// convertiría en excepción.
///
/// Sin ámbito de tenant: estas dos tablas son catálogo global y cruzan tenants
/// por naturaleza.
/// </summary>
public class ExpiracionAsignacionesHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<ExpiracionAsignacionesHostedService> logger)
    : BackgroundService
{
    /// <summary>
    /// Una hora. La vigencia de una asignación se fija en días, así que no hace
    /// falta más resolución; y menos convertiría en ruido un trabajo que casi
    /// siempre no encuentra nada.
    /// </summary>
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        await EjecutarCicloAsync(stoppingToken);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await EjecutarCicloAsync(stoppingToken);
    }

    private async Task EjecutarCicloAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync(
                "expiracion-asignaciones-operativas", ProcesarAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de expiración de asignaciones operativas.");
        }
    }

    private async Task ProcesarAsync(CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        var dbContext = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        await ProcesarParaPruebasAsync(dbContext, logger, stoppingToken);
    }

    /// <summary>
    /// Un ciclo completo sobre un contexto dado. Existe para que los tests
    /// puedan ejercitar la lógica —en particular la revalidación al activar una
    /// programada, que es donde vive el riesgo— sin levantar el servicio de
    /// fondo ni depender de la elección de líder.
    /// </summary>
    public static async Task ProcesarParaPruebasAsync(
        CaeManagerDbContext dbContext, ILogger logger, CancellationToken stoppingToken)
    {
        var ahora = DateTime.UtcNow;

        // Orden deliberado: primero cerrar y guardar, después activar. Los
        // índices únicos parciales no son diferibles, así que activar una
        // programada antes de cerrar a la vigente que sustituye chocaría contra
        // el índice dentro de la misma transacción. Dos guardados ordenados lo
        // evitan sin necesidad de tocar el esquema.
        var cerradas = await CerrarExpiradasAsync(dbContext, ahora, stoppingToken);
        var activadas = await ActivarProgramadasAsync(dbContext, ahora, logger, stoppingToken);

        if (cerradas > 0 || activadas > 0)
            logger.LogInformation(
                "Expiración de asignaciones operativas: {Cerradas} cerradas, {Activadas} activadas.", cerradas, activadas);
    }

    private static async Task<int> CerrarExpiradasAsync(
        CaeManagerDbContext dbContext, DateTime ahora, CancellationToken stoppingToken)
    {
        var carteras = await dbContext.AsignacionesCartera
            .Where(c => c.Estado == EstadoAsignacion.Vigente
                        && c.VigenciaHasta != null && ahora >= c.VigenciaHasta)
            .ToListAsync(stoppingToken);

        var operaciones = await dbContext.AsignacionesOperacion
            .Where(o => o.Estado == EstadoAsignacion.Vigente
                        && o.VigenciaHasta != null && ahora >= o.VigenciaHasta)
            .ToListAsync(stoppingToken);

        // Las carteras de una operación que expira se cierran con ella: una
        // cartera vigente bajo una operación cerrada concedería acceso sin nada
        // que lo ampare.
        var idsOperacionesExpiradas = operaciones.Select(o => o.Id).ToHashSet();
        var carterasHuerfanas = idsOperacionesExpiradas.Count == 0
            ? []
            : await dbContext.AsignacionesCartera
                .Where(c => c.Estado == EstadoAsignacion.Vigente
                            && idsOperacionesExpiradas.Contains(c.AsignacionOperacionId))
                .ToListAsync(stoppingToken);

        foreach (var cartera in carteras.Concat(carterasHuerfanas).DistinctBy(c => c.Id))
            cartera.Cerrar(MotivoCierreAsignacion.Expirada, ahora);

        foreach (var operacion in operaciones)
            operacion.Cerrar(MotivoCierreAsignacion.Expirada, ahora);

        var total = carteras.Count + carterasHuerfanas.Count + operaciones.Count;
        if (total > 0)
            await dbContext.SaveChangesAsync(stoppingToken);

        return total;
    }

    /// <summary>
    /// Activa las programadas cuya vigencia ya empezó, <b>una a una</b>.
    ///
    /// Fila a fila y no en lote porque la activación es donde se re-valida el
    /// solape: el alta solo pudo comprobar contra las vigentes de aquel
    /// momento, y entre medias el reparto ha podido cambiar. Quien choca contra
    /// el índice único parcial se queda como está y se registra; en un único
    /// <c>SaveChanges</c>, un solo choque tumbaría el lote entero y el job
    /// reintentaría cada hora sin avanzar jamás.
    ///
    /// Una cartera no se activa si su operación no está vigente: activarla
    /// concedería acceso sin nada que lo ampare.
    /// </summary>
    private static async Task<int> ActivarProgramadasAsync(
        CaeManagerDbContext dbContext, DateTime ahora, ILogger logger, CancellationToken stoppingToken)
    {
        var activadas = 0;

        var operaciones = await dbContext.AsignacionesOperacion
            .Where(o => o.Estado == EstadoAsignacion.Programada && o.VigenciaDesde <= ahora)
            .ToListAsync(stoppingToken);

        foreach (var operacion in operaciones)
        {
            operacion.Activar();
            if (await GuardarODejarComoEstabaAsync(dbContext, operacion, logger, stoppingToken))
                activadas++;
        }

        var idsOperacionesVigentes = await dbContext.AsignacionesOperacion
            .Where(o => o.Estado == EstadoAsignacion.Vigente)
            .Select(o => o.Id)
            .ToListAsync(stoppingToken);

        var carteras = await dbContext.AsignacionesCartera
            .Where(c => c.Estado == EstadoAsignacion.Programada
                        && c.VigenciaDesde <= ahora
                        && idsOperacionesVigentes.Contains(c.AsignacionOperacionId))
            .ToListAsync(stoppingToken);

        foreach (var cartera in carteras)
        {
            cartera.Activar();
            if (await GuardarODejarComoEstabaAsync(dbContext, cartera, logger, stoppingToken))
                activadas++;
        }

        return activadas;
    }

    /// <summary>
    /// Guarda una activación y, si choca contra el índice único de
    /// responsabilidad (23505 de Postgres), deshace el cambio en memoria y deja
    /// la asignación Programada. Es la traducción del error de base de datos a
    /// una decisión de negocio legible: "esta no puede activarse todavía porque
    /// hay otra respondiendo del mismo ámbito".
    /// </summary>
    private static async Task<bool> GuardarODejarComoEstabaAsync(
        CaeManagerDbContext dbContext, AsignacionResponsabilidad asignacion, ILogger logger,
        CancellationToken stoppingToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(stoppingToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            dbContext.Entry(asignacion).State = EntityState.Detached;

            logger.LogWarning(
                "La asignación {AsignacionId} no puede activarse: ya hay otra vigente sobre el mismo ámbito. " +
                "Queda Programada hasta que se resuelva el solape.", asignacion.Id);

            return false;
        }
    }
}
