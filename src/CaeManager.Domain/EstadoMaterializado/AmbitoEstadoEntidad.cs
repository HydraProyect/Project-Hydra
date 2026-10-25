namespace CaeManager.Domain.EstadoMaterializado;

/// <summary>
/// La entidad cuyo estado de contexto guarda una fila de <see cref="EstadoEntidad"/>, o que una entrada de
/// <see cref="RecalculoPendiente"/> pide recalcular. Una Subcontrata es una Empresa y su estado es el de su fila de
/// <see cref="Empresa"/>: solo su documentación de Empresa, con el mismo criterio que cualquier Empresa (decisión D-1,
/// 2026-10-24); sus Trabajadores bloqueados se cuentan aparte al leer y no se guardan. <see cref="Subcontrata"/> no lo
/// escribe nadie: se conserva para no mover los ordinales.
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
