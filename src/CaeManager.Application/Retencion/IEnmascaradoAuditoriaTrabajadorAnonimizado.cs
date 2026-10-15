namespace CaeManager.Application.Retencion;

/// <summary>
/// Enmascara los datos personales de un Trabajador ya anonimizado en su historial
/// de auditoría (decisión de Chris, 2026-10-15). El historial es de solo inserción
/// para la aplicación; la única reescritura posible es la función
/// <c>app_enmascarar_auditoria_de_trabajador_anonimizado</c>, que solo toca las
/// entradas de ese Trabajador en el Tenant de la sesión y solo si ya está
/// anonimizado, y solo sustituye por "***" los valores de
/// <see cref="Domain.Trabajadores.Trabajador.CamposDeDatosPersonales"/>.
///
/// Se llama dentro de la misma transacción que guarda la anonimización
/// (<see cref="EjecucionPurgaService"/>): si el enmascarado falla, la
/// anonimización tampoco queda escrita.
/// </summary>
public interface IEnmascaradoAuditoriaTrabajadorAnonimizado
{
    /// <summary>Devuelve cuántas entradas de auditoría se reescribieron.</summary>
    Task<int> EnmascararAsync(Guid trabajadorId, CancellationToken cancellationToken = default);
}
