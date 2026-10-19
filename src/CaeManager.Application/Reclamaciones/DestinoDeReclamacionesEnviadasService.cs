using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// A dónde iría hoy una reclamación ya enviada a la Empresa empleadora (<c>ReclamacionDocumental.ParaEmpresaEmpleadora</c>).
/// </summary>
/// <param name="DelegacionEmpresaId">
/// La Delegación cuyo contacto de reclamación recibiría el lote, o <c>null</c> si va a la agenda de la Empresa (sus
/// Trabajadores no tienen Delegación o la suya no tiene correo). Mismo criterio que
/// <see cref="ReclamacionEmpresaEmpleadoraService.TrabajadoresDelDestinoAsync"/>.
/// </param>
/// <param name="DelegacionEmpresaDeterminada">
/// <c>false</c> cuando sus Trabajadores ya no caen todos en un mismo destino (alguno cambió de Delegación o de Empresa, o
/// ya no existe): el lote no puede reenviarse tal cual y quien lo pinta no ofrece «Reclamar de nuevo».
/// </param>
public sealed record DestinoDeReclamacionEnviadaDto(Guid? DelegacionEmpresaId, bool DelegacionEmpresaDeterminada);

/// <summary>La última reclamación enviada a un destino (Empresa empleadora y Delegación).</summary>
public sealed record UltimaReclamacionEnviada(DateTime FechaEnvioUtc, Guid? ConversacionId);

/// <summary>
/// Reconoce, entre las reclamaciones ya registradas con titular Empresa, las que pidieron documentos de sus Trabajadores
/// («Gestionar», 2026-10-18) y no documentos de la propia Empresa (DEC-7), y deriva su Delegación.
///
/// <para>
/// <b>Por qué se deriva.</b> La reclamación no guarda ni su forma ni su Delegación (no hubo migración: el invariante vive en
/// el dominio). La forma sale de sus líneas: una reclamación con titular Empresa es de documentos de Trabajador si alguna
/// línea nombra a un Trabajador (lo que faltaba) o apunta a un Documento de un Trabajador; una de documentos de empresa no
/// puede tener ninguna de las dos (<c>EnviarReclamacionEmpresaCommand</c> solo acepta documentos de ámbito Empresa). La
/// Delegación sale de la de HOY de esos Trabajadores, con el mismo criterio que el envío: es la que revalidaría
/// «Reclamar de nuevo», así que historial, última reclamación y reenvío no pueden discrepar entre sí.
/// </para>
///
/// <para>
/// No filtra por alcance: recibe identificadores que el llamador ya acotó a su cartera.
/// </para>
/// </summary>
public class DestinoDeReclamacionesEnviadasService(
    IReclamacionesQueryContext reclamacionesContext,
    IDocumentosQueryContext documentosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext)
{
    private sealed record DestinoDeTrabajador(bool Valido, Guid? DelegacionEmpresaId);

    /// <summary>
    /// Los identificadores de las reclamaciones con alguna línea de un Trabajador, para usar como subconsulta. Incluye las de
    /// titular Cliente: quien la use para separar las de titular Empresa la combina con <c>EmpresaId != null</c>.
    /// </summary>
    public static IQueryable<Guid> IdsConDocumentosDeTrabajadores(
        IReclamacionesQueryContext reclamaciones, IDocumentosQueryContext documentos) =>
        reclamaciones.ReclamacionesDocumentalesDocumento
            .Where(l => l.TrabajadorId != null
                || documentos.Documentos.Any(d => d.Id == l.DocumentoId && d.TrabajadorId != null))
            .Select(l => l.ReclamacionDocumentalId);

    /// <summary>
    /// De <paramref name="reclamacionIds"/>, las que son de documentos de Trabajador con titular Empresa, con su destino de
    /// hoy. Las demás (titular Cliente, documentos de empresa) no aparecen en el resultado.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, DestinoDeReclamacionEnviadaDto>> DerivarAsync(
        IReadOnlyCollection<Guid> reclamacionIds, CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<Guid, DestinoDeReclamacionEnviadaDto>();
        if (reclamacionIds.Count == 0)
            return resultado;

        var ids = reclamacionIds.Distinct().ToList();
        var empresaPorReclamacion = await reclamacionesContext.ReclamacionesDocumentales
            .Where(r => ids.Contains(r.Id) && r.EmpresaId != null)
            .Select(r => new { r.Id, EmpresaId = r.EmpresaId!.Value })
            .ToDictionaryAsync(r => r.Id, r => r.EmpresaId, cancellationToken);
        if (empresaPorReclamacion.Count == 0)
            return resultado;

        var deEmpresa = empresaPorReclamacion.Keys.ToList();
        var lineas = await reclamacionesContext.ReclamacionesDocumentalesDocumento
            .Where(l => deEmpresa.Contains(l.ReclamacionDocumentalId))
            .Select(l => new { l.ReclamacionDocumentalId, l.DocumentoId, l.TrabajadorId })
            .ToListAsync(cancellationToken);

        var documentoIds = lineas.Where(l => l.DocumentoId is not null).Select(l => l.DocumentoId!.Value).Distinct().ToList();
        var trabajadorDelDocumento = documentoIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await documentosContext.Documentos
                .Where(d => documentoIds.Contains(d.Id) && d.TrabajadorId != null)
                .Select(d => new { d.Id, TrabajadorId = d.TrabajadorId!.Value })
                .ToDictionaryAsync(d => d.Id, d => d.TrabajadorId, cancellationToken);

        Guid? TrabajadorDe(Guid? trabajadorId, Guid? documentoId) =>
            trabajadorId ?? (documentoId is { } id && trabajadorDelDocumento.TryGetValue(id, out var deDocumento) ? (Guid?)deDocumento : null);

        var trabajadoresPorReclamacion = lineas
            .Select(l => (l.ReclamacionDocumentalId, TrabajadorId: TrabajadorDe(l.TrabajadorId, l.DocumentoId)))
            .Where(x => x.TrabajadorId is not null)
            .GroupBy(x => x.ReclamacionDocumentalId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.TrabajadorId!.Value).Distinct().ToList());
        if (trabajadoresPorReclamacion.Count == 0)
            return resultado;

        var trabajadorIds = trabajadoresPorReclamacion.Values.SelectMany(t => t).Distinct().ToList();
        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => new { t.Id, t.EmpresaId, t.DelegacionEmpresaId })
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        var empresaIds = trabajadoresPorReclamacion.Keys.Select(id => empresaPorReclamacion[id]).Distinct().ToList();
        var delegacionesEmpresaConCorreo = (await empresasContext.DelegacionesEmpresa
                .Where(d => empresaIds.Contains(d.EmpresaId) && !d.EstaEliminado)
                .Select(d => new { d.Id, d.ContactoReclamacionCorreo })
                .ToListAsync(cancellationToken))
            .Where(d => DestinatariosExtraDeReclamacion.EsCorreoUtilizable(d.ContactoReclamacionCorreo ?? string.Empty))
            .Select(d => d.Id)
            .ToHashSet();

        foreach (var (reclamacionId, deLaReclamacion) in trabajadoresPorReclamacion)
        {
            var empresaId = empresaPorReclamacion[reclamacionId];
            var destinos = deLaReclamacion
                .Select(id => trabajadores.TryGetValue(id, out var trabajador) && trabajador.EmpresaId == empresaId
                    ? new DestinoDeTrabajador(true,
                        trabajador.DelegacionEmpresaId is { } delegacionId && delegacionesEmpresaConCorreo.Contains(delegacionId) ? delegacionId : null)
                    : new DestinoDeTrabajador(false, null))
                .Distinct()
                .ToList();

            resultado[reclamacionId] = destinos is [{ Valido: true } unico]
                ? new DestinoDeReclamacionEnviadaDto(unico.DelegacionEmpresaId, true)
                : new DestinoDeReclamacionEnviadaDto(null, false);
        }

        return resultado;
    }

    /// <summary>
    /// La última reclamación de documentos de Trabajador enviada a cada destino (Empresa y Delegación) de
    /// <paramref name="empresaIds"/>. Solo cuentan las de destino determinado: una cuyo destino ya no es uno solo no es «la
    /// última» de ninguno.
    /// </summary>
    public async Task<IReadOnlyDictionary<(Guid EmpresaId, Guid? DelegacionEmpresaId), UltimaReclamacionEnviada>> UltimasPorDestinoAsync(
        IReadOnlyCollection<Guid> empresaIds, CancellationToken cancellationToken)
    {
        if (empresaIds.Count == 0)
            return new Dictionary<(Guid, Guid?), UltimaReclamacionEnviada>();

        var ids = empresaIds.Distinct().ToList();
        var reclamaciones = await reclamacionesContext.ReclamacionesDocumentales
            .Where(r => r.EmpresaId != null && ids.Contains(r.EmpresaId!.Value))
            .Select(r => new { r.Id, EmpresaId = r.EmpresaId!.Value, r.FechaEnvioUtc, r.ConversacionId })
            .ToListAsync(cancellationToken);

        var destinos = await DerivarAsync([.. reclamaciones.Select(r => r.Id)], cancellationToken);

        return reclamaciones
            .Where(r => destinos.TryGetValue(r.Id, out var destino) && destino.DelegacionEmpresaDeterminada)
            .GroupBy(r => (r.EmpresaId, destinos[r.Id].DelegacionEmpresaId))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.FechaEnvioUtc).Select(r => new UltimaReclamacionEnviada(r.FechaEnvioUtc, r.ConversacionId)).First());
    }
}
