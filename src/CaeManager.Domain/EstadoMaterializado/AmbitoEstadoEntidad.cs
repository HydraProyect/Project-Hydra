namespace CaeManager.Domain.EstadoMaterializado;

/// <summary>
/// La entidad cuyo estado de contexto guarda una fila de <see cref="EstadoEntidad"/>, o que una entrada de
/// <see cref="RecalculoPendiente"/> pide recalcular. Una Subcontrata es una Empresa, pero su estado se guarda en fila
/// propia porque se resume con sus Trabajadores, no con su documentación de Empresa.
///
/// <para>
/// <b>Valores numéricos explícitos y congelados</b>: se persisten como <c>smallint</c> en las tablas del estado
/// materializado, así que insertar un valor en medio cambiaría en silencio el significado de las filas guardadas
/// (<c>OrdinalesDeEnumsPublicadosTests</c>).
/// </para>
/// </summary>
public enum AmbitoEstadoEntidad
{
    Centro = 0,
    Trabajador = 1,
    Empresa = 2,
    Subcontrata = 3
}
