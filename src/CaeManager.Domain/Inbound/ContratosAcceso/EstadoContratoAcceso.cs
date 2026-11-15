namespace CaeManager.Domain.Inbound.ContratosAcceso;

/// <summary>
/// Estado del contrato de acceso (duda 3 del plan de pantallas de Inbound, DECISION_LOG 2026-11-14): seis estados.
/// «Prorrogado» no es un estado: ampliar la vigencia es <see cref="ContratoAcceso.Renovar"/>, con historial.
///
/// <para>
/// Transiciones (las aplica <see cref="ContratoAcceso"/>):
/// <list type="bullet">
/// <item><see cref="PendienteDeConfirmar"/> → <see cref="Activo"/> (el Tenant TEC confirma) o → <see cref="Rechazado"/> (el Tenant TEC rechaza).</item>
/// <item><see cref="Activo"/> ⇄ <see cref="Suspendido"/> (Suspender y Reanudar).</item>
/// <item><see cref="Activo"/> o <see cref="Suspendido"/> → <see cref="Finalizado"/> (Finalizar).</item>
/// <item><see cref="PendienteDeConfirmar"/> o <see cref="Activo"/> → <see cref="Cancelado"/>: la baja antes de empezar y sin actividad (D-21).</item>
/// </list>
/// <see cref="Finalizado"/>, <see cref="Cancelado"/> y <see cref="Rechazado"/> son finales: un contrato Finalizado no se
/// reabre (D-21 retira <c>Reabrir</c>).
/// </para>
///
/// <para>
/// Se guarda como texto. Los valores no se renombran: la columna <c>Estado</c> de <c>ContratosAcceso</c> y la auxiliar
/// <c>app_inbound_tenant_contratista_del_contrato</c> los comparan por su nombre.
/// </para>
/// </summary>
public enum EstadoContratoAcceso
{
    PendienteDeConfirmar,
    Activo,
    Suspendido,
    Finalizado,
    Cancelado,
    Rechazado
}
