using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// Fake de <see cref="IRegistroEnvioReclamacionService"/>: no manda correo ni
/// escribe historial, solo graba con qué se le llamó y devuelve la
/// respuesta que el test configure. Sirve para comprobar, desde los tests de
/// los dos Commands, que la cola común NUNCA se invoca cuando el envío debe
/// fallar entero por "todo o nada" (VecesLlamado se queda en 0).
/// </summary>
public class RegistroEnvioReclamacionServiceFalso : IRegistroEnvioReclamacionService
{
    public Result RespuestaAEnviar { get; set; } = Result.Exito();
    public int VecesLlamado { get; private set; }
    public TitularReclamacion? UltimoTitular { get; private set; }
    public IReadOnlyList<Guid>? UltimosDocumentoIds { get; private set; }
    public IReadOnlyList<string>? UltimosDestinatarios { get; private set; }

    public Task<Result> EnviarYRegistrarAsync(
        TitularReclamacion titular,
        IReadOnlyList<Guid> documentoIds,
        IReadOnlyList<string> destinatarios,
        string asunto,
        string cuerpoHtml,
        CancellationToken cancellationToken)
    {
        VecesLlamado++;
        UltimoTitular = titular;
        UltimosDocumentoIds = documentoIds;
        UltimosDestinatarios = destinatarios;
        return Task.FromResult(RespuestaAEnviar);
    }
}
