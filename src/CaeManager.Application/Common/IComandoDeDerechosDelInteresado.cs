namespace CaeManager.Application.Common;

/// <summary>
/// Comando con el que el Tenant propietario registra o tramita una solicitud de un derecho del interesado (#2002,
/// decisión D-3). Recibe la misma exención que <see cref="IComandoDePortabilidadDelTenantPropietario"/>:
/// <see cref="GateComercialTenantBehavior{TRequest,TResponse}"/> no lo bloquea por el estado comercial (SoloLectura o
/// Suspendida por impago), porque el plazo del RGPD para responder al interesado no se detiene porque el Tenant deje
/// de pagar (decisión de Chris del 2026-11-17, opción (b)). La suspensión operativa (<c>EstadoTenant.Suspendido</c>)
/// y la del Tenant de origen del Actor real sí lo bloquean, y la autorización por rol, la capacidad «Derechos del
/// interesado» y las comprobaciones del propio handler se aplican igual.
/// </summary>
public interface IComandoDeDerechosDelInteresado;
