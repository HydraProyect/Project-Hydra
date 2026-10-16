using CaeManager.Domain.Centros;
using CaeManager.Domain.Plataforma;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCentroPorId;

public record ObtenerCentroPorIdQuery(Guid Id) : IRequest<CentroDetalleDto?>;

public record CentroDetalleDto(
    Guid Id,
    Guid ClienteId,
    string ClienteRazonSocial,
    Guid EmpresaId,
    string EmpresaRazonSocial,
    string Nombre,
    string? CodigoCentro,
    string? Direccion,
    string? Contacto,
    DateOnly? ContratoVigenteHasta,
    Guid Version,
    ModalidadGestionCae GestionCae = ModalidadGestionCae.ConGestionCae,
    string? HorarioAcceso = null,
    string? ControlAccesosNombre = null,
    string? ControlAccesosTelefono = null,
    string? TecnicoPrevencionNombre = null,
    string? TecnicoPrevencionTelefono = null,
    string? TecnicoPrevencionCorreo = null,
    bool? TrabajosEnAltura = null,
    bool? EspaciosConfinados = null,
    bool? AtmosferasExplosivas = null,
    bool FichaVisible = false);

public class ObtenerCentroPorIdQueryHandler(ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos, IPoliticaNotaInterna politicaNotaInterna)
    : IRequestHandler<ObtenerCentroPorIdQuery, CentroDetalleDto?>
{
    public async Task<CentroDetalleDto?> Handle(ObtenerCentroPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.Id, cancellationToken)) return null;

        // Los contactos personales de la ficha siguen la misma política que Empresa 360.
        // El Drawer lee y escribe con el mismo ámbito de gestión.
        var conFicha = await politicaNotaInterna.PuedeLeerAsync(cancellationToken)
            && await alcanceDatos.CentroParaGestionVisibleAsync(request.Id, cancellationToken);

        return await (
            from centro in centrosContext.Centros
            // Centro.ClienteId repunta contra Empresas desde F3b (CentroConfiguration):
            // "Cliente" es una Empresa contraparte (Empresa.CrearComoCliente).
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            join empresa in empresasContext.Empresas on centro.EmpresaId equals empresa.Id
            where centro.Id == request.Id
            select new CentroDetalleDto(
                centro.Id, centro.ClienteId, cliente.RazonSocial, centro.EmpresaId, empresa.RazonSocial, centro.Nombre,
                centro.CodigoCentro, centro.Direccion, centro.Contacto, centro.ContratoVigenteHasta, centro.Version,
                centro.GestionCae,
                conFicha ? centro.HorarioAcceso : null,
                conFicha ? centro.ControlAccesosNombre : null,
                conFicha ? centro.ControlAccesosTelefono : null,
                conFicha ? centro.TecnicoPrevencionNombre : null,
                conFicha ? centro.TecnicoPrevencionTelefono : null,
                conFicha ? centro.TecnicoPrevencionCorreo : null,
                conFicha ? centro.TrabajosEnAltura : null,
                conFicha ? centro.EspaciosConfinados : null,
                conFicha ? centro.AtmosferasExplosivas : null, conFicha))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
