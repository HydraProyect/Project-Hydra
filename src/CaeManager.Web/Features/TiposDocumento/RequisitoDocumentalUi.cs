using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.TiposDocumento;

/// <summary>
/// Traduce los dos ejes del catálogo —¿se pide? y ¿con qué autoridad?— a lo
/// que ve el usuario.
///
/// <para>
/// La regla que gobierna este fichero: <b>el rótulo dice la naturaleza, no
/// «Obligatorio» a secas</b>. Un badge que pone «Obligatorio» sobre el seguro
/// de responsabilidad civil afirma una ley que no existe en España; uno que
/// pone «Requisito de cliente» dice la verdad y sigue dejando claro que hay
/// que aportarlo. Es la diferencia entre una herramienta que orienta y una
/// que asusta con datos falsos.
/// </para>
///
/// <para>
/// Las ramas por defecto no degradan a favorable, mismo criterio que
/// <c>EstadoDocumentoUi</c>: un valor sin traducir sale como desconocido, no
/// como benigno.
/// </para>
/// </summary>
public static class RequisitoDocumentalUi
{
    public static string Texto(RequisitoDocumental requerido, NaturalezaJuridica naturaleza) => requerido switch
    {
        RequisitoDocumental.No => "No se pide",
        RequisitoDocumental.Condicional => "Si aplica",
        RequisitoDocumental.Si => TextoNaturaleza(naturaleza),
        _ => "Requisito desconocido"
    };

    public static string TextoNaturaleza(NaturalezaJuridica naturaleza) => naturaleza switch
    {
        NaturalezaJuridica.ObligacionLegal => "Obligación legal",
        NaturalezaJuridica.ObligacionCondicionada => "Obligación condicionada",
        NaturalezaJuridica.PracticaSector => "Práctica del sector",
        NaturalezaJuridica.RequisitoCliente => "Requisito de cliente",
        NaturalezaJuridica.Recomendacion => "Recomendado por TALVEG",
        _ => "Naturaleza desconocida"
    };

    /// <summary>
    /// El tono sube con la fuerza de la exigencia, no con la gravedad: nada
    /// de esto es una alarma — es una explicación de por qué se pide.
    /// </summary>
    public static TonoBadge Tono(NaturalezaJuridica naturaleza) => naturaleza switch
    {
        NaturalezaJuridica.ObligacionLegal => TonoBadge.Peligro,
        NaturalezaJuridica.ObligacionCondicionada => TonoBadge.Advertencia,
        NaturalezaJuridica.PracticaSector => TonoBadge.Advertencia,
        NaturalezaJuridica.RequisitoCliente => TonoBadge.Info,
        NaturalezaJuridica.Recomendacion => TonoBadge.Neutro,
        _ => TonoBadge.Peligro
    };
}
