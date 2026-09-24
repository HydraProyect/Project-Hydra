namespace CaeManager.Domain.Documentos;

/// <summary>
/// Qué se sabe de la vigencia de un <see cref="Documento"/> en TALVEG. Es la
/// misma forma que <see cref="EstadoVigenciaEnPlataforma"/>, aplicada al
/// documento en vez de a su acreditación en una plataforma concreta.
///
/// <para>
/// Los tres valores son explícitos <b>a propósito</b>. Antes, una
/// <c>FechaVencimiento</c> nula significaba a la vez «este documento no
/// caduca» y «nadie ha anotado cuándo caduca», y las dos salían como vigencia
/// infinita: un documento sin fecha anotada desplazaba del paquete de
/// acreditación a otro con vigencia comprobada. «No lo sé» no es «no caduca».
/// </para>
///
/// <para>
/// La vigencia la confirma el Gestor CAE a mano —no hay integraciones que la
/// traigan—, o la calcula el sistema cuando el TipoDocumento tiene vencimiento
/// automático. Si nadie la ha confirmado, el documento está
/// <see cref="SinConfirmar"/>, que es un registro deliberado de ignorancia,
/// no un hueco.
/// </para>
///
/// <para>
/// <b>Ordinales congelados</b>: se persisten en <c>Documentos.EstadoVigencia</c>
/// y los vigila una restricción CHECK junto con la fecha.
/// </para>
/// </summary>
public enum EstadoVigenciaDocumento
{
    /// <summary>Nadie ha dicho todavía hasta cuándo vale. NO es «no caduca».</summary>
    SinConfirmar = 0,

    /// <summary>Confirmado que no caduca (p. ej. una formación de 60 horas).</summary>
    NoCaduca = 1,

    /// <summary>Vale hasta una fecha concreta, que acompaña a este estado y nunca es nula.</summary>
    VenceEnFecha = 2
}
