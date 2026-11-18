using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Common;

/// <summary>
/// Qué estado del Tenant actual bloquea sus escrituras, y con qué error: la regla de
/// <see cref="GateComercialTenantBehavior{TRequest,TResponse}"/> para el propio Tenant, en un único sitio para que
/// quien necesite saber de antemano si un Command se va a rechazar lo pregunte aquí y no la duplique.
/// <list type="bullet">
/// <item>la suspensión operativa (<see cref="EstadoTenant.Suspendido"/>, decisión D-3), por delante del estado comercial;</item>
/// <item><see cref="EstadoComercialTenant.Suspendida"/> y <see cref="EstadoComercialTenant.SoloLectura"/>;</item>
/// <item><see cref="EstadoComercialTenant.SinSuscripcion"/>, <see cref="EstadoComercialTenant.Activa"/> y
/// <see cref="EstadoComercialTenant.AvisoImpago"/> no bloquean: el aviso es una etapa de advertencia.</item>
/// </list>
/// No cubre lo que el behavior decide fuera del propio Tenant: la exención del Tenant de plataforma, el Tenant actual
/// sin resolver y la suspensión del Tenant de origen del Actor real cuando no es el Tenant actual.
/// Tampoco la exención por impago: un <see cref="IComandoDePortabilidadDelTenantPropietario"/> o un
/// <see cref="IComandoDeDerechosDelInteresado"/> pasa el estado comercial bloqueante y solo lo detiene la suspensión
/// operativa.
/// </summary>
public static class BloqueoEscrituraPorEstadoTenant
{
    public static Error? ErrorDe(EstadoTenant estado, EstadoComercialTenant estadoComercial)
    {
        if (estado is EstadoTenant.Suspendido)
            return Error.Crear("Tenant.Suspendido", "Esta organización está suspendida. Contacta con nosotros.");

        return estadoComercial switch
        {
            EstadoComercialTenant.Suspendida => Error.Crear(
                "Comercial.Suspendido",
                "Esta cuenta está suspendida por impago. Contacta con nosotros para reactivarla."),
            EstadoComercialTenant.SoloLectura => Error.Crear(
                "Comercial.SoloLectura",
                "Esta cuenta está en modo de solo lectura por un impago pendiente. Contacta con nosotros para regularizarlo."),
            _ => null,
        };
    }
}
