namespace CaeManager.Web.Components.Layout;

/// <summary>
/// A dónde vuelve el selector de Tenant de la cabecera tras cambiar de Tenant beneficiario
/// (contrato del selector, § 4.4: «Cambiar desde una ficha lleva a la lista de esa entidad en el
/// Tenant nuevo, nunca a la misma ficha»).
/// <para>
/// Una ruta con un Id en el camino (<c>/trabajadores/{id}</c>, <c>/clientes/{id}/lectura-ia</c>,
/// <c>/documentos/tipos/{id}</c>, <c>/plantillas/{id}/editar</c>…) es una ficha del Tenant que se
/// deja: en el nuevo no existe, y volver a ella pintaba el estado vacío de «sin acceso». Se vuelve a
/// la lista del mismo árbol, que es el primer segmento del camino. Cualquier otra ruta se conserva tal
/// cual. La consulta nunca sobrevive: los filtros con Ids del Tenant anterior no valen en el nuevo (I14).
/// </para>
/// <para>
/// Solo decide la navegación tras un cambio de contexto pedido por el usuario. No es autoridad ni
/// toca la autorización: el POST a <c>/cuenta/cliente-activo</c> revalida el Tenant igual que antes,
/// y abrir una ficha de otro Tenant pegando su URL sigue pintando el mismo estado vacío (§ 4.5).
/// </para>
/// </summary>
public static class RutaTrasCambioDeTenant
{
    /// <summary>
    /// La ruta local a la que volver, sin consulta ni fragmento. <paramref name="camino"/> es el
    /// camino absoluto de la página actual (p. ej. <c>new Uri(NavigationManager.Uri).AbsolutePath</c>);
    /// si trae consulta o fragmento, se descartan.
    /// </summary>
    public static string Calcular(string? camino)
    {
        if (string.IsNullOrWhiteSpace(camino))
            return "/";

        var sinConsulta = camino.Split('?', '#')[0];
        var segmentos = sinConsulta.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (!segmentos.Any(s => Guid.TryParse(s, out _)))
            return sinConsulta.StartsWith('/') ? sinConsulta : "/" + sinConsulta;

        return Guid.TryParse(segmentos[0], out _) ? "/" : "/" + segmentos[0];
    }
}
