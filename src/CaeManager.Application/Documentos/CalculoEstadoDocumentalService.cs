using CaeManager.Application.Configuracion;
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
/// única fuente de verdad de vigencias (ver <c>DATABASE.md</c>), y se queda
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
}

public class CalculoEstadoDocumentalService(
    IDocumentosQueryContext documentosContext, IConfiguracionQueryContext configuracionContext)
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
            AmbitoAplicacion.Trabajador => documentosContext.Documentos
                .Where(d => d.TrabajadorId != null && ids.Contains(d.TrabajadorId!.Value))
                .Select(d => new { PropietarioId = d.TrabajadorId!.Value, d.FechaVencimiento }),
            AmbitoAplicacion.Empresa => documentosContext.Documentos
                .Where(d => d.EmpresaId != null && ids.Contains(d.EmpresaId!.Value))
                .Select(d => new { PropietarioId = d.EmpresaId!.Value, d.FechaVencimiento }),
            AmbitoAplicacion.Vehiculo => documentosContext.Documentos
                .Where(d => d.VehiculoId != null && ids.Contains(d.VehiculoId!.Value))
                .Select(d => new { PropietarioId = d.VehiculoId!.Value, d.FechaVencimiento }),
            AmbitoAplicacion.Cliente => documentosContext.Documentos
                .Where(d => d.ClienteId != null && ids.Contains(d.ClienteId!.Value))
                .Select(d => new { PropietarioId = d.ClienteId!.Value, d.FechaVencimiento }),
            _ => throw new ArgumentOutOfRangeException(
                nameof(ambito), ambito, "Este ámbito no tiene estado documental derivado.")
        };

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var filas = await consulta.ToListAsync(cancellationToken);

        // El peor estado es el mayor del enum: el orden de EstadoDocumento va
        // de menos a más urgente (NoAplica … Vencido), así que Max es
        // literalmente "lo que más urge de este propietario".
        return filas
            .GroupBy(f => f.PropietarioId)
            .ToDictionary(
                grupo => grupo.Key,
                grupo => grupo.Max(f => CalculadoraEstadoDocumento.Calcular(
                    f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)));
    }
}
