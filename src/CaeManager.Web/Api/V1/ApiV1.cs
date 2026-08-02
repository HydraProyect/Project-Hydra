namespace CaeManager.Web.Api.V1;

/// <summary>
/// Utilidades compartidas por los endpoints de <c>/api/v1</c>.
/// </summary>
public static class ApiV1
{
    /// <summary>
    /// Límite duro de página para la API pública, más bajo que el que se
    /// permite desde la UI interna (<c>int.MaxValue</c> en exportaciones):
    /// un tercero no debería poder pedir el catálogo entero en una sola
    /// llamada sin paginar.
    /// </summary>
    public const int TamanoPaginaMaximo = 100;

    public static int TamanoPagina(int solicitado) =>
        solicitado <= 0 ? 20 : Math.Min(solicitado, TamanoPaginaMaximo);

    public static int Pagina(int solicitada) => solicitada < 1 ? 1 : solicitada;
}
