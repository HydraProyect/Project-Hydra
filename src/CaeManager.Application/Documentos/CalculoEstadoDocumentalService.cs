using CaeManager.Domain.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Peor estado de vigencia de los Documentos de un propietario (Trabajador,
/// Empresa o Vehículo), calculado en bloque para una página entera de una
/// tabla.
///
/// Existe porque esas tres entidades <b>no tienen estado propio</b> en el
/// modelo — a diferencia de Centro, que sí tiene
/// <see cref="Domain.Centros.EstadoCentro"/>. La pregunta que el gestor hace
/// de verdad ("enséñame los trabajadores con algo vencido") se responde con
/// el mismo semáforo que rige en el resto del sistema, no con un concepto
/// nuevo: el estado se deriva con <see cref="CalculadoraEstadoDocumento"/>, la
/// única fuente de verdad de vigencias (ver <c>Project-Hydra-Negocio/tecnico/DATABASE.md</c>), y se queda
/// con el peor de los documentos de cada propietario.
///
/// Un propietario sin ningún Documento <b>no aparece</b> en el diccionario:
/// "sin documentos" no es un estado de vigencia, y quien llama decide cómo
/// mostrarlo. Tampoco produce <see cref="EstadoDocumento.Faltante"/>, que
/// exige saber qué <i>debería</i> existir — eso vive en
/// <c>ObtenerAlertasQuery</c> y se incorporará aquí cuando ese cálculo se
/// extraiga a su propio servicio.
///
/// Subcontrata queda fuera a propósito: no es un
/// <see cref="AmbitoAplicacion"/>, no tiene Documentos propios.
/// </summary>
public interface ICalculoEstadoDocumentalService
{
    Task<IReadOnlyDictionary<Guid, EstadoDocumento>> CalcularPeorEstadoAsync(
        AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken);

    /// <summary>
    /// El estado de cada documento operativo de un Vehículo, uno por documento y sin orden. Es lo que
    /// necesita la ficha 360 del Vehículo para decir cuántos de sus documentos registrados están al día
    /// (<see cref="CumplimientoDocumental.Evaluar(IEnumerable{EstadoDocumento})"/>): el peor estado de
    /// <see cref="CalcularPeorEstadoAsync"/> no alcanza para una fracción. Sin documentos, lista vacía.
    /// </summary>
    Task<IReadOnlyList<EstadoDocumento>> CalcularEstadosDeDocumentosDeVehiculoAsync(
        Guid vehiculoId, CancellationToken cancellationToken);

    /// <summary>
    /// El desglose documental de cada propietario de una página de listado: sus incidencias (documentos
    /// Vencidos, Urgentes, Próximos o Sin confirmar) y cuántos de sus documentos registrados están al día
    /// (<see cref="DesgloseDocumentalDto"/>). Una sola consulta de documentos para todos los propietarios pedidos.
    ///
    /// <para>
    /// Mira los mismos documentos que <see cref="CalcularPeorEstadoAsync"/> (los operativos del propietario) y
    /// clasifica cada uno con la misma calculadora, así que el peor estado de las incidencias de un propietario
    /// ES su peor estado, y no hay incidencias cuando ese estado es Vigente o Sin caducidad. Un propietario sin
    /// ningún documento <b>no aparece</b> en el diccionario, como allí.
    /// </para>
    ///
    /// <para>
    /// Trae una fila por documento: es para los propietarios de UNA página, no para todos los visibles (para
    /// filtrar u ordenar por estado está el agregado de <see cref="CalcularPeorEstadoAsync"/>). Ámbitos
    /// admitidos: Trabajador, Empresa y Vehículo.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DesgloseDocumentalDto>> CalcularDesgloseAsync(
        AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken);
}

public class CalculoEstadoDocumentalService(
    IDocumentosQueryContext documentosContext, IConfiguracionQueryContext configuracionContext,
    ITiposDocumentoQueryContext tiposDocumentoContext)
    : ICalculoEstadoDocumentalService
{
    public async Task<IReadOnlyDictionary<Guid, EstadoDocumento>> CalcularPeorEstadoAsync(
        AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken)
    {
        if (propietarioIds.Count == 0)
            return new Dictionary<Guid, EstadoDocumento>();

        var ids = propietarioIds.Distinct().ToList();

        var consulta = ambito switch
        {
            AmbitoAplicacion.Trabajador => documentosContext.Documentos.Operativos()
                .Where(d => d.TrabajadorId != null && ids.Contains(d.TrabajadorId!.Value))
                .Select(d => new { PropietarioId = d.TrabajadorId!.Value, d.EstadoVigencia, d.FechaVencimiento }),
            AmbitoAplicacion.Empresa => documentosContext.Documentos.Operativos()
                .Where(d => d.EmpresaId != null && ids.Contains(d.EmpresaId!.Value))
                .Select(d => new { PropietarioId = d.EmpresaId!.Value, d.EstadoVigencia, d.FechaVencimiento }),
            AmbitoAplicacion.Vehiculo => documentosContext.Documentos.Operativos()
                .Where(d => d.VehiculoId != null && ids.Contains(d.VehiculoId!.Value))
                .Select(d => new { PropietarioId = d.VehiculoId!.Value, d.EstadoVigencia, d.FechaVencimiento }),
            AmbitoAplicacion.Cliente => documentosContext.Documentos.Operativos()
                .Where(d => d.ClienteId != null && ids.Contains(d.ClienteId!.Value))
                .Select(d => new { PropietarioId = d.ClienteId!.Value, d.EstadoVigencia, d.FechaVencimiento }),
            _ => throw new ArgumentOutOfRangeException(
                nameof(ambito), ambito, "Este ámbito no tiene estado documental derivado.")
        };

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        // Agregado en SQL (MIN por propietario), no traer una fila por
        // Documento para agrupar en memoria (hallazgo crítico, auditoría
        // Módulo 8: esto se llamaba con todos los propietarios visibles de la
        // página cuando se ordena/filtra por estado, así que antes eran
        // potencialmente miles de filas de Documento por una sola pantalla).
        // MIN(FechaVencimiento) da el peor de los documentos con fecha: el
        // estado es una función monótona de la fecha —cuanto antes vence, más
        // urgente—. MIN ignora los NULL, que son los «no caduca» y los «sin
        // confirmar»; estos segundos se cuentan aparte, porque no pueden
        // perderse en el MIN como si no caducaran.
        var agregadoPorPropietario = await consulta
            .GroupBy(f => f.PropietarioId)
            .Select(g => new
            {
                PropietarioId = g.Key,
                PeorFecha = g.Min(f => f.FechaVencimiento),
                SinConfirmar = g.Count(f => f.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar)
            })
            .ToListAsync(cancellationToken);

        return agregadoPorPropietario.ToDictionary(
            a => a.PropietarioId,
            a => PeorEstado(a.PeorFecha, a.SinConfirmar > 0, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias));
    }

    public async Task<IReadOnlyList<EstadoDocumento>> CalcularEstadosDeDocumentosDeVehiculoAsync(
        Guid vehiculoId, CancellationToken cancellationToken)
    {
        var vigencias = await documentosContext.Documentos.Operativos()
            .Where(d => d.VehiculoId == vehiculoId)
            .Select(d => new { d.EstadoVigencia, d.FechaVencimiento })
            .ToListAsync(cancellationToken);

        if (vigencias.Count == 0)
            return [];

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        return vigencias
            .Select(v => CalculadoraEstadoDocumento.Calcular(
                v.EstadoVigencia, v.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, DesgloseDocumentalDto>> CalcularDesgloseAsync(
        AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken)
    {
        if (propietarioIds.Count == 0)
            return new Dictionary<Guid, DesgloseDocumentalDto>();

        var ids = propietarioIds.Distinct().ToList();

        // Mismo universo que CalcularPeorEstadoAsync: los documentos operativos del propietario, acotados por su
        // Id (más el filtro global de Tenant y RLS, que son de la consulta de Documentos).
        var documentos = documentosContext.Documentos.Operativos();
        var delAmbito = ambito switch
        {
            AmbitoAplicacion.Trabajador => documentos
                .Where(d => d.TrabajadorId != null && ids.Contains(d.TrabajadorId!.Value))
                .Select(d => new { PropietarioId = d.TrabajadorId!.Value, d.Id, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento }),
            AmbitoAplicacion.Empresa => documentos
                .Where(d => d.EmpresaId != null && ids.Contains(d.EmpresaId!.Value))
                .Select(d => new { PropietarioId = d.EmpresaId!.Value, d.Id, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento }),
            AmbitoAplicacion.Vehiculo => documentos
                .Where(d => d.VehiculoId != null && ids.Contains(d.VehiculoId!.Value))
                .Select(d => new { PropietarioId = d.VehiculoId!.Value, d.Id, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento }),
            // Hoy lo piden los listados de Trabajadores y de Vehículos; el ámbito de Empresa está admitido, sin
            // llamador todavía. El de Cliente empresarial no se admite: no se añade aquí otro uso de la columna
            // legacy de Documento que ClienteIdNoSeExtiendeTests congela.
            _ => throw new ArgumentOutOfRangeException(
                nameof(ambito), ambito, "Este ámbito no tiene desglose documental.")
        };

        // LEFT JOIN con el Tipo, no INNER: un documento cuyo Tipo ya no es legible sigue contando en el peor
        // estado del propietario, así que tiene que seguir en su desglose (con el nombre vacío).
        var filas = await (
            from documento in delAmbito
            join tipo in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipo.Id into tipos
            from tipo in tipos.DefaultIfEmpty()
            select new
            {
                documento.PropietarioId,
                documento.Id,
                documento.TipoDocumentoId,
                TipoDocumentoNombre = tipo != null ? tipo.Nombre : null,
                documento.EstadoVigencia,
                documento.FechaVencimiento
            }).ToListAsync(cancellationToken);

        if (filas.Count == 0)
            return new Dictionary<Guid, DesgloseDocumentalDto>();

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        return filas
            .GroupBy(f => f.PropietarioId)
            .ToDictionary(
                grupo => grupo.Key,
                grupo =>
                {
                    var conEstado = grupo
                        .Select(f => new IncidenciaDocumentalDto(
                            f.Id, f.TipoDocumentoId, f.TipoDocumentoNombre ?? string.Empty,
                            CalculadoraEstadoDocumento.Calcular(
                                f.EstadoVigencia, f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                            f.FechaVencimiento))
                        .ToList();
                    var fraccion = CumplimientoDocumental.Evaluar(conEstado.Select(d => d.Estado));

                    return new DesgloseDocumentalDto(
                        conEstado
                            .Where(d => DesgloseDocumentalDto.EsIncidencia(d.Estado))
                            .OrderBy(d => SeveridadEstadoDocumento.Rango(d.Estado))
                            .ThenBy(d => d.TipoDocumentoNombre, StringComparer.CurrentCultureIgnoreCase)
                            // Desempate estable entre dos documentos del mismo Tipo y estado.
                            .ThenBy(d => d.FechaVencimiento)
                            .ThenBy(d => d.DocumentoId)
                            .ToList(),
                        fraccion.Requeridos,
                        fraccion.AlDia);
                });
    }

    /// <summary>
    /// Lo malo conocido (Próximo, Urgente, Vencido) pesa más que lo
    /// desconocido, y lo desconocido más que lo bueno conocido: un propietario
    /// con un documento vigente y otro sin vigencia confirmada está
    /// <see cref="EstadoDocumento.SinConfirmar"/>, no Vigente. Mismo orden que
    /// <see cref="EstadoDocumentalFiltro.ClaveOrden"/>.
    /// </summary>
    internal static EstadoDocumento PeorEstado(
        DateOnly? peorFecha, bool haySinConfirmar, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias)
    {
        var porFecha = peorFecha is { } fecha
            ? CalculadoraEstadoDocumento.Calcular(VigenciaDocumento.VenceEl(fecha), hoy, umbralAmbarDias, umbralRojoDias)
            : EstadoDocumento.SinCaducidad;

        return haySinConfirmar && porFecha is EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad
            ? EstadoDocumento.SinConfirmar
            : porFecha;
    }
}
