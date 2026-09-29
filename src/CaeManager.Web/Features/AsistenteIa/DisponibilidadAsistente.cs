using CaeManager.Infrastructure.AsistenteIa;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.AsistenteIa;

/// <summary>
/// Qué partes del asistente están configuradas en este entorno. Son dos proveedores
/// distintos y se encienden por separado:
/// <list type="bullet">
/// <item><see cref="PreguntasDisponibles"/>: el chat de consultas normativas, con
/// <c>Anthropic:ApiKey</c>.</item>
/// <item><see cref="AgenteDisponible"/>: el agente que propone planes de gestión, con
/// <c>TypeSafe:Activo</c> <b>y</b> <c>TypeSafe:ApiKey</c> (las dos llaves que exige el
/// adaptador; ver <see cref="TypeSafeOptions"/>). Apagado por defecto: activarlo con
/// datos reales exige un DPA firmado y registrar al proveedor como subencargado.</item>
/// </list>
/// Es solo presentación: que el agente figure como disponible no autoriza nada. Cada
/// consulta y cada escritura pasan por la comprobación de instrucción de tratamiento
/// con IA y por los Commands de Application.
/// </summary>
public sealed class DisponibilidadAsistente(IOptions<AnthropicOptions> anthropic, IOptions<TypeSafeOptions> typeSafe)
{
    public bool PreguntasDisponibles => !string.IsNullOrWhiteSpace(anthropic.Value.ApiKey);

    public bool AgenteDisponible => typeSafe.Value.Activo && !string.IsNullOrWhiteSpace(typeSafe.Value.ApiKey);

    public bool Alguna => PreguntasDisponibles || AgenteDisponible;
}
