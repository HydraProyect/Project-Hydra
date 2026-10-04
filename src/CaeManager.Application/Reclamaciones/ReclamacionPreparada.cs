using CaeManager.Application.Contactos;
using CaeManager.Domain.Reclamaciones;

namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// Lo que una reclamación enviaría, decidido sin enviar: documentos que
/// siguen siendo reclamables, destinatarios resueltos de la agenda, asunto y
/// cuerpo. Lo produce <c>PrepararAsync</c> de los handlers de envío (una sola
/// implementación) y lo consumen tanto el envío como la vista previa.
/// </summary>
/// <param name="Correos">Direcciones distintas a las que se enviaría.</param>
/// <param name="Destinatarios">Los mismos destinatarios con su nombre, una fila por dirección.</param>
/// <param name="DocumentoIds">Todos los Documentos que el envío registra como línea: los que vencen y los «Sin confirmar» sin fecha.</param>
/// <param name="Pendientes">
/// Lo pedido sin vencimiento, ya revalidado (<see cref="IPendientesDeReclamacionService"/>): los documentos que faltan y los
/// «Sin confirmar» sin fecha (estos últimos también van en <paramref name="DocumentoIds"/>). Vacío en una reclamación por vencimiento de siempre.
/// </param>
public sealed record ReclamacionPreparada(
    Guid TitularId,
    string TitularRazonSocial,
    IReadOnlyList<Guid> DocumentoIds,
    IReadOnlyList<string> Correos,
    IReadOnlyList<DestinatarioAgendaDto> Destinatarios,
    string Asunto,
    string CuerpoHtml,
    IReadOnlyList<DocumentoPendienteDto>? Pendientes = null)
{
    /// <summary>Los documentos que faltan, tal como los guarda el registro (no hay Documento al que apuntar).</summary>
    public IReadOnlyList<DocumentoQueFaltaPedido> DocumentosQueFaltan =>
        [.. (Pendientes ?? []).Where(p => p.Motivo == MotivoPendienteDeReclamacion.Ausente)
            .Select(p => new DocumentoQueFaltaPedido(p.TipoDocumentoId, p.TrabajadorId))];
}
