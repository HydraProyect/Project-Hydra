using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// El catálogo de <see cref="CatalogoDivisionPdf"/> de una Empresa gestionada: solo una Empresa propia del Tenant que el
/// usuario pueda gestionar, las Subcontratas con una Relación Empresarial vigente con ella y los Trabajadores y Vehículos
/// de ambas que su alcance deja ver. El filtro de Tenant y RLS siguen actuando por debajo.
/// </summary>
public record ObtenerCatalogoDivisionPdfQuery(Guid EmpresaGestionadaId) : IRequest<Result<CatalogoDivisionPdf>>;

public class ObtenerCatalogoDivisionPdfQueryHandler(
    IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IVehiculosQueryContext vehiculosContext,
    ITiposDocumentoQueryContext tiposContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCatalogoDivisionPdfQuery, Result<CatalogoDivisionPdf>>
{
    public const string ErrorEmpresaNoGestionable = "DivisionPdf.EmpresaNoGestionable";

    public async Task<Result<CatalogoDivisionPdf>> Handle(ObtenerCatalogoDivisionPdfQuery request, CancellationToken cancellationToken)
    {
        var empresaId = request.EmpresaGestionadaId;

        var gestionables = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
        var gestionada = gestionables is null || gestionables.Contains(empresaId)
            ? await empresasContext.Empresas
                .Where(e => e.Id == empresaId && e.EsPropia)
                .Select(e => new EmpresaDivisionPdf(e.Id, e.RazonSocial, e.Cif))
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        if (gestionada is null)
            return Result.Fallo<CatalogoDivisionPdf>(Error.Crear(
                ErrorEmpresaNoGestionable, "La Empresa gestionada no existe o queda fuera de tu cartera."));

        var subcontratasVisibles = await alcanceDatos.ObtenerSubcontrataIdsParaGestionAsync(cancellationToken);
        var subcontrataIds = empresasContext.RelacionesEmpresariales
            .Where(r => r.ClienteId == empresaId && r.VigenciaHasta == null)
            .Select(r => r.ProveedoraId);

        var subcontratas = await empresasContext.Empresas
            .Where(e => e.NivelServicio != null && subcontrataIds.Contains(e.Id))
            .Where(e => subcontratasVisibles == null || subcontratasVisibles.Contains(e.Id))
            .OrderBy(e => e.RazonSocial)
            .Select(e => new EmpresaDivisionPdf(e.Id, e.RazonSocial, e.Cif))
            .ToListAsync(cancellationToken);

        var duenas = subcontratas.Select(s => s.Id).Append(empresaId).ToList();

        return Result.Exito(new CatalogoDivisionPdf(
            gestionada, subcontratas,
            await LeerTrabajadoresAsync(duenas, cancellationToken),
            await LeerVehiculosAsync(duenas, cancellationToken),
            await LeerTiposAsync(cancellationToken)));
    }

    private async Task<List<TrabajadorDivisionPdf>> LeerTrabajadoresAsync(List<Guid> duenas, CancellationToken cancellationToken)
    {
        var visibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        var filas = await trabajadoresContext.Trabajadores
            .Where(t => (t.SubcontrataId != null && duenas.Contains(t.SubcontrataId.Value))
                || (t.EmpresaId != null && duenas.Contains(t.EmpresaId.Value)))
            .Where(t => visibles == null || visibles.Contains(t.Id))
            .OrderBy(t => t.Apellidos).ThenBy(t => t.Nombre)
            .Select(t => new { t.Id, t.Nombre, t.Apellidos, t.Dni, t.SubcontrataId, t.EmpresaId })
            .ToListAsync(cancellationToken);

        return filas
            .Select(t => new TrabajadorDivisionPdf(t.Id, t.Nombre, t.Apellidos, t.Dni, DuenaDe(t.SubcontrataId, t.EmpresaId, duenas)))
            .ToList();
    }

    private async Task<List<VehiculoDivisionPdf>> LeerVehiculosAsync(List<Guid> duenas, CancellationToken cancellationToken)
    {
        var visibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);

        var filas = await vehiculosContext.Vehiculos
            .Where(v => (v.SubcontrataId != null && duenas.Contains(v.SubcontrataId.Value))
                || (v.EmpresaId != null && duenas.Contains(v.EmpresaId.Value)))
            .Where(v => visibles == null || visibles.Contains(v.Id))
            .OrderBy(v => v.NumeroPlaca)
            .Select(v => new { v.Id, v.Nombre, v.NumeroPlaca, v.SubcontrataId, v.EmpresaId })
            .ToListAsync(cancellationToken);

        return filas
            .Select(v => new VehiculoDivisionPdf(v.Id, v.Nombre, v.NumeroPlaca, DuenaDe(v.SubcontrataId, v.EmpresaId, duenas)))
            .ToList();
    }

    /// <summary>La Subcontrata manda: un Trabajador o Vehículo que la lleva pertenece a ella, no a la Empresa contratante.</summary>
    private static Guid DuenaDe(Guid? subcontrataId, Guid? empresaId, List<Guid> duenas) =>
        subcontrataId is { } subcontrata && duenas.Contains(subcontrata) ? subcontrata : empresaId!.Value;

    private async Task<List<TipoDocumentoDivisionPdf>> LeerTiposAsync(CancellationToken cancellationToken)
    {
        var tipos = await tiposContext.TiposDocumento
            .Where(t => !t.Archivado)
            .OrderBy(t => t.Nombre)
            .Select(t => new { t.Id, t.Nombre, t.AmbitoAplicacion, t.AplicaVencimientoAutomatico, t.VigenciaMeses })
            .ToListAsync(cancellationToken);

        var ids = tipos.Select(t => t.Id).ToList();
        var aliases = (await tiposContext.TiposDocumentoAlias
                .Where(a => ids.Contains(a.TipoDocumentoId))
                .Select(a => new { a.TipoDocumentoId, a.Texto })
                .ToListAsync(cancellationToken))
            .ToLookup(a => a.TipoDocumentoId, a => a.Texto);

        return tipos
            .Select(t => new TipoDocumentoDivisionPdf(
                t.Id, t.Nombre, t.AmbitoAplicacion, t.AplicaVencimientoAutomatico, t.VigenciaMeses, aliases[t.Id].ToList()))
            .ToList();
    }
}
