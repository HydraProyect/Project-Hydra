using System.Linq.Expressions;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Qué documentos están en uso y cuáles son historial. Un <see cref="Documento"/> es <b>operativo</b> si no
/// está eliminado y nadie lo ha sustituido (<see cref="Documento.SustituirPor"/>); el sustituido se conserva
/// —nunca se borra— pero deja de contar para cumplimiento, alertas, bloqueo y porcentajes, y no vuelve a ser
/// operativo (decisiones D5 y D8 del 2026-10-03).
///
/// <para>
/// Punto único de esa condición. EF no puede llamar a un método de dominio dentro de una consulta, así que se
/// expone como expresión con nombre: las superficies que recorren documentos sin un requisito delante (Alertas,
/// lista de Trabajadores, recuentos) la aplican con <c>.Where(DocumentoOperativo.Expresion)</c> en vez de
/// escribir <c>SustituidoEnUtc == null</c> a mano; <c>ReglasDeNegocioSinCopiasTests</c> vigila que la
/// comparación no reaparezca fuera de aquí.
/// </para>
///
/// <para>
/// Incluye <c>!EstaEliminado</c> aunque el filtro global de <c>CaeManagerDbContext</c> ya excluya los
/// eliminados: una consulta con <c>IgnoreQueryFilters</c> (que el filtro del Tenant también apaga) no debe
/// contar un eliminado como operativo por haberse olvidado de esa mitad.
/// </para>
/// </summary>
public static class DocumentoOperativo
{
    public static readonly Expression<Func<Documento, bool>> Expresion =
        d => !d.EstaEliminado && d.SustituidoEnUtc == null;

    private static readonly Func<Documento, bool> Compilada = Expresion.Compile();

    /// <summary>La misma condición evaluada en memoria sobre un documento ya cargado.</summary>
    public static bool Es(Documento documento)
    {
        ArgumentNullException.ThrowIfNull(documento);
        return Compilada(documento);
    }

    /// <summary>
    /// Solo los documentos operativos de una consulta: <c>documentosContext.Documentos.Operativos()</c>. Es
    /// <c>Where(Expresion)</c> con nombre, para que la fuente de documentos de un lector diga a primera vista
    /// que el historial no cuenta. Todo lector que decide estado, alertas, bloqueo, faltantes o cifras la usa;
    /// <c>ReglasDeNegocioSinCopiasTests</c> inventaría los lectores de <c>.Documentos</c> y exige esto o una
    /// excepción declarada con su motivo.
    /// </summary>
    public static IQueryable<Documento> Operativos(this IQueryable<Documento> documentos) =>
        documentos.Where(Expresion);
}
