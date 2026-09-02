using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionEmpresa;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;

/// <summary>
/// Traduce un <see cref="FiltroLoteDocumental"/> (selector tipo × ámbito,
/// DEC-7) a lotes reclamables agrupados por titular — la pieza que conecta
/// SelectorLoteDocumental con el envío real. Un dispatcher fino por ámbito,
/// no una reimplementación: cada ámbito ya tiene (o tendrá) su propia query
/// de resolución con el join correcto, esta solo homogeneiza la forma de
/// salida para que el llamador no necesite saber qué ámbito está mirando.
///
/// Trabajador y Empresa son los dos ámbitos con camino de reclamación
/// construido (DEC-11: primero el camino, después la superficie). Cliente,
/// Vehículo y Proyecto siguen lanzando <see cref="NotSupportedException"/> a
/// propósito: SelectorLoteDocumental.AmbitosDisponibles es quien decide qué
/// ámbitos ofrece cada pantalla, así que ese caso solo se alcanza si un
/// llamador ofrece un ámbito que no debería — fallar alto y claro es
/// preferible a devolver una lista vacía que se confunda con "sin
/// pendientes".
/// </summary>
public record ObtenerLoteReclamacionPorFiltroQuery(FiltroLoteDocumental Filtro)
    : IRequest<IReadOnlyList<LoteReclamacionAgrupadoDto>>;

/// <param name="TitularId">
/// A quién se le envía la reclamación — para Ambito=Trabajador es el Cliente
/// dueño del Centro donde está asignado cada Trabajador (mismo criterio que
/// ObtenerLoteReclamacionQuery hoy), NO el Trabajador mismo: un lote agrupa
/// varios documentos de varios trabajadores en un único correo por Cliente.
/// FiltroLoteDocumental.EntidadId, cuando Ambito=Trabajador, es el
/// TrabajadorId elegido en el selector (filtra QUÉ documentos entran, no A
/// QUIÉN se le envían) — puede seguir resolviendo a varios Clientes distintos
/// si ese Trabajador tiene Asignaciones activas en Centros de más de uno.
///
/// Para Ambito=Empresa el titular ES la entidad del filtro: los documentos de
/// empresa cuelgan directamente de su Empresa, sin Centro por medio, así que
/// EntidadId y TitularId coinciden. Por eso el llamador NO puede tratar
/// TitularId como un ClienteId: es el Ambito de este mismo DTO el que dice a
/// qué comando de envío corresponde (EnviarReclamacionCommand para Cliente,
/// EnviarReclamacionEmpresaCommand para Empresa).
/// </param>
public record LoteReclamacionAgrupadoDto(
    Guid TitularId,
    string TitularNombre,
    AmbitoAplicacion Ambito,
    DateTime? UltimaReclamacionFechaUtc,
    IReadOnlyList<DocumentoReclamableDto> Documentos,
    Guid? UltimaReclamacionConversacionId,
    IReadOnlyList<DestinatarioAgendaDto>? Destinatarios);

public class ObtenerLoteReclamacionPorFiltroQueryHandler(IMediator mediator)
    : IRequestHandler<ObtenerLoteReclamacionPorFiltroQuery, IReadOnlyList<LoteReclamacionAgrupadoDto>>
{
    public async Task<IReadOnlyList<LoteReclamacionAgrupadoDto>> Handle(
        ObtenerLoteReclamacionPorFiltroQuery request, CancellationToken cancellationToken)
    {
        var filtro = request.Filtro;

        return filtro.Ambito switch
        {
            AmbitoAplicacion.Trabajador => await ResolverTrabajadorAsync(mediator, filtro, cancellationToken),
            AmbitoAplicacion.Empresa => await ResolverEmpresaAsync(mediator, filtro, cancellationToken),
            _ => throw new NotSupportedException(
                $"Todavía no hay camino de reclamación para el ámbito {filtro.Ambito} — no lo ofrezcas en SelectorLoteDocumental.AmbitosDisponibles.")
        };
    }

    private static async Task<IReadOnlyList<LoteReclamacionAgrupadoDto>> ResolverTrabajadorAsync(
        IMediator mediator, FiltroLoteDocumental filtro, CancellationToken cancellationToken)
    {
        var lotes = await mediator.Send(
            new ObtenerLoteReclamacionQuery(
                TrabajadorId: filtro.EntidadId,
                TipoDocumentoIds: filtro.TipoDocumentoIds.Count > 0 ? filtro.TipoDocumentoIds : null),
            cancellationToken);

        return lotes
            .Select(l => new LoteReclamacionAgrupadoDto(
                l.ClienteId, l.RazonSocialCliente, AmbitoAplicacion.Trabajador,
                l.UltimaReclamacionFechaUtc, l.Documentos, l.UltimaReclamacionConversacionId, l.Destinatarios))
            .ToList();
    }

    private static async Task<IReadOnlyList<LoteReclamacionAgrupadoDto>> ResolverEmpresaAsync(
        IMediator mediator, FiltroLoteDocumental filtro, CancellationToken cancellationToken)
    {
        var lotes = await mediator.Send(
            new ObtenerLoteReclamacionEmpresaQuery(
                EmpresaId: filtro.EntidadId,
                TipoDocumentoIds: filtro.TipoDocumentoIds.Count > 0 ? filtro.TipoDocumentoIds : null),
            cancellationToken);

        return lotes
            .Select(l => new LoteReclamacionAgrupadoDto(
                l.EmpresaId, l.RazonSocialEmpresa, AmbitoAplicacion.Empresa,
                l.UltimaReclamacionFechaUtc, l.Documentos, l.UltimaReclamacionConversacionId, l.Destinatarios))
            .ToList();
    }
}
