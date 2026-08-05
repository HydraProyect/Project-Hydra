using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Components;

/// <summary>
/// Traduce el orden que pide QuickGrid al par <c>OrdenarPor</c>/<c>Descendente</c>
/// que aceptan las Queries de listado.
///
/// Hasta la Fase 85 la ordenación era decorativa: las columnas llevaban
/// <c>Sortable="true"</c>, pero cada <c>ProveerElementosAsync</c> solo leía
/// <c>StartIndex</c> de la petición y la Query traía siempre el mismo
/// <c>OrderBy</c> fijo — hacer clic en una cabecera movía el indicador y no
/// reordenaba nada. Esto es lo que conecta ambos extremos.
///
/// El nombre que devuelve es el de la propiedad del DTO de lista (lo que
/// QuickGrid deduce de <c>PropertyColumn</c> o de <c>GridSort&lt;T&gt;.ByAscending</c>),
/// así que cada Query hace <c>switch</c> sobre nombres de sus propias
/// propiedades — sin tabla de traducción intermedia que mantener.
/// </summary>
public static class LecturaOrden
{
    public static (string? OrdenarPor, bool Descendente) Leer<T>(GridItemsProviderRequest<T> peticion)
    {
        // GetSortByProperties() ya aplica SortByAscending sobre la dirección
        // declarada en la columna, así que no hay que combinarlas a mano.
        var propiedad = peticion.GetSortByProperties().FirstOrDefault();

        return propiedad.PropertyName is null
            ? (null, false)
            : (propiedad.PropertyName, propiedad.Direction == SortDirection.Descending);
    }
}
