using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;

public record ObtenerSubcontrataPorIdQuery(Guid Id) : IRequest<SubcontrataDetalleDto?>;

/// <param name="Notas">
/// La «Nota interna» de la ficha 360 (<c>Empresa.Notas</c>). Solo viaja para el equipo del Tenant propietario
/// (<see cref="PoliticaNotaInterna.RolesQueVenLaNotaInterna"/>); para cualquier otro rol es <c>null</c>.
/// </param>
/// <param name="NotaInternaVisible">
/// Si quien pregunta es del equipo y por tanto la ficha debe pintar la tarjeta «Nota interna» (aunque esté vacía).
/// Con <c>false</c> la ficha no la pinta: <c>Notas</c> nulo no distingue «sin nota» de «no te la enseño».
/// </param>
public record SubcontrataDetalleDto(
    Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc, IReadOnlyList<Guid> ClienteIds, IReadOnlyList<Guid> EmpresaIds,
    Guid Version, NivelServicioSubcontrata NivelServicio, string? Notas, bool NotaInternaVisible);

/// <summary>
/// F4.2c: <c>ClienteIds</c>/<c>EmpresaIds</c> salen de la arista, con el
/// MISMO criterio de clasificación que usa el diff de escritura de
/// <c>EditarSubcontrataCommand</c> — la condición que faltaba cuando la
/// primera migración de este lector (F4.2b) se revirtió: entonces la lectura
/// filtraba soft delete y el diff de escritura leía el repositorio legacy
/// sin filtrarlo, y una contraparte eliminada se borraba en silencio al
/// guardar. Ahora ambos lados leen la clasificación de
/// <c>ContrapartesVigentes</c>: una contraparte opaca no aparece aquí NI
/// entra en "actuales" del diff, así que su relación sobrevive intacta.
/// Igual que en <c>ObtenerEmpresaPorIdQuery</c>, los Ids NO se acotan por
/// cartera a propósito.
///
/// <para>
/// <b>Nota interna: el corte es de aquí, no de la página.</b> La subcontrata la lee también el rol Cliente (usuario
/// de un Cliente, que la tiene en su cartera de lectura), y la nota es del equipo del Tenant propietario. La decisión
/// es <see cref="IPoliticaNotaInterna"/>, la misma para todas las fichas 360: al equipo y a una Sesión Privilegiada con
/// acceso total al Tenant le llega la nota; a cualquier otro —Cliente, sin rol, uno futuro— le responde
/// <c>Notas = null</c> y <c>NotaInternaVisible = false</c>.
/// </para>
/// </summary>
public class ObtenerSubcontrataPorIdQueryHandler(
    IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos, IPoliticaNotaInterna politicaNotaInterna)
    : IRequestHandler<ObtenerSubcontrataPorIdQuery, SubcontrataDetalleDto?>
{
    public async Task<SubcontrataDetalleDto?> Handle(ObtenerSubcontrataPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.SubcontrataVisibleAsync(request.Id, cancellationToken)) return null;

        var subcontrata = await empresasContext.Empresas
            .Where(e => e.Id == request.Id)
            .Select(e => new { e.Id, e.RazonSocial, e.Cif, e.CreadoEnUtc, e.Version, e.NivelServicio, e.Notas })
            .FirstOrDefaultAsync(cancellationToken);

        if (subcontrata is null || subcontrata.NivelServicio is null) return null;

        var relacionesVigentes = empresasContext.RelacionesEmpresariales
            .Where(r => r.ProveedoraId == request.Id && r.VigenciaHasta == null);

        var clienteIds = await relacionesVigentes
            .Join(empresasContext.Empresas.Where(e => e.EsCritico != null), r => r.ClienteId, e => e.Id, (r, e) => e.Id)
            .ToListAsync(cancellationToken);

        var empresaIds = await relacionesVigentes
            .Join(empresasContext.Empresas.Where(e => e.EsPropia), r => r.ClienteId, e => e.Id, (r, e) => e.Id)
            .ToListAsync(cancellationToken);

        var veLaNota = await politicaNotaInterna.PuedeLeerAsync(cancellationToken);

        return new SubcontrataDetalleDto(
            subcontrata.Id, subcontrata.RazonSocial, subcontrata.Cif, subcontrata.CreadoEnUtc, clienteIds, empresaIds,
            subcontrata.Version, Enum.Parse<NivelServicioSubcontrata>(subcontrata.NivelServicio),
            Notas: veLaNota ? subcontrata.Notas : null, NotaInternaVisible: veLaNota);
    }
}
