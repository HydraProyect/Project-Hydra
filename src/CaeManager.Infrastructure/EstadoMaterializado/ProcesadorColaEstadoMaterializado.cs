using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Consume la cola <c>RecalculosPendientes</c> y rellena los Tenants sin versión, para el Tenant del contexto RLS de la
/// conexión (diseño § 4.5, § 7.3 y § 13.8). Lo llama <see cref="ColaEstadoMaterializadoHostedService"/> con un
/// <c>AmbitoTenantExplicito</c> y un ámbito de servicios por Tenant.
///
/// <para>Cada pasada es una transacción:</para>
/// <list type="number">
/// <item>Reclama hasta <see cref="EntradasPorPasada"/> entradas con <c>FOR UPDATE SKIP LOCKED</c>, primero las de Tenant
/// entero.</item>
/// <item>Si hay una de Tenant entero (o el Tenant no tiene versión): cerrojo exclusivo del Tenant, borra todas las entradas
/// del Tenant que nadie más tiene bloqueadas (se juntan en una sola pasada) y rellena el Tenant entero. Una acción que
/// cambia algo mientras tanto no espera: deja su cambio en la cola para la pasada siguiente.</item>
/// <item>Si no, junta las raíces de todas las entradas reclamadas: cerrojo compartido del Tenant, clausura, cerrojos
/// exclusivos de la clausura (el mismo orden que en línea), clausura otra vez y recálculo. Si la clausura pasa de
/// <see cref="RecalculoEstadoEnLinea.MaximoEntidadesBajoCerrojo"/> entidades o crece bajo cerrojo, no sube el cerrojo del
/// Tenant a exclusivo en la misma transacción (dos colas que lo intentaran se interbloquearían): deshace, encola el Tenant
/// entero y la pasada siguiente lo toma.</item>
/// </list>
/// Si una pasada falla, se deshace y sus entradas reclamadas anotan el intento: esperan 2, 4, 8… minutos (como mucho una
/// hora) antes de volver a reclamarse, y el Tenant no sigue en este ciclo. Un relleno de un Tenant sin versión que falla
/// sin entradas reclamadas deja una de Tenant entero ya intentada, para esperar igual.
/// </summary>
public sealed class ProcesadorColaEstadoMaterializado(
    CaeManagerDbContext dbContext, IRecalculoEstadoMaterializado recalculo, ILogger<ProcesadorColaEstadoMaterializado> logger)
{
    /// <summary>Entradas que reclama una pasada como mucho.</summary>
    public const int EntradasPorPasada = 200;

    /// <summary>Pasadas por Tenant en un ciclo como mucho, para que un Tenant con mucha cola no deje esperando a los demás.</summary>
    public const int MaximoPasadasPorCiclo = 10;

    /// <summary>
    /// Espera máxima por el cerrojo del Tenant en la cola: una acción en línea lo tiene compartido como mucho lo que dura su
    /// transacción. Si vence, la pasada falla y se reintenta con espera.
    /// </summary>
    public const string TopeDeEsperaDeCerrojo = EscritorEstadoMaterializado.TopeDeEsperaDelCerrojoExclusivo;

    private enum Resultado
    {
        SinTrabajo,
        Hecho,
        Fallo,
    }

    /// <summary>Procesa el Tenant de la conexión. Devuelve cuántas pasadas hicieron trabajo.</summary>
    public async Task<int> ProcesarTenantAsync(CancellationToken cancellationToken)
    {
        var tenant = await CerrojosEstadoMaterializado.TenantDeLaConexionAsync(dbContext)
                     ?? throw new InvalidOperationException("La cola del estado materializado necesita el contexto RLS de un Tenant.");

        var hechas = 0;
        for (var pasada = 0; pasada < MaximoPasadasPorCiclo; pasada++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resultado = await PasadaAsync(tenant, cancellationToken);
            if (resultado != Resultado.Hecho)
                break;
            hechas++;
        }

        return hechas;
    }

    private async Task<Resultado> PasadaAsync(Guid tenant, CancellationToken cancellationToken)
    {
        List<EntradaColaEstado> reclamadas = [];
        string? escalar = null;
        var rellenando = false;
        var resultado = Resultado.SinTrabajo;

        var fallo = await EnTransaccionAsync(async () =>
        {
            reclamadas = [];
            escalar = null;
            rellenando = false;
            resultado = Resultado.SinTrabajo;

            var tieneVersion = await CerrojosEstadoMaterializado.TieneVersionAsync(dbContext, tenant);
            reclamadas = await ColaEstadoMaterializado.ReclamarAsync(dbContext, tenant, EntradasPorPasada);
            if (tieneVersion && reclamadas.Count == 0)
                return true;

            // Sin versión y sin nada que reclamar, pero con una entrada de Tenant entero en espera tras un fallo (o que otra
            // cola tiene reclamada): el relleno espera a esa entrada en vez de repetirse en cada sondeo.
            if (!tieneVersion && reclamadas.Count == 0
                && (await ColaEstadoMaterializado.HayEntradasAsync(dbContext, tenant)).TenantEntero)
                return true;

            await CerrojosEstadoMaterializado.FijarTopeDeEsperaAsync(dbContext, TopeDeEsperaDeCerrojo);
            if (!tieneVersion || reclamadas.Any(e => e.EntidadId is null))
            {
                rellenando = true;
                await RellenarAsync(tenant, cancellationToken);
                resultado = Resultado.Hecho;
                return true;
            }

            escalar = await RecalcularRaicesAsync(tenant, reclamadas, cancellationToken);
            if (escalar is not null)
                return false;

            await ColaEstadoMaterializado.BorrarAsync(dbContext, tenant, reclamadas.Select(e => e.Id).ToList());
            resultado = Resultado.Hecho;
            return true;
        });

        // El servicio se detiene (relevo azul/verde): la pasada se deshizo y no cuenta como intento fallido; sus entradas
        // siguen en la cola sin espera para la ranura que tome el relevo.
        if (fallo is not null && cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("La cola del estado materializado se detiene.", fallo, cancellationToken);

        if (fallo is not null)
        {
            logger.LogError(fallo, "Estado materializado: falló una pasada de la cola del Tenant {Tenant}; sus {Entradas} entradas esperan al reintento.",
                tenant, reclamadas.Count);
            var ids = reclamadas.Select(e => e.Id).ToList();
            // Un relleno sin entrada de Tenant entero reclamada (el Tenant no tenía versión) deja una ya intentada: sin ella,
            // el sondeo siguiente lo repetiría sin espera.
            var anotarRelleno = rellenando && !reclamadas.Any(e => e.EntidadId is null);
            if (ids.Count > 0 || anotarRelleno)
            {
                var falloAnotar = await EnTransaccionAsync(async () =>
                {
                    if (ids.Count > 0)
                        await ColaEstadoMaterializado.AnotarFalloAsync(dbContext, tenant, ids);
                    if (anotarRelleno)
                        await ColaEstadoMaterializado.AnotarFalloDeRellenoSinEntradasAsync(dbContext, tenant, "Relleno fallido del Tenant sin versión");
                    return true;
                });
                if (falloAnotar is not null)
                    logger.LogError(falloAnotar, "Estado materializado: no se pudo anotar el intento fallido en la cola del Tenant {Tenant}.", tenant);
            }

            return Resultado.Fallo;
        }

        if (escalar is not null)
        {
            logger.LogInformation("Estado materializado: la cola del Tenant {Tenant} pasa a Tenant entero: {Motivo}.", tenant, escalar);
            var falloEscalar = await EnTransaccionAsync(async () =>
            {
                await ColaEstadoMaterializado.EncolarTenantEnteroAsync(dbContext, tenant, escalar);
                return true;
            });
            if (falloEscalar is not null)
            {
                logger.LogError(falloEscalar, "Estado materializado: no se pudo encolar el Tenant entero {Tenant}.", tenant);
                return Resultado.Fallo;
            }

            return Resultado.Hecho;
        }

        return resultado;
    }

    /// <summary>
    /// Tenant entero: el cerrojo exclusivo antes de borrar las entradas (así ninguna acción en línea está a medio recalcular
    /// sobre lo que se borra) y el borrado antes de leer (lo que se encole después se queda para la pasada siguiente). El
    /// relleno vuelve a pedir el mismo cerrojo, que en la misma transacción ya es suyo.
    /// </summary>
    private async Task RellenarAsync(Guid tenant, CancellationToken cancellationToken)
    {
        await CerrojosEstadoMaterializado.BloquearAsync(dbContext, [CerrojosEstadoMaterializado.ClaveDeTenant(tenant)], compartido: false);
        var borradas = await ColaEstadoMaterializado.BorrarTodasAsync(dbContext, tenant);
        var resultado = await recalculo.RellenarTenantAsync(cancellationToken);
        logger.LogInformation(
            "Estado materializado: Tenant {Tenant} recalculado entero ({Entradas} entradas de la cola, {Centros} Centros, {Filas} filas cambiadas).",
            tenant, borradas, resultado.Centros, resultado.FilasCambiadas);
    }

    /// <summary>Las raíces juntas de las entradas reclamadas. Devuelve el motivo para pasar a Tenant entero, o null si recalculó.</summary>
    private async Task<string?> RecalcularRaicesAsync(Guid tenant, List<EntradaColaEstado> reclamadas, CancellationToken cancellationToken)
    {
        List<Guid> De(AmbitoEstadoEntidad ambito) =>
            reclamadas.Where(e => e.Ambito == (short)ambito && e.EntidadId is not null).Select(e => e.EntidadId!.Value).Distinct().ToList();

        var raices = new RaicesDeRecalculo(De(AmbitoEstadoEntidad.Centro), De(AmbitoEstadoEntidad.Trabajador), De(AmbitoEstadoEntidad.Empresa));
        if (raices.EstaVacia)
            return null;

        await CerrojosEstadoMaterializado.BloquearAsync(dbContext, [CerrojosEstadoMaterializado.ClaveDeTenant(tenant)], compartido: true);
        var clausura = await recalculo.CalcularClausuraAsync(raices, cancellationToken);
        if (clausura.Entidades > RecalculoEstadoEnLinea.MaximoEntidadesBajoCerrojo)
            return $"La clausura de la cola pasa de {RecalculoEstadoEnLinea.MaximoEntidadesBajoCerrojo} entidades";

        await CerrojosEstadoMaterializado.BloquearAsync(dbContext, ClavesDe(tenant, clausura), compartido: false);
        var bajoCerrojo = await recalculo.CalcularClausuraAsync(raices, cancellationToken);
        if (!clausura.Contiene(bajoCerrojo))
            return "La clausura de la cola creció bajo cerrojo";

        await recalculo.RecalcularClausuraAsync(bajoCerrojo, cancellationToken);
        return null;
    }

    private static List<string> ClavesDe(Guid tenantId, ClausuraDeRecalculo clausura) =>
        clausura.Centros.Select(id => CerrojosEstadoMaterializado.ClaveDeEntidad(tenantId, 'C', id))
            .Concat(clausura.Trabajadores.Select(id => CerrojosEstadoMaterializado.ClaveDeEntidad(tenantId, 'T', id)))
            .Concat(clausura.Empresas.Select(id => CerrojosEstadoMaterializado.ClaveDeEntidad(tenantId, 'E', id)))
            .ToList();

    /// <summary>
    /// Una transacción por la estrategia de ejecución; el trabajo devuelve si se confirma (<c>false</c> deshace). El fallo
    /// del trabajo se devuelve capturado: la estrategia solo reintenta abrir la transacción, y la cola reintenta la pasada
    /// con su propia espera.
    /// </summary>
    private async Task<Exception?> EnTransaccionAsync(Func<Task<bool>> trabajo)
    {
        Exception? fallo = null;
        try
        {
            var estrategia = dbContext.Database.CreateExecutionStrategy();
            await estrategia.ExecuteAsync(async () =>
            {
                fallo = null;
                await using var transaccion = await dbContext.Database.BeginTransactionAsync(CancellationToken.None);
                try
                {
                    if (await trabajo())
                        await transaccion.CommitAsync(CancellationToken.None);
                    else
                        await transaccion.RollbackAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    fallo = ex;
                }
            });
        }
        catch (Exception ex)
        {
            fallo = ex;
        }

        return fallo;
    }
}
