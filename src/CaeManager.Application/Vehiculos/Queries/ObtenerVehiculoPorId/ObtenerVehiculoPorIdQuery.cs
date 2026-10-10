using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
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
/// <param name="Notas">
/// La «Nota interna» de la ficha 360 (<c>Vehiculo.Notas</c>). Solo viaja para los roles de
/// <see cref="PoliticaNotaInterna.RolesQueVenLaNotaInterna"/>; para cualquier otro es <c>null</c>.
/// </param>
/// <param name="NotaInternaVisible">
/// Si quien pregunta está entre esos roles y por tanto la ficha debe pintar la tarjeta «Nota interna» (aunque esté
/// vacía). Con <c>false</c> la ficha no la pinta: <c>Notas</c> nulo no distingue «sin nota» de «no te la enseño».
/// </param>
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
    EstadoDocumento? PeorEstadoDocumental,
    string? Notas,
    bool NotaInternaVisible);

/// <summary>
/// <para>
/// <b>Nota interna: el corte es de aquí, no de la página.</b> La página Vehículo 360 solo admite a los roles del
/// equipo, pero esta consulta la envían también el panel del Vehículo y cualquier llamador futuro, y
/// <c>VehiculoVisibleAsync</c> responde por alcance, no por rol. El handler solo entrega la nota a los roles de
/// <see cref="PoliticaNotaInterna.RolesQueVenLaNotaInterna"/> —la misma lista blanca que Subcontrata
/// 360, una sola definición de quién ve una «Nota interna»—; a cualquier otro —Cliente, sin rol, uno futuro— le
/// responde <c>Notas = null</c> y <c>NotaInternaVisible = false</c>.
/// </para>
/// </summary>
public class ObtenerVehiculoPorIdQueryHandler(
    IEmpresasQueryContext empresasContext, IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos,
    ICalculoEstadoDocumentalService calculoEstadoDocumental, IPoliticaNotaInterna politicaNotaInterna)
    : IRequestHandler<ObtenerVehiculoPorIdQuery, VehiculoDetalleDto?>
{
    public async Task<VehiculoDetalleDto?> Handle(ObtenerVehiculoPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.VehiculoVisibleAsync(request.Id, cancellationToken)) return null;

        var vehiculo = await vehiculosContext.Vehiculos
            .Where(v => v.Id == request.Id)
            .Select(v => new { v.Id, v.EmpresaId, v.SubcontrataId, v.Nombre, v.Modelo, v.NumeroPlaca, v.Version, v.Notas })
            .FirstOrDefaultAsync(cancellationToken);

        if (vehiculo is null) return null;

        var empleadorNombre = vehiculo.EmpresaId is not null
            ? await empresasContext.Empresas.Where(e => e.Id == vehiculo.EmpresaId).Select(e => e.RazonSocial).FirstAsync(cancellationToken)
            : await empresasContext.Empresas.Where(e => e.Id == vehiculo.SubcontrataId).Select(e => e.RazonSocial).FirstAsync(cancellationToken);

        var estados = await calculoEstadoDocumental.CalcularEstadosDeDocumentosDeVehiculoAsync(vehiculo.Id, cancellationToken);
        EstadoDocumento? peorEstado = estados.Count == 0 ? null : estados.MinBy(SeveridadEstadoDocumento.Rango);

        var veLaNota = await politicaNotaInterna.PuedeLeerAsync(cancellationToken);

        return new VehiculoDetalleDto(
            vehiculo.Id, vehiculo.EmpresaId, vehiculo.SubcontrataId, empleadorNombre, vehiculo.Nombre, vehiculo.Modelo,
            vehiculo.NumeroPlaca, vehiculo.Version, CumplimientoDocumental.Evaluar(estados), peorEstado,
            Notas: veLaNota ? vehiculo.Notas : null, NotaInternaVisible: veLaNota);
    }
}
