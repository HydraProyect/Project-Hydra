using CaeManager.Infrastructure.AsistenteIa;
using CaeManager.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.AsistenteIa;

/// <summary>
/// Qué partes del asistente están configuradas en este entorno. Las consultas se
/// encienden con <c>Anthropic:ApiKey</c> o con el doble de pruebas del arnés E2E; el
/// agente de órdenes necesita además <c>Anthropic:AgenteActivo</c>. Es solo
/// presentación: cada consulta y cada escritura pasan por la comprobación de
/// instrucción de tratamiento con IA y por los Commands de Application.
/// </summary>
public sealed class DisponibilidadAsistente(
    IOptions<AnthropicOptions> anthropic,
    IEnumerable<MarcaAsistenteDePruebas> asistenteDePruebas)
{
    public bool PreguntasDisponibles =>
        !string.IsNullOrWhiteSpace(anthropic.Value.ApiKey) || asistenteDePruebas.Any();

    public bool AgenteDisponible => anthropic.Value.AgenteActivo && !string.IsNullOrWhiteSpace(anthropic.Value.ApiKey);

    public bool Alguna => PreguntasDisponibles || AgenteDisponible;
}
