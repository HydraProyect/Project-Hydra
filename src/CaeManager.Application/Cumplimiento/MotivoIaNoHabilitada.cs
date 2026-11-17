namespace CaeManager.Application.Cumplimiento;

/// <summary>
/// Por qué la IA no está habilitada, para el registro de un consumidor de IA que corre sin pantalla (#2010, D-7;
/// revisión Codex P3 de la PR #2274). Mismo criterio que la negativa del asistente (<c>ErrorIaNoHabilitada</c>):
/// distingue «sin instrucción vigente» de «instrucción vigente con la IA desactivada por la objeción del Tenant
/// propietario a un subencargado», con <see cref="IInstruccionTratamientoIaService.TieneInstruccionVigenteAsync"/>.
/// Solo se consulta después de que <see cref="IInstruccionTratamientoIaService.EstaHabilitadaAsync"/> haya dicho que
/// no: nunca decide si se llama al proveedor.
/// </summary>
public static class MotivoIaNoHabilitada
{
    public const string SinTenant = "no hay Tenant propietario resuelto (Nivel 0)";

    public const string SinInstruccion = "el Tenant propietario no tiene instrucción de tratamiento IA vigente (Nivel 0)";

    public const string DesactivadaPorObjecion =
        "el Tenant propietario tiene instrucción de tratamiento IA vigente, pero desactivó la IA al objetar a un subencargado (Nivel 0)";

    /// <summary>Sin Tenant resuelto no se pregunta nada: no hay a quién.</summary>
    public static async Task<string> DescribirAsync(
        IInstruccionTratamientoIaService instruccionTratamientoIa, Guid? tenantId, CancellationToken cancellationToken) =>
        tenantId is not { } id
            ? SinTenant
            : await instruccionTratamientoIa.TieneInstruccionVigenteAsync(id, cancellationToken)
                ? DesactivadaPorObjecion
                : SinInstruccion;
}
