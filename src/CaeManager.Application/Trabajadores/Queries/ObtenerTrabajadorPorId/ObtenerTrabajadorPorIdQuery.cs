using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;

public record ObtenerTrabajadorPorIdQuery(Guid Id) : IRequest<TrabajadorDetalleDto?>;

public record TrabajadorDetalleDto(
    Guid Id,
    Guid? EmpresaId,
    Guid? SubcontrataId,
    string EmpleadorNombre,
    string Nombre,
    string Apellidos,
    string? Dni,
    DateOnly? FechaNacimiento,
    string? Email,
    string? Observaciones,
    string? Alias,
    string? Telefono,
    string? Puesto,
    Guid Version);

public class ObtenerTrabajadorPorIdQueryHandler(IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerTrabajadorPorIdQuery, TrabajadorDetalleDto?>
{
    public async Task<TrabajadorDetalleDto?> Handle(ObtenerTrabajadorPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.TrabajadorVisibleAsync(request.Id, cancellationToken)) return null;

        var trabajador = await trabajadoresContext.Trabajadores
            .Where(t => t.Id == request.Id)
            .Select(t => new
            {
                t.Id,
                t.EmpresaId,
                t.SubcontrataId,
                t.Nombre,
                t.Apellidos,
                t.Dni,
                t.FechaNacimiento,
                t.Email,
                t.Observaciones,
                t.Alias,
                t.Telefono,
                t.Puesto,
                t.Version
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (trabajador is null) return null;

        var empleadorNombre = trabajador.EmpresaId is not null
            ? await empresasContext.Empresas.Where(e => e.Id == trabajador.EmpresaId).Select(e => e.RazonSocial).FirstAsync(cancellationToken)
            : await empresasContext.Empresas.Where(e => e.Id == trabajador.SubcontrataId).Select(e => e.RazonSocial).FirstAsync(cancellationToken);

        return new TrabajadorDetalleDto(
            trabajador.Id, trabajador.EmpresaId, trabajador.SubcontrataId, empleadorNombre, trabajador.Nombre, trabajador.Apellidos,
            trabajador.Dni, trabajador.FechaNacimiento, trabajador.Email, trabajador.Observaciones, trabajador.Alias,
            trabajador.Telefono, trabajador.Puesto, trabajador.Version);
    }
}
