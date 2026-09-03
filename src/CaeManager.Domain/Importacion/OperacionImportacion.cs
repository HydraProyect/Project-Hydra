using CaeManager.Domain.Common;

namespace CaeManager.Domain.Importacion;

/// <summary>
/// Marca que una operación de importación (identificada por <see cref="OperacionId"/>,
/// generada al analizar el archivo y transportada en <c>PlanImportacionDto</c>) ya se
/// confirmó — REC-108, DEC-20.
///
/// La unicidad de <c>(TenantId, OperacionId)</c> (ver
/// OperacionImportacionConfiguration) es lo único que hace idempotente confirmar dos
/// veces la misma operación, incluida la carrera de dos confirmaciones concurrentes:
/// esta fila se agrega al mismo <c>SaveChangesAsync</c> que las inserciones del plan
/// (Empresa/Trabajador/Documento/Asignación), así que si dos confirmaciones de la
/// MISMA operación compiten, la segunda choca aquí y su transacción entera se
/// descarta — ninguna de sus filas llega a comprometerse. Por eso no hace falta una
/// clave persistida por entrada (hoja, fila, DNI, tipo, fecha): al fallar la
/// operación completa, cero entradas de la confirmación perdedora se escriben, así
/// que "como máximo una vez" se cumple trivialmente sin tener que perseguir cada
/// entrada por separado.
///
/// Deliberadamente NO es <c>(Trabajador, TipoDocumento)</c> — DEC-20 prohíbe esa
/// clave porque colapsaría la cardinalidad de <see cref="Documentos.Documento"/>
/// (varios documentos del mismo tipo por trabajador siguen permitidos).
/// </summary>
public class OperacionImportacion : EntidadConTenant
{
    public Guid OperacionId { get; private set; }
    public DateTime ConfirmadaEnUtc { get; private set; }

    private OperacionImportacion()
    {
    }

    public static OperacionImportacion Registrar(Guid operacionId) =>
        operacionId == Guid.Empty
            ? throw new ArgumentException("La identidad de la operación de importación no puede estar vacía.", nameof(operacionId))
            : new() { OperacionId = operacionId, ConfirmadaEnUtc = DateTime.UtcNow };
}
