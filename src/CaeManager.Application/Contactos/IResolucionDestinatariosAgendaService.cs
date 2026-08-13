using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Contactos;

/// <param name="MotivoTipos">Por qué le toca a esta persona: los tipos de documento que tiene asignados dentro de lo que se está reclamando, o "Contacto predeterminado".</param>
public record DestinatarioAgendaDto(Guid ContactoId, string Nombre, string Email, IReadOnlyList<string> MotivoTipos);

/// <summary>
/// Resuelve a QUIÉN se le pide cada documento, usando la agenda de contactos
/// en vez de los usuarios de portal.
///
/// Decisión del usuario (2026-08-13): la agenda es la fuente de destinatarios,
/// y los usuarios de portal dejan de serlo — "pueden ser personas de soporte o
/// no necesariamente quien esté dentro del flujo, puede ser hasta el dueño de
/// la empresa o el comercial". Reclamar documentación al dueño porque tiene
/// cuenta en el portal es exactamente el error que esto corrige.
///
/// Orden de resolución, por cada tipo de documento reclamado:
/// 1. Contactos de la agenda del CENTRO (si la reclamación va acotada a uno)
///    que tengan ese tipo asignado — lo más específico gana.
/// 2. Contactos de la agenda del CLIENTE con ese tipo asignado.
/// 3. Si nadie tiene ese tipo, el contacto marcado como predeterminado
///    (primero el del Centro, si lo hay; si no, el del Cliente).
///
/// Un tipo sin dueño y sin predeterminado NO cae de vuelta a los usuarios de
/// portal: se queda sin destinatario a propósito, y el llamador avisa. Es
/// preferible a mandárselo a quien no toca.
/// </summary>
public interface IResolucionDestinatariosAgendaService
{
    Task<IReadOnlyList<DestinatarioAgendaDto>> ResolverAsync(
        Guid clienteId, Guid? centroId, IReadOnlyList<Guid> tipoDocumentoIds, CancellationToken cancellationToken = default);
}

public class ResolucionDestinatariosAgendaService(
    IContactosAgendaQueryContext contactosContext,
    TiposDocumento.ITiposDocumentoQueryContext tiposDocumentoContext)
    : IResolucionDestinatariosAgendaService
{
    public async Task<IReadOnlyList<DestinatarioAgendaDto>> ResolverAsync(
        Guid clienteId, Guid? centroId, IReadOnlyList<Guid> tipoDocumentoIds, CancellationToken cancellationToken = default)
    {
        var contactos = await contactosContext.ContactosAgenda
            .Where(c => c.ClienteId == clienteId || (centroId != null && c.CentroId == centroId))
            .Select(c => new
            {
                c.Id,
                c.Nombre,
                c.Email,
                c.EsPredeterminado,
                EsDelCentro = c.CentroId != null
            })
            .ToListAsync(cancellationToken);

        if (contactos.Count == 0) return [];

        var contactoIds = contactos.Select(c => c.Id).ToList();
        var tiposUnicos = tipoDocumentoIds.Distinct().ToList();

        var vinculos = await contactosContext.ContactosAgendaTiposDocumento
            .Where(v => contactoIds.Contains(v.ContactoAgendaId) && tiposUnicos.Contains(v.TipoDocumentoId))
            .Select(v => new { v.ContactoAgendaId, v.TipoDocumentoId })
            .ToListAsync(cancellationToken);

        var nombresTipo = await tiposDocumentoContext.TiposDocumento
            .Where(t => tiposUnicos.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        // ContactoId → motivos por los que entra en la lista.
        var motivosPorContacto = new Dictionary<Guid, List<string>>();
        var tiposSinDuenio = new List<Guid>();

        foreach (var tipoId in tiposUnicos)
        {
            var candidatos = vinculos.Where(v => v.TipoDocumentoId == tipoId).Select(v => v.ContactoAgendaId).ToList();

            // Lo más específico gana: si algún contacto del propio Centro tiene
            // este tipo, los del Cliente no entran para este tipo.
            var deCentro = candidatos.Where(id => contactos.First(c => c.Id == id).EsDelCentro).ToList();
            var elegidos = deCentro.Count > 0 ? deCentro : candidatos;

            if (elegidos.Count == 0)
            {
                tiposSinDuenio.Add(tipoId);
                continue;
            }

            foreach (var contactoId in elegidos)
                AgregarMotivo(motivosPorContacto, contactoId, nombresTipo.GetValueOrDefault(tipoId, "Documento"));
        }

        if (tiposSinDuenio.Count > 0)
        {
            var predeterminadosDeCentro = contactos.Where(c => c.EsPredeterminado && c.EsDelCentro).ToList();
            var predeterminados = predeterminadosDeCentro.Count > 0
                ? predeterminadosDeCentro
                : contactos.Where(c => c.EsPredeterminado && !c.EsDelCentro).ToList();

            foreach (var predeterminado in predeterminados)
                AgregarMotivo(motivosPorContacto, predeterminado.Id, "Contacto predeterminado");
        }

        return motivosPorContacto
            .Select(par =>
            {
                var contacto = contactos.First(c => c.Id == par.Key);
                return new DestinatarioAgendaDto(contacto.Id, contacto.Nombre, contacto.Email, par.Value);
            })
            .OrderBy(d => d.Nombre)
            .ToList();
    }

    private static void AgregarMotivo(Dictionary<Guid, List<string>> motivos, Guid contactoId, string motivo)
    {
        if (!motivos.TryGetValue(contactoId, out var lista))
            motivos[contactoId] = lista = [];

        if (!lista.Contains(motivo))
            lista.Add(motivo);
    }
}
