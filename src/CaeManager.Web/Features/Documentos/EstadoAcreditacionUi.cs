using CaeManager.Domain.Documentos;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Traduce <see cref="EstadoAcreditacion"/> a texto — mismo espíritu que
/// <see cref="EstadoDocumentoUi"/>/<see cref="CausaRechazoAcreditacionUi"/>:
/// un solo sitio de traducción.
///
/// <para>
/// <b>Existe porque no lo había, y se notaba.</b> El mapeo vivía dentro de
/// <c>BadgesAcreditacion.razor</c> y <c>PlataformaTab.razor</c> lo repetía a
/// mano para dos de los cinco valores. Dos copias del mismo mapeo son dos
/// vocabularios en cuanto una de ellas cambie — que es exactamente lo que el
/// inventario de vocabulario encontró: el mismo valor rotulado de varias
/// formas según la pantalla.
/// </para>
///
/// <para>
/// <b>Género femenino</b> (decisión del propietario, 2026-08-29): la etiqueta
/// concuerda con el concepto que nombra —la <i>acreditación</i>—, igual que el
/// propio enum (<c>Aceptada</c>, <c>Rechazada</c>, <c>NoRequerida</c>). Antes
/// la interfaz lo renderizaba en masculino, concordando con <i>documento</i>,
/// y código e interfaz decían cosas distintas sobre el mismo valor.
/// </para>
///
/// <para>
/// <b>Nunca «validado» aquí.</b> Ese término queda reservado al eje de
/// confianza técnica del archivo (firma íntegra, emisor reconocible — ver
/// <c>DecisionValidacionOficial</c> y <c>NivelConfianzaDocumental</c>). La
/// aceptación por un tercero se dice «aceptada», y solo así: son dos
/// preguntas distintas sobre el mismo documento y compartir palabra las
/// funde.
/// </para>
/// </summary>
public static class EstadoAcreditacionUi
{
    public static string Texto(EstadoAcreditacion estado) => estado switch
    {
        EstadoAcreditacion.PendienteDeSubir => "pendiente de envío",
        EstadoAcreditacion.Subida => "enviada",
        EstadoAcreditacion.Aceptada => "aceptada",
        EstadoAcreditacion.Rechazada => "rechazada",
        EstadoAcreditacion.NoRequerida => "no exigida",
        _ => "—"
    };

    /// <summary>
    /// La misma etiqueta con la inicial en mayúscula, para cuando encabeza un
    /// badge en vez de ir dentro de una frase («Nalanda: aceptada»). Se deriva
    /// del texto único en vez de mantener una segunda tabla: así no puede
    /// divergir de <see cref="Texto"/>, que es justo el fallo que este helper
    /// existe para cerrar.
    /// </summary>
    public static string TextoCapitalizado(EstadoAcreditacion estado)
    {
        var texto = Texto(estado);
        return texto.Length == 0 ? texto : char.ToUpperInvariant(texto[0]) + texto[1..];
    }
}
