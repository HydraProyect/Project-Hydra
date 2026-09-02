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

    /// <summary>
    /// Igual que <see cref="ResolverAsync"/> pero para N Clientes de una sola
    /// vez: una consulta de contactos y una de vínculos sobre la unión de
    /// todos, en vez de repetir ambas por Cliente (ver
    /// ObtenerLoteReclamacionQuery, caso CentroId null: un Cliente por email
    /// de reclamación, así que sin batch son 2 queries × Cliente visible).
    /// <paramref name="centroId"/> es único para toda la llamada, no por
    /// Cliente — igual que en ObtenerLoteReclamacionQuery, de donde viene:
    /// un Centro pertenece a un único Cliente, así que cuando va acotada a
    /// uno el diccionario de entrada solo trae esa entrada de todos modos.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<DestinatarioAgendaDto>>> ResolverParaClientesAsync(
        Guid? centroId, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> tipoDocumentoIdsPorCliente, CancellationToken cancellationToken = default);

    /// <summary>
    /// Igual que <see cref="ResolverAsync"/> pero sobre la agenda de una
    /// Empresa contraparte (<c>ContactoAgenda.EmpresaId</c>) — la que se usa
    /// al reclamar documentos de empresa (DEC-7), cuyo titular es la propia
    /// Empresa y no un Cliente.
    ///
    /// Sin escalón de Centro, y no por olvido: un Centro pertenece a un
    /// Cliente (<c>Centro.ClienteId</c>), así que no hay agenda de Centro
    /// "más específica" que la de la Empresa titular. El resto del orden es
    /// idéntico: quien tenga el tipo de documento asignado y, para los tipos
    /// sin dueño, el contacto predeterminado de esa Empresa.
    /// </summary>
    Task<IReadOnlyList<DestinatarioAgendaDto>> ResolverParaEmpresaAsync(
        Guid empresaId, IReadOnlyList<Guid> tipoDocumentoIds, CancellationToken cancellationToken = default);

    /// <summary>Versión por lote de <see cref="ResolverParaEmpresaAsync"/> — mismo motivo que <see cref="ResolverParaClientesAsync"/>: una consulta sobre la unión, no dos por Empresa.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<DestinatarioAgendaDto>>> ResolverParaEmpresasAsync(
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> tipoDocumentoIdsPorEmpresa, CancellationToken cancellationToken = default);
}

public class ResolucionDestinatariosAgendaService(
    IContactosAgendaQueryContext contactosContext,
    TiposDocumento.ITiposDocumentoQueryContext tiposDocumentoContext)
    : IResolucionDestinatariosAgendaService
{
    /// <param name="PropietarioId">Cliente o Empresa contraparte dueña de la agenda, según por qué rama se haya resuelto.</param>
    private sealed record Contacto(Guid Id, Guid? PropietarioId, string Nombre, string Email, bool EsPredeterminado, bool EsDelCentro);
    private sealed record Vinculo(Guid ContactoAgendaId, Guid TipoDocumentoId);

    public async Task<IReadOnlyList<DestinatarioAgendaDto>> ResolverAsync(
        Guid clienteId, Guid? centroId, IReadOnlyList<Guid> tipoDocumentoIds, CancellationToken cancellationToken = default)
    {
        var resultado = await ResolverParaClientesAsync(
            centroId,
            new Dictionary<Guid, IReadOnlyList<Guid>> { [clienteId] = tipoDocumentoIds },
            cancellationToken);

        return resultado.GetValueOrDefault(clienteId, []);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DestinatarioAgendaDto>>> ResolverParaClientesAsync(
        Guid? centroId, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> tipoDocumentoIdsPorCliente, CancellationToken cancellationToken = default)
    {
        if (tipoDocumentoIdsPorCliente.Count == 0) return new Dictionary<Guid, IReadOnlyList<DestinatarioAgendaDto>>();

        var clienteIds = tipoDocumentoIdsPorCliente.Keys.ToList();

        var contactos = await contactosContext.ContactosAgenda
            .Where(c => (c.ClienteId != null && clienteIds.Contains(c.ClienteId.Value)) || (centroId != null && c.CentroId == centroId))
            .Select(c => new Contacto(c.Id, c.ClienteId, c.Nombre, c.Email, c.EsPredeterminado, c.CentroId != null))
            .ToListAsync(cancellationToken);

        var resultado = new Dictionary<Guid, IReadOnlyList<DestinatarioAgendaDto>>();

        if (contactos.Count == 0)
        {
            foreach (var clienteId in clienteIds)
                resultado[clienteId] = [];

            return resultado;
        }

        var contactoIds = contactos.Select(c => c.Id).ToList();
        var tiposUnicos = tipoDocumentoIdsPorCliente.Values.SelectMany(t => t).Distinct().ToList();

        var vinculos = await contactosContext.ContactosAgendaTiposDocumento
            .Where(v => contactoIds.Contains(v.ContactoAgendaId) && tiposUnicos.Contains(v.TipoDocumentoId))
            .Select(v => new Vinculo(v.ContactoAgendaId, v.TipoDocumentoId))
            .ToListAsync(cancellationToken);

        var nombresTipo = await tiposDocumentoContext.TiposDocumento
            .Where(t => tiposUnicos.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        foreach (var (clienteId, tipoDocumentoIds) in tipoDocumentoIdsPorCliente)
        {
            // Los del propio Cliente, más los del Centro compartido de la
            // llamada si lo hay (a lo sumo un Cliente en ese caso, ver docstring).
            var contactosDelCliente = contactos.Where(c => c.PropietarioId == clienteId || (centroId != null && c.EsDelCentro)).ToList();

            resultado[clienteId] = Resolver(contactosDelCliente, vinculos, nombresTipo, tipoDocumentoIds.Distinct().ToList());
        }

        return resultado;
    }

    public async Task<IReadOnlyList<DestinatarioAgendaDto>> ResolverParaEmpresaAsync(
        Guid empresaId, IReadOnlyList<Guid> tipoDocumentoIds, CancellationToken cancellationToken = default)
    {
        var resultado = await ResolverParaEmpresasAsync(
            new Dictionary<Guid, IReadOnlyList<Guid>> { [empresaId] = tipoDocumentoIds },
            cancellationToken);

        return resultado.GetValueOrDefault(empresaId, []);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DestinatarioAgendaDto>>> ResolverParaEmpresasAsync(
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> tipoDocumentoIdsPorEmpresa, CancellationToken cancellationToken = default)
    {
        if (tipoDocumentoIdsPorEmpresa.Count == 0) return new Dictionary<Guid, IReadOnlyList<DestinatarioAgendaDto>>();

        var empresaIds = tipoDocumentoIdsPorEmpresa.Keys.ToList();

        var contactos = await contactosContext.ContactosAgenda
            .Where(c => c.EmpresaId != null && empresaIds.Contains(c.EmpresaId.Value))
            .Select(c => new Contacto(c.Id, c.EmpresaId, c.Nombre, c.Email, c.EsPredeterminado, false))
            .ToListAsync(cancellationToken);

        var resultado = new Dictionary<Guid, IReadOnlyList<DestinatarioAgendaDto>>();

        if (contactos.Count == 0)
        {
            foreach (var empresaId in empresaIds)
                resultado[empresaId] = [];

            return resultado;
        }

        var contactoIds = contactos.Select(c => c.Id).ToList();
        var tiposUnicos = tipoDocumentoIdsPorEmpresa.Values.SelectMany(t => t).Distinct().ToList();

        var vinculos = await contactosContext.ContactosAgendaTiposDocumento
            .Where(v => contactoIds.Contains(v.ContactoAgendaId) && tiposUnicos.Contains(v.TipoDocumentoId))
            .Select(v => new Vinculo(v.ContactoAgendaId, v.TipoDocumentoId))
            .ToListAsync(cancellationToken);

        var nombresTipo = await tiposDocumentoContext.TiposDocumento
            .Where(t => tiposUnicos.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        foreach (var (empresaId, tipoDocumentoIds) in tipoDocumentoIdsPorEmpresa)
        {
            var contactosDeLaEmpresa = contactos.Where(c => c.PropietarioId == empresaId).ToList();
            resultado[empresaId] = Resolver(contactosDeLaEmpresa, vinculos, nombresTipo, tipoDocumentoIds.Distinct().ToList());
        }

        return resultado;
    }

    private static IReadOnlyList<DestinatarioAgendaDto> Resolver(
        List<Contacto> contactos, List<Vinculo> vinculosGlobales, Dictionary<Guid, string> nombresTipo, List<Guid> tiposUnicos)
    {
        if (contactos.Count == 0) return [];

        var contactoIds = contactos.Select(c => c.Id).ToHashSet();
        var vinculos = vinculosGlobales.Where(v => contactoIds.Contains(v.ContactoAgendaId)).ToList();

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
