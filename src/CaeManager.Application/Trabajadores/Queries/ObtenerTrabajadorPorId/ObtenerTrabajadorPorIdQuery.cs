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
    TipoContratacion TipoContratacion = TipoContratacion.CuentaAjena,
    string? Notas = null,
    bool NotaInternaVisible = false,
    string? ContactoEmergenciaNombre = null,
    string? ContactoEmergenciaTelefono = null,
    bool ContactoEmergenciaVisible = false);

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
///
/// <para>
/// <b>Nota interna.</b> <c>Notas</c> (la «Nota interna» de Trabajador 360) sigue la misma política y el mismo criterio
/// que la de Empresa 360: solo viaja a quien la lee según <see cref="IPoliticaNotaInterna"/>; los demás reciben
/// <c>null</c> y <c>NotaInternaVisible</c> en <c>false</c>. La API pública v1 no la publica (<c>TrabajadorApiDetalleDto</c>).
/// </para>
///
/// <para>
/// <b>Contacto de emergencia (decisión de Chris, 2026-10-18).</b> <c>ContactoEmergenciaNombre</c> y
/// <c>ContactoEmergenciaTelefono</c> siguen la misma política: solo viajan a quien lee la nota interna; el Usuario de
/// Cliente recibe <c>null</c> y <c>ContactoEmergenciaVisible</c> en <c>false</c>. Tampoco los publica la API v1 ni
/// salen en listados o exportaciones. Todo rol con escritura los lee, así que el editor de la ficha nunca reenvía como
/// vacío un contacto que no pudo ver.
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
                t.TipoContratacion,
                t.Notas,
                t.ContactoEmergenciaNombre,
                t.ContactoEmergenciaTelefono
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (trabajador is null) return null;

        var empleadorNombre = trabajador.EmpresaId is not null
            ? await empresasContext.Empresas.Where(e => e.Id == trabajador.EmpresaId).Select(e => e.RazonSocial).FirstAsync(cancellationToken)
            : await empresasContext.Empresas.Where(e => e.Id == trabajador.SubcontrataId).Select(e => e.RazonSocial).FirstAsync(cancellationToken);

        // Ver el comentario de la clase: sin la política, ni los tres campos del permiso, ni la nota interna, ni el
        // contacto de emergencia salen del servidor.
        var conPermisoTrabajo = await politicaNotaInterna.PuedeLeerAsync(cancellationToken);
        var conNotaInterna = conPermisoTrabajo;
        var conContactoEmergencia = conPermisoTrabajo;

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
            trabajador.TipoContratacion,
            conNotaInterna ? trabajador.Notas : null,
            conNotaInterna,
            conContactoEmergencia ? trabajador.ContactoEmergenciaNombre : null,
            conContactoEmergencia ? trabajador.ContactoEmergenciaTelefono : null,
            conContactoEmergencia);
    }
}
