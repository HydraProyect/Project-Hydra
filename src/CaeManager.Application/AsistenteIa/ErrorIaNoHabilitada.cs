using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Common;

namespace CaeManager.Application.AsistenteIa;

/// <summary>
/// La negativa del asistente cuando la IA no está habilitada para el Tenant de la pantalla. El código es el mismo en
/// los dos casos; el texto distingue la causa (#2010, D-7): sin instrucción de tratamiento con IA vigente, o con ella
/// vigente pero con la IA desactivada por la objeción del Tenant a un subencargado. Solo se consulta después de que
/// <see cref="IInstruccionTratamientoIaService.EstaHabilitadaAsync"/> haya dicho que no: nunca decide nada.
/// </summary>
internal static class ErrorIaNoHabilitada
{
    public const string Codigo = "AsistenteIa.SinInstruccion";

    public const string SinInstruccion =
        "Esta organización todavía no tiene una instrucción de tratamiento con IA vigente — el asistente no puede procesar tu mensaje.";

    public const string DesactivadaPorObjecion =
        "Esta organización ha desactivado la IA al objetar a un subencargado — el asistente no puede procesar tu mensaje.";

    public static async Task<Error> CrearAsync(
        IInstruccionTratamientoIaService instruccionTratamientoIa, Guid tenantId, CancellationToken cancellationToken) =>
        Error.Crear(Codigo, await instruccionTratamientoIa.TieneInstruccionVigenteAsync(tenantId, cancellationToken)
            ? DesactivadaPorObjecion
            : SinInstruccion);
}
