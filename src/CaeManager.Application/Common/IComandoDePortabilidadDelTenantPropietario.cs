namespace CaeManager.Application.Common;

/// <summary>
/// Comando con el que el Tenant propietario saca sus propios datos (exportación completa).
/// <see cref="GateComercialTenantBehavior{TRequest,TResponse}"/> no lo bloquea por el estado comercial
/// (SoloLectura o Suspendida por impago): sacar los datos propios no depende del estado de pago
/// (decisión de Chris del 2026-11-16). La suspensión operativa (<c>EstadoTenant.Suspendido</c>) sí lo
/// bloquea, porque también se usa ante un compromiso y una exportación completa sería exfiltración.
/// La autorización por rol y la del propio handler se aplican igual.
/// </summary>
public interface IComandoDePortabilidadDelTenantPropietario;
