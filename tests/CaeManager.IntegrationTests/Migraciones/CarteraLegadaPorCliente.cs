using CaeManager.Domain.Operaciones;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// Fabrica el <b>estado heredado</b> que la migración <c>ConvierteCarterasPorClienteATenantEntero</c> tuvo que
/// convertir: una Asignación de Cartera repartida por Cliente empresarial. Desde D-7 (incremento 3) el dominio
/// no deja crearla (<see cref="AsignacionCartera"/> rechaza un ámbito por Cliente empresarial) y la base de
/// datos tampoco (<c>CK_AsignacionesCartera_TenantEnteroSalvoCerrada</c>), así que solo se puede escribir con
/// el esquema ANTERIOR a esa restricción y saltándose la guarda de dominio: eso es exactamente lo que estas
/// pruebas necesitan para ejercitar una migración de datos sobre el modo retirado.
///
/// <para>
/// <b>Solo para las pruebas de la migración de datos y del CHECK</b> (carpeta <c>Migraciones</c>), sobre un
/// esquema intermedio o para provocar el rechazo de la base. Cualquier otra prueba que
/// necesite acotar el alcance a un Cliente empresarial acota la Asignación de Operación
/// (<c>AsignacionOperacion.Interna/Externa</c> con <c>AmbitoAsignacion.DeRelacionCliente</c>) y cuelga de
/// ella una cartera universal. Lo vigila <c>RepartoDeCarteraPorClienteRetiradoTests</c>.
/// </para>
/// </summary>
internal static class CarteraLegadaPorCliente
{
    public static AsignacionCartera Interna(
        AsignacionOperacion operacion, Guid usuarioId, Guid clienteId, DateTime desde, DateTime? hasta, DateTime ahora) =>
        Repartir(AsignacionCartera.Interna(operacion, usuarioId, AmbitoAsignacion.Universal, desde, hasta, ahora), clienteId);

    public static AsignacionCartera Externa(
        AsignacionOperacion operacion, Guid usuarioId, string rol, Guid clienteId, DateTime desde, DateTime? hasta, DateTime ahora) =>
        Repartir(AsignacionCartera.Externa(operacion, usuarioId, rol, AmbitoAsignacion.Universal, desde, hasta, ahora), clienteId);

    private static AsignacionCartera Repartir(AsignacionCartera cartera, Guid clienteId)
    {
        typeof(AsignacionResponsabilidad)
            .GetProperty(nameof(AsignacionResponsabilidad.AmbitoRelacionClienteId))!
            .SetValue(cartera, clienteId);
        return cartera;
    }
}
