using CaeManager.Domain.Trabajadores;
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
    Guid Version,
    string? NumeroAfiliacionSeguridadSocial = null,
    string? Nacionalidad = null,
    string? TipoVinculo = null,
    DateOnly? FechaAltaEmpresa = null,
    DateOnly? FechaBajaEmpresa = null,
    string? FiguraEnRnt = null,
    string? TipoPermisoTrabajo = null,
    DateOnly? CaducidadPermisoTrabajo = null,
    string? MotivoPermisoTrabajo = null,
    bool PermisoTrabajoVisible = false,
    TipoContratacion TipoContratacion = TipoContratacion.CuentaAjena);

/// <summary>
/// Detalle de Trabajador 360.
///
/// <para>
/// <b>Permiso de trabajo (decisión de Chris, 2026-10-15).</b> El tipo, la caducidad y el motivo del permiso de trabajo
/// solo viajan a quien lee la «Nota interna» de las fichas 360 (<see cref="IPoliticaNotaInterna"/>): quien tiene en el
/// Tenant un rol del equipo de gestión (<see cref="PoliticaNotaInterna.RolesQueVenLaNotaInterna"/>; el equipo del Tenant
/// propietario y el Gestor CAE que lo opera por Asignación de Operación o encargo) y la Sesión Privilegiada con acceso
/// total. El Usuario de Cliente que ve al Trabajador por
/// una Asignación recibe la ficha sin esos tres campos (<c>null</c>) y con <c>PermisoTrabajoVisible</c> en
/// <c>false</c>; la afiliación y el vínculo con la empresa sí le llegan. La regla vive aquí, en Application, y no solo
/// en la pantalla: el DTO es lo que sale del servidor. El <c>false</c> por defecto de <c>PermisoTrabajoVisible</c>
/// hace que quien construya el DTO sin pasar por esta regla oculte, no enseñe.
/// </para>
/// </summary>
public class ObtenerTrabajadorPorIdQueryHandler(IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext,
    IAlcanceDatosService alcanceDatos, IPoliticaNotaInterna politicaNotaInterna)
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
                t.Version,
                t.NumeroAfiliacionSeguridadSocial,
                t.Nacionalidad,
                t.TipoVinculo,
                t.FechaAltaEmpresa,
                t.FechaBajaEmpresa,
                t.FiguraEnRnt,
                t.TipoPermisoTrabajo,
                t.CaducidadPermisoTrabajo,
                t.MotivoPermisoTrabajo,
                t.TipoContratacion
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (trabajador is null) return null;

        var empleadorNombre = trabajador.EmpresaId is not null
            ? await empresasContext.Empresas.Where(e => e.Id == trabajador.EmpresaId).Select(e => e.RazonSocial).FirstAsync(cancellationToken)
            : await empresasContext.Empresas.Where(e => e.Id == trabajador.SubcontrataId).Select(e => e.RazonSocial).FirstAsync(cancellationToken);

        // Ver el comentario de la clase: sin la política, los tres campos del permiso no salen del servidor.
        var conPermisoTrabajo = await politicaNotaInterna.PuedeLeerAsync(cancellationToken);

        return new TrabajadorDetalleDto(
            trabajador.Id, trabajador.EmpresaId, trabajador.SubcontrataId, empleadorNombre, trabajador.Nombre, trabajador.Apellidos,
            trabajador.Dni, trabajador.FechaNacimiento, trabajador.Email, trabajador.Observaciones, trabajador.Alias,
            trabajador.Telefono, trabajador.Puesto, trabajador.Version,
            trabajador.NumeroAfiliacionSeguridadSocial, trabajador.Nacionalidad, trabajador.TipoVinculo,
            trabajador.FechaAltaEmpresa, trabajador.FechaBajaEmpresa, trabajador.FiguraEnRnt,
            conPermisoTrabajo ? trabajador.TipoPermisoTrabajo : null,
            conPermisoTrabajo ? trabajador.CaducidadPermisoTrabajo : null,
            conPermisoTrabajo ? trabajador.MotivoPermisoTrabajo : null,
            conPermisoTrabajo,
            trabajador.TipoContratacion);
    }
}
