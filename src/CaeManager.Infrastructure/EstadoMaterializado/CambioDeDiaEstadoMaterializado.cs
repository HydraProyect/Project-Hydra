using System.Data;
using CaeManager.Application.Common;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// El cambio de día del estado materializado de un Tenant (diseño § 2.2, § 7.2 y § 13.12): la pasada de medianoche, el
/// cotejo nocturno y la marca <c>UltimoDiaProcesado</c>, para el Tenant del contexto RLS de la conexión. Lo llama
/// <see cref="CambioDeDiaEstadoMaterializadoHostedService"/> con un <c>AmbitoTenantExplicito</c> y un ámbito de servicios
/// por Tenant, el mismo contrato de identidad que la cola (<c>cae_app_runtime</c> bajo la RLS forzada).
///
/// <para>
/// El estado por vigencia cambia una sola vez al día, a las 00:00 de Madrid (decisión de producto del 2026-10-24): un
/// documento nunca vence a mitad del día. Por eso «hoy» es el día de negocio (<see cref="DiaDeNegocio"/>), el mismo que usa
/// el recálculo, y la pasada solo toca las filas cuyo <c>ProximoCambioEl</c> es hoy o anterior.
/// </para>
///
/// <list type="number">
/// <item><b>Pasada.</b> Encola las raíces de las filas con <c>ProximoCambioEl</c> igual o anterior a hoy (el Tenant entero si
/// pasan de <see cref="RecalculoEstadoEnLinea.MaximoRaicesEnCola"/>) y drena la cola del Tenant con
/// <see cref="ProcesadorColaEstadoMaterializado"/>, con sus cerrojos y su espera tras un fallo.</item>
/// <item><b>Cotejo.</b> Si la cola quedó vacía, en una transacción de solo lectura <c>REPEATABLE READ</c> (una sola foto
/// para lo calculado y lo guardado) recalcula el Tenant entero por lotes, sin escribir, y lo compara con lo guardado
/// (<see cref="CotejoEstadoMaterializado"/>). Repone lo que la cola no recoge: los cambios guardados con el contexto de
/// otro Tenant o con un contexto creado a mano, un guardado síncrono que no llegó a recalcular y una caída entre el
/// guardado y el recálculo. Si encuentra diferencias, deja un aviso con los recuentos (sin datos personales), encola las
/// raíces que las corrigen y vuelve a drenar la cola.</item>
/// <item><b>Marca.</b> <c>UltimoDiaProcesado</c> = hoy, solo hacia delante. Significa «el cambio de día de hoy está hecho»:
/// pasada, cola drenada y cotejo hecho (con sus correcciones ya encoladas o hechas). Con la marca al día, repetir no hace
/// nada: el cambio de día es idempotente por día.</item>
/// </list>
/// Si la cola no queda vacía (una pasada falló y espera su reintento) el cambio de día se aplaza sin marca, y el siguiente
/// ciclo lo retoma: así un servidor parado o una pasada fallida se reponen en cuanto se pueda (diseño § 13.12).
/// </summary>
public sealed class CambioDeDiaEstadoMaterializado(
    CaeManagerDbContext dbContext,
    IRecalculoEstadoMaterializado recalculo,
    ProcesadorColaEstadoMaterializado procesadorCola,
    IAlertaOperativa alerta,
    ILogger<CambioDeDiaEstadoMaterializado> logger)
{
    /// <summary>Veces que se drena la cola del Tenant en un cambio de día como mucho, antes de aplazarlo.</summary>
    public const int MaximoDrenados = 3;

    /// <summary>Días de retraso de la marca a partir de los que un aplazamiento deja un aviso.</summary>
    public const int DiasDeRetrasoConAviso = 2;

    /// <summary>
    /// El cambio de día de <paramref name="hoy"/> en el Tenant de la conexión. Si el día de negocio deja de ser
    /// <paramref name="hoy"/> mientras dura, no marca: el ciclo siguiente lo hace con el día nuevo.
    /// </summary>
    public async Task<ResultadoCambioDeDia> ProcesarTenantAsync(DateOnly hoy, CancellationToken cancellationToken)
    {
        var tenant = await CerrojosEstadoMaterializado.TenantDeLaConexionAsync(dbContext)
                     ?? throw new InvalidOperationException("El cambio de día del estado materializado necesita el contexto RLS de un Tenant.");

        if (DiaDeNegocio.Hoy() != hoy)
            return ResultadoCambioDeDia.De(EstadoCambioDeDia.OtroDia);

        var version = await dbContext.Set<VersionEstadoTenant>().AsNoTracking()
            .Where(v => v.TenantId == tenant)
            .Select(v => new { v.UltimoDiaProcesado })
            .SingleOrDefaultAsync(cancellationToken);

        // Un Tenant sin versión no tiene estado guardado: sus pantallas calculan en vivo y la cola lo rellena (si la opción
        // está encendida). Nada que pasar ni cotejar.
        if (version is null)
            return ResultadoCambioDeDia.De(EstadoCambioDeDia.SinVersion);
        if (version.UltimoDiaProcesado >= hoy)
            return ResultadoCambioDeDia.De(EstadoCambioDeDia.YaProcesado);

        // ---- Pasada de medianoche ----
        var (raices, tenantEntero) = await RaicesQueCambianAsync(hoy, cancellationToken);
        if (tenantEntero)
            await ColaEstadoMaterializado.EncolarTenantEnteroAsync(dbContext, tenant, "Cambio de día: muchas filas cambian hoy");
        else
            await ColaEstadoMaterializado.EncolarRaicesAsync(dbContext, tenant, raices, "Cambio de día");
        var raicesDeLaPasada = tenantEntero ? -1 : raices.Cuantas;

        if (!await DrenarColaAsync(tenant, cancellationToken))
            return Aplazado(tenant, hoy, version.UltimoDiaProcesado, raicesDeLaPasada);

        // ---- Cotejo nocturno ----
        var diferencias = await CotejarAsync(tenant, hoy, cancellationToken);
        if (diferencias is null)
            return Aplazado(tenant, hoy, version.UltimoDiaProcesado, raicesDeLaPasada);

        if (diferencias.Total > 0)
        {
            var raicesCotejo = diferencias.Raices;
            var corregirTenantEntero = diferencias.Huerfanas > 0 || raicesCotejo.Cuantas > RecalculoEstadoEnLinea.MaximoRaicesEnCola;
            var mensaje =
                $"Cotejo nocturno del estado materializado: el Tenant propietario {tenant} tenía {diferencias.Total} filas distintas " +
                $"del recálculo completo del día {hoy:yyyy-MM-dd} ({diferencias}). Se corrigen encolando " +
                (corregirTenantEntero ? "el Tenant entero." : $"{raicesCotejo.Cuantas} raíces.");
            logger.LogWarning(
                "Cotejo nocturno del estado materializado: el Tenant propietario {TenantId} tenía {Diferencias} filas distintas del recálculo completo del día {Dia} ({Detalle}); se corrigen encolando {Correccion}.",
                tenant, diferencias.Total, hoy, diferencias.ToString(), corregirTenantEntero ? "el Tenant entero" : $"{raicesCotejo.Cuantas} raíces");
            // Un LogWarning no llega a Sentry (su nivel mínimo de evento es Error): el aviso va por el canal de alertas.
            alerta.Emitir(mensaje, NivelAlertaOperativa.Aviso);

            if (corregirTenantEntero)
                await ColaEstadoMaterializado.EncolarTenantEnteroAsync(dbContext, tenant, "Cotejo nocturno: diferencias");
            else
                await ColaEstadoMaterializado.EncolarRaicesAsync(dbContext, tenant, raicesCotejo, "Cotejo nocturno: diferencias");

            // La corrección ya está encolada: aunque la cola no se drene ahora, la recoge su propio ciclo. El cotejo de hoy
            // está hecho.
            await DrenarColaAsync(tenant, cancellationToken);
        }

        // ---- Marca ----
        if (DiaDeNegocio.Hoy() != hoy)
            return new ResultadoCambioDeDia(EstadoCambioDeDia.OtroDia, raicesDeLaPasada, diferencias);

        await MarcarDiaProcesadoAsync(tenant, hoy);
        return new ResultadoCambioDeDia(EstadoCambioDeDia.Hecho, raicesDeLaPasada, diferencias);
    }

    /// <summary>
    /// Las raíces de las filas cuyo <c>ProximoCambioEl</c> es hoy o anterior: la entidad de una fila de entidad, el titular
    /// de un par de Trabajador o de Empresa (sus pares en todos sus Centros) y el Centro de cualquier otro par y de las
    /// acreditaciones en la Plataforma CAE. Cada consulta trae como mucho una raíz más del máximo: si alguna llega, el Tenant
    /// entero.
    /// </summary>
    private async Task<(RaicesDeRecalculo Raices, bool TenantEntero)> RaicesQueCambianAsync(DateOnly hoy, CancellationToken ct)
    {
        const int tope = RecalculoEstadoEnLinea.MaximoRaicesEnCola + 1;

        var entidades = await dbContext.Set<EstadoEntidad>().AsNoTracking()
            .Where(e => e.ProximoCambioEl <= hoy)
            .Select(e => new { e.Ambito, e.EntidadId })
            .Take(tope)
            .ToListAsync(ct);
        var trabajadoresDePares = await dbContext.Set<EstadoRequisito>().AsNoTracking()
            .Where(r => r.ProximoCambioEl <= hoy && r.AmbitoTitular == AmbitoAplicacion.Trabajador)
            .Select(r => r.TitularId).Distinct().Take(tope)
            .ToListAsync(ct);
        var empresasDePares = await dbContext.Set<EstadoRequisito>().AsNoTracking()
            .Where(r => r.ProximoCambioEl <= hoy && r.AmbitoTitular == AmbitoAplicacion.Empresa)
            .Select(r => r.TitularId).Distinct().Take(tope)
            .ToListAsync(ct);
        var centrosDePares = await dbContext.Set<EstadoRequisito>().AsNoTracking()
            .Where(r => r.ProximoCambioEl <= hoy && r.AmbitoTitular != AmbitoAplicacion.Trabajador && r.AmbitoTitular != AmbitoAplicacion.Empresa)
            .Select(r => r.CentroId).Distinct().Take(tope)
            .ToListAsync(ct);
        var centrosDePlataforma = await dbContext.Set<EstadoEnPlataforma>().AsNoTracking()
            .Where(p => p.ProximoCambioEl <= hoy)
            .Select(p => p.CentroId).Distinct().Take(tope)
            .ToListAsync(ct);

        var anotadas = new DiferenciasCotejo();
        foreach (var e in entidades)
            anotadas.AnotarRaiz(e.Ambito, e.EntidadId);
        foreach (var id in trabajadoresDePares)
            anotadas.AnotarRaiz(AmbitoEstadoEntidad.Trabajador, id);
        foreach (var id in empresasDePares)
            anotadas.AnotarRaiz(AmbitoEstadoEntidad.Empresa, id);
        foreach (var id in centrosDePares.Concat(centrosDePlataforma))
            anotadas.AnotarRaiz(AmbitoEstadoEntidad.Centro, id);

        var raices = anotadas.Raices;
        var algunaLlena = new[] { entidades.Count, trabajadoresDePares.Count, empresasDePares.Count, centrosDePares.Count, centrosDePlataforma.Count }
            .Any(n => n >= tope);
        return (raices, algunaLlena || raices.Cuantas > RecalculoEstadoEnLinea.MaximoRaicesEnCola);
    }

    /// <summary>Drena la cola del Tenant. Devuelve si quedó vacía.</summary>
    private async Task<bool> DrenarColaAsync(Guid tenant, CancellationToken ct)
    {
        for (var vuelta = 0; vuelta < MaximoDrenados; vuelta++)
        {
            ct.ThrowIfCancellationRequested();
            var hechas = await procesadorCola.ProcesarTenantAsync(ct);
            if (!(await ColaEstadoMaterializado.HayEntradasAsync(dbContext, tenant)).Alguna)
                return true;
            // Sin trabajo hecho y con entradas: esperan su reintento tras un fallo, o las tiene otra pasada.
            if (hechas == 0)
                return false;
        }

        return false;
    }

    /// <summary>
    /// Recalcula el Tenant entero por lotes, sin escribir, y lo compara con lo guardado, todo en una foto. Devuelve null si
    /// la foto tiene cola (lo guardado aún no está al día y compararlo daría diferencias falsas) o si algún lote se calculó
    /// con otro día.
    /// </summary>
    private Task<DiferenciasCotejo?> CotejarAsync(Guid tenant, DateOnly hoy, CancellationToken ct) =>
        dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // Solo lectura: se deshace siempre al soltarla.
            await using var transaccion = await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

            // La primera lectura fija la foto: lo que la cola tuviera pendiente en ella no está aún en lo guardado.
            if ((await ColaEstadoMaterializado.HayEntradasAsync(dbContext, tenant)).Alguna)
                return null;

            var diferencias = new DiferenciasCotejo();
            var otroDia = false;
            var recorridas = await recalculo.CalcularTenantPorLotesAsync(async calculo =>
            {
                if (calculo.Dia != hoy)
                {
                    otroDia = true;
                    return;
                }

                CotejoEstadoMaterializado.CompararLote(
                    calculo,
                    await ParesGuardadosAsync(calculo, ct),
                    await PlataformaGuardadaAsync(calculo, ct),
                    await EntidadesGuardadasAsync(calculo, ct),
                    diferencias);
            }, ct);
            if (otroDia)
                return null;

            diferencias.Huerfanas = await HuerfanasAsync(recorridas, ct);
            return diferencias;
        });

    /// <summary>Los pares guardados en el alcance del lote: el mismo que sustituye el escritor.</summary>
    private async Task<List<CotejoEstadoMaterializado.Guardada<FilaEstadoRequisito>>> ParesGuardadosAsync(
        CalculoEstadoMaterializado calculo, CancellationToken ct)
    {
        var completos = calculo.CentrosConTodosLosPares.ToList();
        var centros = calculo.CentroIds.ToList();
        var titulares = calculo.TrabajadorIds.Concat(calculo.EmpresaIds).Distinct().ToList();
        if (completos.Count == 0 && (centros.Count == 0 || titulares.Count == 0))
            return [];

        return await dbContext.Set<EstadoRequisito>().AsNoTracking()
            .Where(r => completos.Contains(r.CentroId) || (centros.Contains(r.CentroId) && titulares.Contains(r.TitularId)))
            .Select(r => new CotejoEstadoMaterializado.Guardada<FilaEstadoRequisito>(
                new FilaEstadoRequisito(r.CentroId, r.AmbitoTitular, r.TitularId, r.TipoDocumentoId, r.DocumentoEfectivoId,
                    r.EstadoTalveg, r.BloqueaAcceso, r.CuentaParaCumplimiento, r.EstadoPlataforma, r.VencimientoEfectivo, r.ProximoCambioEl),
                r.VersionCalculo))
            .ToListAsync(ct);
    }

    /// <summary>Las acreditaciones en la Plataforma CAE guardadas de los Centros del lote.</summary>
    private async Task<List<CotejoEstadoMaterializado.Guardada<FilaEstadoEnPlataforma>>> PlataformaGuardadaAsync(
        CalculoEstadoMaterializado calculo, CancellationToken ct)
    {
        var centros = calculo.CentroIds.ToList();
        if (centros.Count == 0)
            return [];

        return await dbContext.Set<EstadoEnPlataforma>().AsNoTracking()
            .Where(p => centros.Contains(p.CentroId))
            .Select(p => new CotejoEstadoMaterializado.Guardada<FilaEstadoEnPlataforma>(
                new FilaEstadoEnPlataforma(p.CentroId, p.CanalGestionDocumentalId, p.AmbitoTitular, p.TitularId, p.TipoDocumentoId,
                    p.Estado, p.EstadoVigencia, p.FechaVencimientoEnPlataforma, p.ProximoCambioEl),
                p.VersionCalculo))
            .ToListAsync(ct);
    }

    /// <summary>Las filas guardadas de los Centros, Trabajadores y Empresas del lote.</summary>
    private async Task<List<CotejoEstadoMaterializado.Guardada<FilaEstadoEntidad>>> EntidadesGuardadasAsync(
        CalculoEstadoMaterializado calculo, CancellationToken ct)
    {
        var centros = calculo.CentroIds.ToList();
        var trabajadores = calculo.TrabajadorIds.ToList();
        var empresas = calculo.EmpresaIds.ToList();
        if (centros.Count == 0 && trabajadores.Count == 0 && empresas.Count == 0)
            return [];

        return await dbContext.Set<EstadoEntidad>().AsNoTracking()
            .Where(e => (e.Ambito == AmbitoEstadoEntidad.Centro && centros.Contains(e.EntidadId))
                        || (e.Ambito == AmbitoEstadoEntidad.Trabajador && trabajadores.Contains(e.EntidadId))
                        || (e.Ambito == AmbitoEstadoEntidad.Empresa && empresas.Contains(e.EntidadId)))
            .Select(e => new CotejoEstadoMaterializado.Guardada<FilaEstadoEntidad>(
                new FilaEstadoEntidad(e.Ambito, e.EntidadId, e.Estado, e.EstadoPlataforma, e.Vencidos, e.VencidosSinBloqueo,
                    e.Urgentes, e.Proximos, e.Faltan, e.SinConfirmar, e.PendientesEnPlataforma, e.Requeridos, e.AlDia, e.ProximoCambioEl),
                e.VersionCalculo))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Filas de entidades que el recorrido no encontró (borradas, o de otro ámbito): las que purgaría el relleno
    /// (<c>EscritorEstadoMaterializado.PurgarHuerfanosAsync</c>).
    /// </summary>
    private async Task<int> HuerfanasAsync(EntidadesDelTenant recorridas, CancellationToken ct)
    {
        var centros = recorridas.Centros.ToList();
        var trabajadores = recorridas.Trabajadores.ToList();
        var empresas = recorridas.Empresas.ToList();

        var pares = await dbContext.Set<EstadoRequisito>().AsNoTracking().CountAsync(r => !centros.Contains(r.CentroId), ct);
        var plataforma = await dbContext.Set<EstadoEnPlataforma>().AsNoTracking().CountAsync(p => !centros.Contains(p.CentroId), ct);
        var entidades = await dbContext.Set<EstadoEntidad>().AsNoTracking().CountAsync(e =>
            !((e.Ambito == AmbitoEstadoEntidad.Centro && centros.Contains(e.EntidadId))
              || (e.Ambito == AmbitoEstadoEntidad.Trabajador && trabajadores.Contains(e.EntidadId))
              || (e.Ambito == AmbitoEstadoEntidad.Empresa && empresas.Contains(e.EntidadId))), ct);
        return pares + plataforma + entidades;
    }

    /// <summary>
    /// <c>UltimoDiaProcesado</c> = hoy, solo hacia delante (dos ranuras que marcaran a la vez no la echan atrás). No sube la
    /// versión: marcar el día no cambia ninguna fila del estado.
    /// </summary>
    private Task<int> MarcarDiaProcesadoAsync(Guid tenant, DateOnly hoy) =>
        dbContext.Database.ExecuteSqlAsync($"""
            UPDATE "VersionesEstadoTenant" SET "UltimoDiaProcesado" = {hoy}
            WHERE "TenantId" = {tenant} AND ("UltimoDiaProcesado" IS NULL OR "UltimoDiaProcesado" < {hoy})
            """, CancellationToken.None);

    private ResultadoCambioDeDia Aplazado(Guid tenant, DateOnly hoy, DateOnly? ultimoDiaProcesado, int raicesDeLaPasada)
    {
        if (ultimoDiaProcesado is { } ultimo && hoy.DayNumber - ultimo.DayNumber >= DiasDeRetrasoConAviso)
        {
            logger.LogWarning(
                "El cambio de día del estado materializado del Tenant propietario {TenantId} lleva {Dias} días sin completarse (último día procesado: {Ultimo}): la cola del Tenant no se vacía.",
                tenant, hoy.DayNumber - ultimo.DayNumber, ultimo);
        }

        return new ResultadoCambioDeDia(EstadoCambioDeDia.Aplazado, raicesDeLaPasada, null);
    }
}

/// <summary>Cómo terminó el cambio de día de un Tenant.</summary>
public enum EstadoCambioDeDia
{
    /// <summary>El Tenant no tiene versión: no tiene estado guardado.</summary>
    SinVersion,

    /// <summary>La marca ya estaba en hoy: no se hizo nada.</summary>
    YaProcesado,

    /// <summary>Pasada, cola drenada, cotejo y marca.</summary>
    Hecho,

    /// <summary>La cola no quedó vacía: sin cotejo ni marca, el ciclo siguiente lo retoma.</summary>
    Aplazado,

    /// <summary>El día de negocio cambió mientras duraba: sin marca, el ciclo siguiente lo hace con el día nuevo.</summary>
    OtroDia,
}

/// <summary>
/// El resultado del cambio de día de un Tenant: su estado, las raíces que encoló la pasada (-1 si encoló el Tenant entero) y
/// lo que encontró el cotejo (null si no llegó a cotejar).
/// </summary>
public sealed record ResultadoCambioDeDia(EstadoCambioDeDia Estado, int RaicesDeLaPasada, DiferenciasCotejo? Cotejo)
{
    public static ResultadoCambioDeDia De(EstadoCambioDeDia estado) => new(estado, 0, null);
}
