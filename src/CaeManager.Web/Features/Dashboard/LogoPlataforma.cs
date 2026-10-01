namespace CaeManager.Web.Features.Dashboard;

/// <summary>
/// Logotipo de una plataforma CAE a partir del <c>Codigo</c> del catálogo.
///
/// <para>
/// Se mapea por el <b>slug</b>, no por el nombre: el <c>Codigo</c> es el
/// identificador estable que el propio dominio documenta como «nunca mostrado
/// al usuario ni editable desde la UI», mientras que el <c>Nombre</c> se puede
/// editar, lleva acentos y no sirve de clave.
/// </para>
///
/// <para>
/// <b>El catálogo tiene 23 proveedores y aquí hay 19 marcas.</b> Las cuatro
/// restantes (Valora, Arch, Opground y Norprevención) no tienen una marca
/// oficial localizable —dominio redirigido a otra marca, en venta o inactivo,
/// o logotipo solo del grupo matriz—, así que devolver
/// <see langword="null"/> sigue siendo una respuesta de primera clase: la fila
/// se pinta igual, solo con el nombre. Añadir una marca es añadir una entrada y
/// su fichero; no hay nada más que tocar. La procedencia de cada fichero está
/// registrada fuera de este repositorio.
/// </para>
///
/// <para>
/// <b>Ni un logotipo por Grupo.</b> <c>ProveedorPlataformaCae.Grupo</c> agrupa
/// «Twind (CTAIMA Group)» con Twind, CTAIMACAE legacy y e-coordina, pero su
/// documentación es explícita: el grupo es <i>solo para analítica, nunca lógica
/// operativa</i>, porque son proveedores separados aunque compartan marca
/// comercial. Así que Twind no hereda el logotipo de CTAIMA: cada slug lleva el
/// suyo o ninguno.
/// </para>
/// </summary>
public static class LogoPlataforma
{
    private static readonly Dictionary<string, string> PorCodigo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nalanda"] = "nalanda.jpg",
        ["dokify"] = "dokify.jpg",
        ["e-coordina"] = "ecoordina.png",
        ["ctaimacae-legacy"] = "ctaima.png",
        ["twind"] = "twind.png",
        ["metacontratas"] = "metacontratas.png",
        ["coordinaplus"] = "coordinaplus.png",
        ["ucae"] = "ucae.png",
        ["validate"] = "validate.png",
        ["egestiona"] = "egestiona.png",
        ["smartosh"] = "smartosh.png",
        ["ecogestor"] = "ecogestor.png",
        ["sabentis"] = "sabentis.png",
        ["unifikas"] = "unifikas.png",
        ["quiron-prevencion"] = "quironprevencion.png",
        ["previntegral"] = "previntegral.png",
        ["ergasia"] = "ergasia.png",
        ["playcae"] = "playcae.png",
        ["docuprl"] = "docuprl.png",
    };

    /// <summary>
    /// Ruta del logotipo, o <see langword="null"/> si esta plataforma no tiene
    /// marca en el repositorio — que es lo habitual.
    /// </summary>
    public static string? Ruta(string? codigo) =>
        !string.IsNullOrWhiteSpace(codigo) && PorCodigo.TryGetValue(codigo, out var fichero)
            ? $"/img/plataformas/{fichero}"
            : null;

    /// <summary>Los slugs con logotipo, para que un test pueda comprobarlos contra la semilla del catálogo.</summary>
    public static IReadOnlyCollection<string> CodigosConLogo => PorCodigo.Keys;
}
