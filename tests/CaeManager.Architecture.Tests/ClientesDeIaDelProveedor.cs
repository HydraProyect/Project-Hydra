using CaeManager.Infrastructure.AsistenteIa;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Lista única de los clientes que hablan con el proveedor de IA de pago (Anthropic). La
/// comparten los ratchets que necesitan saberlo (resiliencia HTTP propia y conmutador del
/// Tenant propietario) para que un cliente nuevo se declare en UN solo sitio y todos los
/// ratchets lo vean a la vez.
/// </summary>
internal static class ClientesDeIaDelProveedor
{
    public static readonly Type[] Tipos =
    [
        typeof(AnthropicAsistenteIaService),
        typeof(AnthropicDecisionesCerradasService),
        typeof(AnthropicDeteccionGestionCorreoService),
        typeof(AnthropicDeteccionIntencionCaeService),
        typeof(AnthropicDeteccionRelevanciaCaeService),
        typeof(AnthropicDeteccionVisitaCorreoService),
        typeof(AnthropicDocumentAIProvider),
        typeof(AnthropicExtraccionTrabajadoresIaService),
        typeof(AnthropicRedaccionRespuestaIaService),
        typeof(AnthropicSegundaOpinionCriticaIa),
    ];

    public static readonly string[] Nombres = Tipos.Select(t => t.Name).ToArray();
}
