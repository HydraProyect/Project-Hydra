using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Telemetria;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Visitas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

public record ObtenerKpisBpoQuery(PeriodoKpi Periodo) : IRequest<KpisBpoDto>;

public record OcupacionGestorDto(Guid UsuarioId, string Nombre, int SegundosActivos, int? PorcentajeOcupacion);

public record HorasClienteDto(Guid ClienteId, string ClienteNombre, int SegundosActivos);

public record TramoAntelacionConteoDto(TramoAntelacion Tramo, int Cantidad);

public record AtribucionUrgenciaConteoDto(AtribucionUrgencia Atribucion, int Cantidad);

/// <summary>
/// Bloque Operativa + Fricción del catálogo de KPIs, para el tenant activo y el
/// <see cref="PeriodoKpi"/> pedido (el mismo que reciben los KPIs de IA y facturación).
///
/// Los denominadores viajan explícitos (<c>Total*</c>) por el mismo motivo que en
/// <see cref="CatalogoKpisValoresDto"/>: el fan-out multi-tenant pondera por volumen
/// al fusionar, y un promedio simple entre tenants mentiría.
/// </summary>
public record KpisBpoDto(
    int SugerenciasConfirmadasSinEdicion,
    int TotalSugerenciasResueltas,
    IReadOnlyList<OcupacionGestorDto> OcupacionPorGestor,
    IReadOnlyList<HorasClienteDto> HorasPorCliente,
    IReadOnlyList<TramoAntelacionConteoDto> DistribucionAntelacion,
    int VisitasConFalsoAviso,
    int TotalVisitasConAntelacionMedida,
    decimal HorasBloqueadasPorClienteTotal)
{
    public static readonly KpisBpoDto Vacio = new(0, 0, [], [], [], 0, 0, 0m);

    public IReadOnlyList<AtribucionUrgenciaConteoDto> AtribucionUrgencia { get; init; } = [];
}

public class ObtenerKpisBpoQueryHandler(
    ITelemetriaQueryContext telemetriaContext,
    IComunicacionesQueryContext comunicacionesContext,
    IVisitasQueryContext visitasContext,
    IClientesQueryContext clientesContext,
    IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos,
    IDirectorioUsuariosService directorioUsuarios)
    : IRequestHandler<ObtenerKpisBpoQuery, KpisBpoDto>
{
    public const int TopClientesPorHoras = 5;

    public async Task<KpisBpoDto> Handle(ObtenerKpisBpoQuery request, CancellationToken cancellationToken)
    {
        // Acotado por ambos extremos: sin tope superior, cualquier fila con fecha futura
        // (un reloj desajustado, datos de prueba mal generados) se colaría en el período
        // y nadie lo notaría.
        var (inicioMes, finMes) = (request.Periodo.InicioUtc, request.Periodo.FinUtc);
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);

        var (confirmadasSinEdicion, totalResueltas) = await CalcularPalancaIaAsync(inicioMes, finMes, cancellationToken);
        var (ocupacion, horasPorCliente) = await CalcularTiempoAsync(inicioMes, finMes, parametros.HorasJornadaMensualGestor, cancellationToken);
        var friccion = await CalcularFriccionAsync(inicioMes, finMes, cancellationToken);

        return friccion with
        {
            SugerenciasConfirmadasSinEdicion = confirmadasSinEdicion,
            TotalSugerenciasResueltas = totalResueltas,
            OcupacionPorGestor = ocupacion,
            HorasPorCliente = horasPorCliente
        };
    }

    /// <summary>
    /// Solo cuenta sugerencias con <c>Resolucion</c> informada: las resueltas antes de
    /// que existiera el dato no se cuentan como confirmadas ni como descartadas, porque
    /// no se sabe qué fueron (ver <see cref="ResolucionSugerencia"/>).
    /// </summary>
    private async Task<(int ConfirmadasSinEdicion, int TotalResueltas)> CalcularPalancaIaAsync(
        DateTime inicioMes, DateTime finMes, CancellationToken cancellationToken)
    {
        var visitas = await comunicacionesContext.SugerenciasVisitaCorreo
            .Where(s => s.Resolucion != null && s.ResueltaEnUtc >= inicioMes && s.ResueltaEnUtc < finMes)
            .GroupBy(s => s.Resolucion)
            .Select(g => new { Resolucion = g.Key, Cantidad = g.Count() })
            .ToListAsync(cancellationToken);

        var gestiones = await comunicacionesContext.DetallesSugerenciaGestionCorreo
            .Where(d => d.Resolucion != null && d.ResueltaEnUtc >= inicioMes && d.ResueltaEnUtc < finMes)
            .GroupBy(d => d.Resolucion)
            .Select(g => new { Resolucion = g.Key, Cantidad = g.Count() })
            .ToListAsync(cancellationToken);

        var todas = visitas.Concat(gestiones).ToList();

        return (
            todas.Where(x => x.Resolucion == ResolucionSugerencia.Confirmada).Sum(x => x.Cantidad),
            todas.Sum(x => x.Cantidad));
    }

    private async Task<(IReadOnlyList<OcupacionGestorDto> Ocupacion, IReadOnlyList<HorasClienteDto> HorasPorCliente)>
        CalcularTiempoAsync(DateTime inicioMes, DateTime finMes, int horasJornadaMensual, CancellationToken cancellationToken)
    {
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);

        var registrosQuery = telemetriaContext.RegistrosTiempoGestion.Where(r => r.FinUtc >= inicioMes && r.FinUtc < finMes);

        // Un Gestor CAE solo debe ver el tiempo de su propia cartera; los tramos de hilos
        // en triage (sin Cliente) quedan fuera de su vista, no en un cajón "otros".
        if (clienteIdsVisibles is not null)
            registrosQuery = registrosQuery.Where(r => r.ClienteId != null && clienteIdsVisibles.Contains(r.ClienteId.Value));

        var porUsuario = await registrosQuery
            .GroupBy(r => r.UsuarioId)
            .Select(g => new { UsuarioId = g.Key, Segundos = g.Sum(r => r.SegundosActivos) })
            .ToListAsync(cancellationToken);

        var porCliente = await registrosQuery
            .Where(r => r.ClienteId != null)
            .GroupBy(r => r.ClienteId!.Value)
            .Select(g => new { ClienteId = g.Key, Segundos = g.Sum(r => r.SegundosActivos) })
            .OrderByDescending(x => x.Segundos)
            .Take(TopClientesPorHoras)
            .ToListAsync(cancellationToken);

        var nombresUsuario = await directorioUsuarios.ObtenerNombresVisiblesAsync(
            porUsuario.Select(u => u.UsuarioId).ToList(), cancellationToken);

        var clienteIds = porCliente.Select(c => c.ClienteId).ToList();
        var nombresCliente = await clientesContext.Clientes
            .Where(c => clienteIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.RazonSocial, cancellationToken);

        var segundosJornada = horasJornadaMensual * 3600;

        var ocupacion = porUsuario
            .Where(u => nombresUsuario.ContainsKey(u.UsuarioId))
            .Select(u => new OcupacionGestorDto(
                u.UsuarioId,
                nombresUsuario[u.UsuarioId],
                u.Segundos,
                segundosJornada == 0 ? null : (int)Math.Round(u.Segundos * 100.0 / segundosJornada)))
            .OrderByDescending(u => u.SegundosActivos)
            .ToList();

        var horas = porCliente
            .Select(c => new HorasClienteDto(c.ClienteId, nombresCliente.GetValueOrDefault(c.ClienteId, "—"), c.Segundos))
            .ToList();

        return (ocupacion, horas);
    }

    private async Task<KpisBpoDto> CalcularFriccionAsync(DateTime inicioMes, DateTime finMes, CancellationToken cancellationToken)
    {
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var visitasQuery = visitasContext.Visitas.Where(v => v.Tramo != null && v.FechaHoraExpedienteCompletoUtc >= inicioMes && v.FechaHoraExpedienteCompletoUtc < finMes);
        if (centroIdsVisibles is not null)
            visitasQuery = visitasQuery.Where(v => centroIdsVisibles.Contains(v.CentroId));

        var medidas = await visitasQuery
            .Select(v => new { v.Tramo, v.Atribucion, v.AntelacionNominalHoras, v.AntelacionEfectivaHoras })
            .ToListAsync(cancellationToken);

        if (medidas.Count == 0) return KpisBpoDto.Vacio;

        var distribucion = medidas
            .GroupBy(v => v.Tramo!.Value)
            .Select(g => new TramoAntelacionConteoDto(g.Key, g.Count()))
            .OrderBy(t => t.Tramo)
            .ToList();

        var atribucion = medidas
            .GroupBy(v => v.Atribucion)
            .Select(g => new AtribucionUrgenciaConteoDto(g.Key, g.Count()))
            .OrderBy(a => a.Atribucion)
            .ToList();

        var falsosAvisos = medidas.Count(v => v.Atribucion == Domain.Visitas.AtribucionUrgencia.DocumentacionTardiaCliente);

        // Bloqueado = lo que el cliente dijo que daba menos lo que el gestor tuvo.
        // Se deriva en vez de persistirse: es exactamente la resta de dos columnas
        // que sí están selladas, y una tercera columna podría desincronizarse.
        var bloqueadas = medidas.Sum(v => (v.AntelacionNominalHoras ?? 0m) - (v.AntelacionEfectivaHoras ?? 0m));

        return KpisBpoDto.Vacio with
        {
            DistribucionAntelacion = distribucion,
            VisitasConFalsoAviso = falsosAvisos,
            TotalVisitasConAntelacionMedida = medidas.Count,
            HorasBloqueadasPorClienteTotal = bloqueadas,
            AtribucionUrgencia = atribucion
        };
    }
}
