using CaeManager.Domain.Common;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Redacta un borrador de respuesta al último entrante de una conversación. Solo recibe lo mínimo: el asunto y el cuerpo
/// de ese mensaje, que escribió un tercero y son dato no fiable. La implementación los sanea antes de que salgan de
/// TALVEG. Devuelve texto plano; quien lo pide es responsable de consultar antes la instrucción de tratamiento.
/// </summary>
public interface IRedaccionRespuestaIa
{
    Task<Result<string>> RedactarAsync(string asunto, string cuerpoUltimoEntrante, CancellationToken cancellationToken = default);
}
