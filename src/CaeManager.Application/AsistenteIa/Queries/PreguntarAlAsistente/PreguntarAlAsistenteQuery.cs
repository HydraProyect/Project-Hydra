using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;

/// <summary>
/// Historial completo del chat (no solo la última pregunta) porque la API
/// de Anthropic no mantiene estado entre llamadas — cada petición manda la
/// conversación entera para que el modelo tenga contexto de los turnos
/// anteriores.
/// </summary>
public record PreguntarAlAsistenteQuery(IReadOnlyList<MensajeChatDto> Historial) : IRequest<Result<string>>;

public class PreguntarAlAsistenteQueryHandler(IAsistenteIaService asistenteIa)
    : IRequestHandler<PreguntarAlAsistenteQuery, Result<string>>
{
    public Task<Result<string>> Handle(PreguntarAlAsistenteQuery request, CancellationToken cancellationToken) =>
        asistenteIa.PreguntarAsync(request.Historial, cancellationToken);
}
