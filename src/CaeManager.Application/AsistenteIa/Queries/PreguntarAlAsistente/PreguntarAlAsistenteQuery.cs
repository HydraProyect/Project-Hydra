using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;

/// <summary>
/// Historial completo del chat (no solo la última pregunta) porque la API
/// de Anthropic no mantiene estado entre llamadas — cada petición manda la
/// conversación entera para que el modelo tenga contexto de los turnos
/// anteriores.
///
/// Único de los cinco tratamientos de <c>tecnico/docs/POLITICA-TECNICA-IA.md</c>
/// § 1 que no tenía, hasta este incremento, ningún control de cumplimiento:
/// el Nivel 0 (DEC-33, REC-035) es el primer gate que le aplica — lo que el
/// usuario escribe en el chat puede incluir datos personales, y viaja al
/// mismo proveedor externo que los demás tratamientos.
/// </summary>
public record PreguntarAlAsistenteQuery(IReadOnlyList<MensajeChatDto> Historial) : IRequest<Result<string>>;

public class PreguntarAlAsistenteQueryHandler(
    IAsistenteIaService asistenteIa, IInstruccionTratamientoIaService instruccionTratamientoIa, ITenantActual tenantActual)
    : IRequestHandler<PreguntarAlAsistenteQuery, Result<string>>
{
    public async Task<Result<string>> Handle(PreguntarAlAsistenteQuery request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId || !await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken))
            return Result.Fallo<string>(Error.Crear(
                "AsistenteIa.SinInstruccion",
                "Este tenant todavía no tiene una instrucción de tratamiento con IA vigente — el asistente no puede procesar tu mensaje."));

        return await asistenteIa.PreguntarAsync(request.Historial, cancellationToken);
    }
}
