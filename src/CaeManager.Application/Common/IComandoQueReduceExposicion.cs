namespace CaeManager.Application.Common;

/// <summary>
/// Comando que solo retira acceso a los datos del Tenant propietario.
/// <see cref="GateComercialTenantBehavior{TRequest,TResponse}"/> no lo bloquea: ni un impago ni una
/// suspensión pueden dejar sin forma de cortar un acceso comprometido. La autorización por rol y la del
/// propio handler se aplican igual.
/// </summary>
public interface IComandoQueReduceExposicion;
