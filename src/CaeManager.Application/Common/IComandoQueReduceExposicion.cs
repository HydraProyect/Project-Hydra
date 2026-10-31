namespace CaeManager.Application.Common;

/// <summary>
/// Comando que solo retira acceso a los datos del Tenant propietario.
/// <see cref="GateComercialTenantBehavior{TRequest,TResponse}"/> no lo bloquea: un impago no puede
/// dejar a un Tenant sin forma de cortar una credencial filtrada. La autorización por rol y la del
/// propio handler se aplican igual.
/// </summary>
public interface IComandoQueReduceExposicion;
