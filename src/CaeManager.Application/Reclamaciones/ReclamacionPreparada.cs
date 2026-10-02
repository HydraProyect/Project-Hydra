using CaeManager.Application.Contactos;

namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// Lo que una reclamación enviaría, decidido sin enviar: documentos que
/// siguen siendo reclamables, destinatarios resueltos de la agenda, asunto y
/// cuerpo. Lo produce <c>PrepararAsync</c> de los handlers de envío (una sola
/// implementación) y lo consumen tanto el envío como la vista previa.
/// </summary>
/// <param name="Correos">Direcciones distintas a las que se enviaría.</param>
/// <param name="Destinatarios">Los mismos destinatarios con su nombre, una fila por dirección.</param>
public sealed record ReclamacionPreparada(
    Guid TitularId,
    string TitularRazonSocial,
    IReadOnlyList<Guid> DocumentoIds,
    IReadOnlyList<string> Correos,
    IReadOnlyList<DestinatarioAgendaDto> Destinatarios,
    string Asunto,
    string CuerpoHtml);
