using ClosedXML.Excel;

namespace CaeManager.Infrastructure.Importacion;

internal enum EstadoCeldaFecha { Vacia, Ilegible, Valida }

/// <summary>
/// DCR-12 B exige distinguir la celda vacía (legítimo: no hay dato de ese
/// tipo) de la celda con un valor que no se pudo interpretar como fecha
/// (dato perdido: hay que registrarlo con el valor bruto que traía).
/// </summary>
internal readonly record struct ResultadoFechaCelda(EstadoCeldaFecha Estado, DateOnly? Fecha, string? ValorBruto);

/// <summary>
/// Lectura de celda de fecha común a los tres analizadores de importación
/// que leen fechas (REC-129) — <see cref="ClosedXmlPlantillaClientesService"/>
/// no tiene ninguna celda de fecha y no usa este ayudante. Antes de
/// extraerlo, <see cref="ClosedXmlImportacionParser"/> ya distinguía celda
/// vacía de celda ilegible para la fecha de cada documento, pero no para la
/// fecha de nacimiento (REC-128); <see cref="ClosedXmlPlantillaDocumentosService"/>
/// y <see cref="ClosedXmlPlantillaCombinadaService"/> colapsaban ambos
/// estados en <c>null</c> en todas sus fechas, perdiendo en silencio
/// cualquier valor presente pero no interpretable. Extraer la detección de
/// estado a un solo sitio no decide qué hace cada camino con el resultado —
/// eso sigue siendo decisión de cada uno, por su propia causal (DCR-12 B
/// prohíbe uniformizar los motivos, no compartir esta lectura).
/// </summary>
internal static class FechaCeldaAyudante
{
    public static ResultadoFechaCelda Leer(IXLCell celda)
    {
        if (celda.IsEmpty()) return new ResultadoFechaCelda(EstadoCeldaFecha.Vacia, null, null);
        if (celda.TryGetValue<DateTime>(out var fecha)) return new ResultadoFechaCelda(EstadoCeldaFecha.Valida, DateOnly.FromDateTime(fecha), null);
        return new ResultadoFechaCelda(EstadoCeldaFecha.Ilegible, null, celda.GetString().Trim());
    }
}
