namespace CaeManager.Domain.Importacion;

public interface IOperacionImportacionRepository
{
    /// <summary>
    /// Comprobación previa, no autoritativa: solo evita el trabajo de reconstruir
    /// el plan entero cuando la operación ya se confirmó antes (reintento
    /// secuencial, doble clic). La garantía real la da el índice único de
    /// <c>(TenantId, OperacionId)</c> — ver <see cref="GuardarSiOperacionNuevaAsync"/> —
    /// no esta lectura.
    /// </summary>
    Task<bool> ExisteAsync(Guid operacionId, CancellationToken cancellationToken = default);

    void Agregar(OperacionImportacion operacion);

    /// <summary>
    /// Persiste TODO lo pendiente en el contexto (la fila de esta operación y,
    /// junto a ella, en el mismo <c>SaveChangesAsync</c>, cualquier entidad que
    /// otros repositorios de la misma unidad de trabajo hayan agregado) y
    /// traduce la violación del índice único de <c>(TenantId, OperacionId)</c>
    /// en <c>false</c> en vez de dejar escapar la excepción de Postgres — esa
    /// violación es la carrera real de dos confirmaciones concurrentes de la
    /// MISMA operación (REC-108, DEC-20): la transacción entera de la
    /// perdedora se descarta, así que <c>false</c> significa "no se escribió
    /// nada, ni de esta operación ni de ninguna otra entidad del plan".
    /// Cualquier otro fallo de guardado se propaga sin tocar.
    /// </summary>
    Task<bool> GuardarSiOperacionNuevaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Descarta TODO lo que el contexto tuviera pendiente de guardar. Blazor
    /// Server comparte un único <c>DbContext</c> por circuito (ver
    /// Importacion.razor.cs / PuertaAccesoDatos), así que un fallo a mitad de
    /// esta operación (una excepción antes del guardado final, o el intento
    /// perdedor de la carrera de <see cref="GuardarSiOperacionNuevaAsync"/>)
    /// deja entidades en estado <c>Added</c> que un guardado NO relacionado
    /// posterior sobre el MISMO contexto —el que registra el historial de la
    /// importación, justo después, en el mismo circuito— intentaría
    /// persistir también: escritura parcial filtrada como si fuera del
    /// historial, o el mismo choque de unicidad repitiéndose sin que nadie lo
    /// capture. Llamar aquí, en el punto exacto donde se descubre que esta
    /// operación no va a completarse, evita las dos cosas.
    /// </summary>
    void DescartarPendientes();
}
