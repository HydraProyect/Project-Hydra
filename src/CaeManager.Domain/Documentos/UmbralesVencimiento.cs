namespace CaeManager.Domain.Documentos;

/// <summary>
/// Los umbrales efectivos con los que se clasifica un documento en «Por vencer» (<see cref="EstadoDocumento.Proximo"/> y
/// <see cref="EstadoDocumento.Urgente"/>). Punto único de la regla: todo lector que clasifique un documento por su
/// vencimiento obtiene aquí sus umbrales, nunca directamente de Parámetros del sistema.
///
/// <para>
/// <b>Regla (decisión del 2026-11-07, «Aviso "Por vencer" proporcional a la vigencia del tipo de documento»).</b> Un
/// TipoDocumento con vigencia de un mes (<see cref="TipoDocumento.VigenciaMeses"/> = 1: certificados de Seguridad Social,
/// RLC, ITA, RNT…) entra en «Por vencer» 7 días antes de vencer, no con el umbral ámbar del Tenant (30 días por defecto):
/// con 30 días entraba nada más subirse. El resto de tipos, y los documentos sin tipo conocido, siguen con los umbrales de
/// Parámetros del sistema.
/// </para>
///
/// <para>
/// Dos acotaciones que no amplían la decisión: el aviso corto nunca supera el umbral ámbar del Tenant (si el Tenant avisa
/// con menos de 7 días, manda el suyo, porque la regla existe para avisar menos, no más), y el umbral rojo no supera al
/// ámbar efectivo (<see cref="Configuracion.ParametroSistema"/> exige rojo &lt; ámbar; con el rojo del Tenant intacto, un
/// mensual entraría en «Por vencer» por el rojo a los 15 días y la regla no se cumpliría). Con los valores por defecto
/// (30/15) un mensual pasa de Vigente a <see cref="EstadoDocumento.Urgente"/> a 7 días, sin tramo Próximo.
/// </para>
/// </summary>
public readonly record struct UmbralesVencimiento(int AmbarDias, int RojoDias)
{
    /// <summary>La vigencia, en meses, de los tipos que avisan con <see cref="AvisoVigenciaMensualDias"/>.</summary>
    public const int VigenciaMensualMeses = 1;

    /// <summary>Días antes del vencimiento en que un tipo mensual entra en «Por vencer».</summary>
    public const int AvisoVigenciaMensualDias = 7;

    /// <summary>
    /// Umbrales efectivos para un documento cuyo TipoDocumento tiene <paramref name="vigenciaMesesTipo"/> (<c>null</c> si
    /// el tipo no fija vigencia o no se conoce), con los umbrales del Tenant.
    /// </summary>
    public static UmbralesVencimiento Para(int? vigenciaMesesTipo, int umbralAmbarDias, int umbralRojoDias)
    {
        if (!EsVigenciaMensual(vigenciaMesesTipo))
            return new(umbralAmbarDias, umbralRojoDias);

        var ambar = Math.Min(umbralAmbarDias, AvisoVigenciaMensualDias);
        return new(ambar, Math.Min(umbralRojoDias, ambar));
    }

    /// <summary>Si un TipoDocumento con esta vigencia avisa con el umbral corto.</summary>
    public static bool EsVigenciaMensual(int? vigenciaMesesTipo) => vigenciaMesesTipo == VigenciaMensualMeses;
}

/// <summary>
/// Los umbrales efectivos de un Tenant para cada uno de sus TiposDocumento: los de Parámetros del sistema más la vigencia
/// de cada tipo, aplicados con <see cref="UmbralesVencimiento.Para"/>. Lo construye una vez cada lector (en Application,
/// <c>UmbralesVencimientoDelTenant</c>) y lo consulta por <c>TipoDocumentoId</c> para cada documento.
/// </summary>
public sealed class UmbralesVencimientoPorTipo
{
    private readonly IReadOnlyDictionary<Guid, int?> vigenciaMesesPorTipo;

    public UmbralesVencimientoPorTipo(int umbralAmbarDias, int umbralRojoDias, IReadOnlyDictionary<Guid, int?> vigenciaMesesPorTipo)
    {
        UmbralAmbarDias = umbralAmbarDias;
        UmbralRojoDias = umbralRojoDias;
        this.vigenciaMesesPorTipo = vigenciaMesesPorTipo;
        TiposConAvisoMensual = [.. vigenciaMesesPorTipo.Where(t => UmbralesVencimiento.EsVigenciaMensual(t.Value)).Select(t => t.Key)];
        Mensual = UmbralesVencimiento.Para(UmbralesVencimiento.VigenciaMensualMeses, umbralAmbarDias, umbralRojoDias);
    }

    /// <summary>Umbral ámbar de Parámetros del sistema: la cota superior de cualquier «Por vencer» (prefiltros en SQL).</summary>
    public int UmbralAmbarDias { get; }

    /// <summary>Umbral rojo de Parámetros del sistema.</summary>
    public int UmbralRojoDias { get; }

    /// <summary>Los umbrales de un tipo que no avisa con el umbral corto.</summary>
    public UmbralesVencimiento General => new(UmbralAmbarDias, UmbralRojoDias);

    /// <summary>Los umbrales de un tipo mensual (<see cref="TiposConAvisoMensual"/>).</summary>
    public UmbralesVencimiento Mensual { get; }

    /// <summary>
    /// Los tipos que avisan con el umbral corto: para las consultas que tienen que clasificar en SQL, donde la regla se
    /// expresa como «el tipo está en esta lista ? límite mensual : límite general».
    /// </summary>
    public IReadOnlyList<Guid> TiposConAvisoMensual { get; }

    /// <summary>Umbrales efectivos de un documento de <paramref name="tipoDocumentoId"/>; los generales si el tipo no se conoce.</summary>
    public UmbralesVencimiento Para(Guid tipoDocumentoId) =>
        UmbralesVencimiento.Para(vigenciaMesesPorTipo.GetValueOrDefault(tipoDocumentoId), UmbralAmbarDias, UmbralRojoDias);
}
