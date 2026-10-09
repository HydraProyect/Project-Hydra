using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;

public record ObtenerVehiculoPorIdQuery(Guid Id) : IRequest<VehiculoDetalleDto?>;

/// <summary>
/// <paramref name="DocumentosAlDia"/> cuenta los documentos <b>registrados</b> del Vehículo, no los exigidos:
/// todavía no existe un universo de documentos exigidos a un vehículo, así que «requeridos» es aquí «los que
/// tiene». Qué cuenta como al día lo decide <see cref="CumplimientoDocumental.EsConforme(EstadoDocumento)"/>,
/// igual que en el resto de porcentajes. <paramref name="PeorEstadoDocumental"/> es null sin documentos.
/// </summary>
public record VehiculoDetalleDto(
    Guid Id,
    Guid? EmpresaId,
    Guid? SubcontrataId,
    string EmpleadorNombre,
    string Nombre,
    string Modelo,
    string NumeroPlaca,
    Guid Version,
    FraccionCumplimiento DocumentosAlDia,
    EstadoDocumento? PeorEstadoDocumental);

public class ObtenerVehiculoPorIdQueryHandler(IEmpresasQueryContext empresasContext, IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos, ICalculoEstadoDocumentalService calculoEstadoDocumental)
    : IRequestHandler<ObtenerVehiculoPorIdQuery, VehiculoDetalleDto?>
{
    public async Task<VehiculoDetalleDto?> Handle(ObtenerVehiculoPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.VehiculoVisibleAsync(request.Id, cancellationToken)) return null;

        var vehiculo = await vehiculosContext.Vehiculos
            .Where(v => v.Id == request.Id)
            .Select(v => new { v.Id, v.EmpresaId, v.SubcontrataId, v.Nombre, v.Modelo, v.NumeroPlaca, v.Version })
            .FirstOrDefaultAsync(cancellationToken);

        if (vehiculo is null) return null;

        var empleadorNombre = vehiculo.EmpresaId is not null
            ? await empresasContext.Empresas.Where(e => e.Id == vehiculo.EmpresaId).Select(e => e.RazonSocial).FirstAsync(cancellationToken)
            : await empresasContext.Empresas.Where(e => e.Id == vehiculo.SubcontrataId).Select(e => e.RazonSocial).FirstAsync(cancellationToken);

        var estados = await calculoEstadoDocumental.CalcularEstadosDeDocumentosDeVehiculoAsync(vehiculo.Id, cancellationToken);
        EstadoDocumento? peorEstado = estados.Count == 0 ? null : estados.MinBy(SeveridadEstadoDocumento.Rango);

        return new VehiculoDetalleDto(
            vehiculo.Id, vehiculo.EmpresaId, vehiculo.SubcontrataId, empleadorNombre, vehiculo.Nombre, vehiculo.Modelo,
            vehiculo.NumeroPlaca, vehiculo.Version, CumplimientoDocumental.Evaluar(estados), peorEstado);
    }
}
