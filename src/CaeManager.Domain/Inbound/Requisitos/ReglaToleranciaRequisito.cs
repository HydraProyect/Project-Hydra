using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.Inbound.Requisitos;

/// <summary>
/// R-6 (revisión por seis papeles, 2026-11-14): la tolerancia tras el vencimiento es configurable por tipo de documento,
/// pero <b>nunca</b> en aptitud médica, formación obligatoria ni seguro.
///
/// <para>
/// Se decide por el <see cref="TipoDocumento.Grupo"/> del tipo que acredita el requisito: Vigilancia de la salud, Formación
/// PRL y Seguros y mutua no la admiten, y tampoco un tipo de categoría especial de salud aunque esté en otro grupo. Un tipo
/// <b>sin grupo</b> tampoco la admite: no se sabe si es uno de los tres, y equivocarse hacia «sin tolerancia» solo obliga a
/// renovar a tiempo, mientras que equivocarse hacia «con tolerancia» deja entrar con un seguro o una aptitud vencidos.
/// </para>
///
/// <para>
/// El grupo de un tipo puede cambiar después de fijar la tolerancia. Por eso la resolución por capas (B-15) no debe leer
/// <see cref="RequisitoPlantilla.ToleranciaDias"/> a secas sino <see cref="RequisitoPlantilla.ToleranciaEfectiva"/>, que
/// vuelve a aplicar esta regla sobre el tipo de ese momento.
/// </para>
/// </summary>
public static class ReglaToleranciaRequisito
{
    /// <summary>Grupos de tipo de documento que nunca admiten tolerancia (R-6).</summary>
    public static readonly IReadOnlySet<GrupoTipoDocumento> GruposSinTolerancia = new HashSet<GrupoTipoDocumento>
    {
        GrupoTipoDocumento.VigilanciaDeLaSalud,
        GrupoTipoDocumento.FormacionPrl,
        GrupoTipoDocumento.SegurosYMutua,
    };

    public static bool AdmiteTolerancia(TipoDocumento tipo)
    {
        ArgumentNullException.ThrowIfNull(tipo);
        return tipo.Grupo is { } grupo && !GruposSinTolerancia.Contains(grupo) && !tipo.RevelaSalud;
    }
}
