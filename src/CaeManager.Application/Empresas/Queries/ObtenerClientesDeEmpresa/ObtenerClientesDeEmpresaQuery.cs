using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;

/// <summary>
/// Respalda la pestaña "Clientes" del Context Workspace de Empresa — mismo
/// criterio que <c>ObtenerEmpresasDeClienteQuery</c>, en la dirección
/// contraria.
/// </summary>
public record ObtenerClientesDeEmpresaQuery(Guid EmpresaId) : IRequest<IReadOnlyList<ClienteDeEmpresaDto>>;

public record ClienteDeEmpresaDto(Guid Id, string RazonSocial, string? Cif);

/// <summary>
/// F4.2b: repuntado de <c>EmpresaCliente</c> a <c>RelacionEmpresarial</c>.
/// Aquí el lado fijado es la proveedora y el ambiguo la contraparte:
/// <c>RelacionEmpresarial.ClienteId</c> contiene un Cliente real en las
/// shapes Empresa→Cliente y Subcontrata→Cliente, pero una <b>Empresa
/// propia</b> en la shape Subcontrata→Empresa. Como el alcance de Empresa
/// (lectura o gestión, ver <c>ObtenerEmpresaIdsVisiblesAsync</c>) no comprueba
/// tipo bajo acceso total, sin filtrar por <c>EsCritico != null</c> una
/// Empresa propia podría acabar listada como "Cliente de la Empresa".
/// </summary>
public class ObtenerClientesDeEmpresaQueryHandler(IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerClientesDeEmpresaQuery, IReadOnlyList<ClienteDeEmpresaDto>>
{
    public async Task<IReadOnlyList<ClienteDeEmpresaDto>> Handle(
        ObtenerClientesDeEmpresaQuery request, CancellationToken cancellationToken)
    {
        // Alcance de GESTIÓN, no de lectura (REC-153): esto expone la cartera
        // COMERCIAL de la contratista —qué otros Clientes tiene—, que no es
        // documentación del propio Cliente. Con el alcance de lectura
        // (ObtenerEmpresaIdsVisiblesAsync) como puerta, un usuario de portal
        // (rol Cliente) veía qué otros clientes tiene su contratista solo por
        // tenerla en su propia cartera de lectura.
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return [];

        return await ClientesVigentesDeEmpresas.De(empresasContext, [request.EmpresaId])
            .OrderBy(f => f.RazonSocial)
            .Select(f => new ClienteDeEmpresaDto(f.ClienteEmpresarialId, f.RazonSocial, f.Cif))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Un Cliente empresarial al que una Empresa presta servicio en una Relación Empresarial vigente.</summary>
internal sealed class ClienteVigenteDeEmpresa
{
    public Guid EmpresaId { get; init; }
    public Guid ClienteEmpresarialId { get; init; }
    public string RazonSocial { get; init; } = string.Empty;
    public string? Cif { get; init; }
}

/// <summary>
/// Predicado de «a quién presta servicio esta Empresa»: Relación Empresarial vigente en la que la
/// Empresa es la proveedora y la contraparte es un Cliente empresarial real (<c>EsCritico != null</c>; ver
/// el comentario del handler de arriba). Lo llaman el handler de arriba (la pestaña «Clientes» de Empresa 360) y
/// el filtro y la agrupación «Por Cliente» de /empresas (<c>ObtenerEmpresasQueryHandler</c>). NO aplica alcance:
/// el llamador acota antes con el alcance de GESTIÓN de las Empresas (REC-153). Proyecta a una clase con inicializador, no a un record
/// con constructor, para que EF pueda ordenar después de la proyección.
/// </summary>
internal static class ClientesVigentesDeEmpresas
{
    public static IQueryable<ClienteVigenteDeEmpresa> De(IEmpresasQueryContext empresasContext, IReadOnlyCollection<Guid> empresaIds) =>
        Todas(empresasContext).Where(c => empresaIds.Contains(c.EmpresaId));

    /// <summary>
    /// El mismo predicado sin acotar las Empresas, para quien lo usa como subconsulta correlacionada (el filtro y
    /// la agrupación por Cliente del listado de Empresas). Con más razón que <see cref="De"/>: NO aplica alcance,
    /// y el llamador lo acota antes con el de GESTIÓN de las Empresas y el de lectura de los Clientes.
    /// </summary>
    public static IQueryable<ClienteVigenteDeEmpresa> Todas(IEmpresasQueryContext empresasContext) =>
        from r in empresasContext.RelacionesEmpresariales
        where r.VigenciaHasta == null
        join cliente in empresasContext.Empresas.Where(e => e.EsCritico != null)
            on r.ClienteId equals cliente.Id
        select new ClienteVigenteDeEmpresa
        {
            EmpresaId = r.ProveedoraId,
            ClienteEmpresarialId = cliente.Id,
            RazonSocial = cliente.RazonSocial,
            Cif = cliente.Cif,
        };
}
