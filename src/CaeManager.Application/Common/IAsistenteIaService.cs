using CaeManager.Domain.Common;

namespace CaeManager.Application.Common;

public enum RolMensajeChat
{
    Usuario,
    Asistente
}

public record MensajeChatDto(RolMensajeChat Rol, string Texto);

/// <summary>
/// Chat "Pregúntale a Hydra" — Etapa 1 (ver ROADMAP.md § Iniciativa de IA):
/// especialista general en legislación PRL europea/española, sin acceso a
/// datos reales de Clientes/Trabajadores/Documentos. No confundir con un
/// futuro asistente con contexto de "lo que estoy viendo" (Etapa 2, todavía
/// sin construir) — esa versión sí necesitaría revisar qué datos es seguro
/// mandar a la API externa, esta no, porque nunca ve ningún dato de negocio.
/// </summary>
public interface IAsistenteIaService
{
    Task<Result<string>> PreguntarAsync(IReadOnlyList<MensajeChatDto> historial, CancellationToken cancellationToken);
}
