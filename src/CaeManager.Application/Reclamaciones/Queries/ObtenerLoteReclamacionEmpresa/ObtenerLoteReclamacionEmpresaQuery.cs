using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Contactos;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionEmpresa;

/// <summary>
/// Agrupa por Empresa contraparte titular sus Documentos de ámbito Empresa
/// que vencen dentro de los próximos 3 meses o ya vencieron, para reclamarlos
/// en un único correo por Empresa — la mitad "documentos de empresa" del
/// selector tipo × ámbito (DEC-7, caso literal "todos los documentos de
/// empresa de una empresa").
///
/// Hermana de <c>ObtenerLoteReclamacionQuery</c>, no una variante suya: allí
/// el titular se DEDUCE recorriendo Trabajador→Asignación activa→Centro→
/// Cliente, porque un Documento de Trabajador no dice a quién hay que
/// reclamárselo; aquí el titular ES el propietario del documento
/// (<c>Documento.EmpresaId</c>), sin ningún salto intermedio. Mismos
/// criterios de reclamable —ventana de 3 meses, sin filtrar por Estado (ver
/// el comentario de su hermana sobre por qué)— y misma forma de salida.
///
/// El ámbito se comprueba contra el TipoDocumento y no contra
/// <c>Documento.Ambito</c>: esa propiedad se calcula en C# (no se traduce a
/// SQL) y además devuelve <c>Empresa</c> por descarte cuando las cinco anclas
/// están en null, así que usarla aquí dejaría entrar documentos huérfanos
/// como si fueran de empresa.
/// </summary>
/// <param name="EmpresaId">Null = todas las Empresas visibles (respetando IAlcanceDatosService); con valor = esa Empresa concreta.</param>
/// <param name="TipoDocumentoIds">Null = todos los tipos de documento de ámbito Empresa.</param>
public record ObtenerLoteReclamacionEmpresaQuery(
    Guid? EmpresaId = null, IReadOnlyList<Guid>? TipoDocumentoIds = null)
    : IRequest<IReadOnlyList<LoteReclamacionEmpresaDto>>;

public record LoteReclamacionEmpresaDto(
    Guid EmpresaId,
    string RazonSocialEmpresa,
    DateTime? UltimaReclamacionFechaUtc,
    IReadOnlyList<DocumentoReclamableDto> Documentos,
    Guid? UltimaReclamacionConversacionId = null,
    IReadOnlyList<DestinatarioAgendaDto>? Destinatarios = null);

public class ObtenerLoteReclamacionEmpresaQueryHandler(
    IConfiguracionQueryContext configuracionContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IEmpresasQueryContext empresasContext,
    IReclamacionesQueryContext reclamacionesContext,
    IAlcanceDatosService alcanceDatos,
    IResolucionDestinatariosAgendaService resolucionDestinatarios)
    : IRequestHandler<ObtenerLoteReclamacionEmpresaQuery, IReadOnlyList<LoteReclamacionEmpresaDto>>
{
    public async Task<IReadOnlyList<LoteReclamacionEmpresaDto>> Handle(
        ObtenerLoteReclamacionEmpresaQuery request, CancellationToken cancellationToken)
    {
        // Alcance de cartera de Empresas, no de Clientes: la existencia por
        // tenant no basta (CLAUDE.md § 14). null = rol sin restricción; lista
        // vacía = cartera todavía sin asignar, y entonces el Contains no deja
        // pasar nada, que es el fallo cerrado correcto.
        //
        // Y de GESTIÓN: reclamar es operar sobre la Empresa, no leer su
        // documentación, así que el rol Cliente (portal) no entra aunque la
        // contratista esté relacionada con su propio Cliente.
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var limiteVentana = hoy.AddMonths(3);

        var filas = await (
            from documento in documentosContext.Documentos
            where documento.EmpresaId != null
            where request.EmpresaId == null || documento.EmpresaId == request.EmpresaId
            where empresaIdsVisibles == null || empresaIdsVisibles.Contains(documento.EmpresaId!.Value)
            where documento.FechaVencimiento != null && documento.FechaVencimiento <= limiteVentana
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where tipoDocumento.AmbitoAplicacion == AmbitoAplicacion.Empresa
            where request.TipoDocumentoIds == null || request.TipoDocumentoIds.Contains(tipoDocumento.Id)
            join empresa in empresasContext.Empresas on documento.EmpresaId!.Value equals empresa.Id
            select new
            {
                empresa.Id,
                empresa.RazonSocial,
                DocumentoId = documento.Id,
                TipoDocumentoId = tipoDocumento.Id,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaVencimiento
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        // Solo las de titular Empresa: una reclamación anterior a la MISMA
        // Empresa en posición de cliente (ClienteId) es otro asunto, con otros
        // documentos y otros destinatarios — mezclarlas mostraría "última
        // reclamación: hace 2 días" sobre algo que nunca se reclamó.
        var ultimasReclamaciones = await reclamacionesContext.ReclamacionesDocumentales
            .Where(r => r.EmpresaId != null)
            .GroupBy(r => r.EmpresaId!.Value)
            .Select(g => new
            {
                EmpresaId = g.Key,
                Ultima = (DateTime?)g.Max(r => r.FechaEnvioUtc),
                ConversacionId = g.OrderByDescending(r => r.FechaEnvioUtc).Select(r => r.ConversacionId).FirstOrDefault()
            })
            .ToDictionaryAsync(x => x.EmpresaId, x => x, cancellationToken);

        var documentosPorEmpresa = filas
            .GroupBy(f => new { f.Id, f.RazonSocial })
            .Select(grupo => (
                Empresa: grupo.Key,
                Documentos: grupo
                    .Select(f => new DocumentoReclamableDto(
                        f.DocumentoId, null, null, f.TipoDocumentoId, f.TipoDocumentoNombre,
                        f.FechaVencimiento!.Value,
                        CalculadoraEstadoDocumento.Calcular(
                            f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)))
                    .OrderBy(d => d.FechaVencimiento)
                    .ToList()))
            .Where(x => x.Documentos.Count > 0)
            .ToList();

        var tipoDocumentoIdsPorEmpresa = documentosPorEmpresa
            .ToDictionary(
                x => x.Empresa.Id,
                IReadOnlyList<Guid> (x) => x.Documentos.Select(d => d.TipoDocumentoId).Distinct().ToList());

        var destinatariosPorEmpresa = await resolucionDestinatarios.ResolverParaEmpresasAsync(
            tipoDocumentoIdsPorEmpresa, cancellationToken);

        return documentosPorEmpresa
            .Select(x =>
            {
                var ultima = ultimasReclamaciones.GetValueOrDefault(x.Empresa.Id);
                return new LoteReclamacionEmpresaDto(
                    x.Empresa.Id,
                    x.Empresa.RazonSocial,
                    ultima?.Ultima,
                    x.Documentos,
                    ultima?.ConversacionId,
                    destinatariosPorEmpresa.GetValueOrDefault(x.Empresa.Id, []));
            })
            .OrderByDescending(r => r.Documentos.Any(d => d.Estado == EstadoDocumento.Vencido))
            .ThenBy(r => r.RazonSocialEmpresa)
            .ToList();
    }
}
