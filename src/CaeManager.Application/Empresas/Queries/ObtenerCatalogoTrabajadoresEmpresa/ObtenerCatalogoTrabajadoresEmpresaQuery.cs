using CaeManager.Application.Common;
using CaeManager.Domain.Empresas;
using MediatR;

namespace CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa;

public record DelegacionEmpresaDto(
    Guid Id, string Nombre, string? ContactoReclamacionNombre, string? ContactoReclamacionCorreo,
    string? ContactoReclamacionTelefono, Guid Version);

public record OficioEmpresaDto(Guid Id, string Nombre, Guid Version);

public record CatalogoTrabajadoresEmpresaDto(IReadOnlyList<DelegacionEmpresaDto> DelegacionesEmpresa, IReadOnlyList<OficioEmpresaDto> Oficios)
{
    public static readonly CatalogoTrabajadoresEmpresaDto Vacio = new([], []);
}

/// <summary>
/// Delegaciones y Oficios de una Empresa, para la sección de Empresa 360 y para los desplegables del Trabajador. Lo ve
/// quien ve la Empresa (<see cref="AlcanceDatosServiceExtensions.EmpresaVisibleAsync"/>); fuera de alcance, catálogo vacío.
/// </summary>
public record ObtenerCatalogoTrabajadoresEmpresaQuery(Guid EmpresaId) : IRequest<CatalogoTrabajadoresEmpresaDto>;

public class ObtenerCatalogoTrabajadoresEmpresaQueryHandler(
    ICatalogoTrabajadoresEmpresaRepository catalogo,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCatalogoTrabajadoresEmpresaQuery, CatalogoTrabajadoresEmpresaDto>
{
    public async Task<CatalogoTrabajadoresEmpresaDto> Handle(ObtenerCatalogoTrabajadoresEmpresaQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaVisibleAsync(request.EmpresaId, cancellationToken))
            return CatalogoTrabajadoresEmpresaDto.Vacio;

        var delegaciones = await catalogo.ListarDelegacionesEmpresaAsync(request.EmpresaId, cancellationToken);
        var oficios = await catalogo.ListarOficiosAsync(request.EmpresaId, cancellationToken);

        return new(
            [.. delegaciones.Select(d => new DelegacionEmpresaDto(d.Id, d.Nombre, d.ContactoReclamacionNombre,
                d.ContactoReclamacionCorreo, d.ContactoReclamacionTelefono, d.Version))],
            [.. oficios.Select(o => new OficioEmpresaDto(o.Id, o.Nombre, o.Version))]);
    }
}
