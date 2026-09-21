namespace CaeManager.Domain.Documentos;

/// <summary>
/// Qué se sabe de la vigencia de un Documento <b>en una plataforma CAE
/// concreta</b>, que no tiene por qué coincidir con la vigencia del propio
/// Documento en TALVEG (<see cref="EstadoDocumento"/>): el mismo tipo de
/// documento puede vencer a los 3 meses en un Centro, a los 12 en otro y no
/// vencer en un tercero.
///
/// <para>
/// Los tres valores son explícitos <b>a propósito</b>. Sin ellos, «nadie lo ha
/// anotado» y «aquí no caduca» se representan igual —una fecha nula— y salen
/// los dos en verde, que es justo el error que no se puede permitir: el
/// semáforo existe para decir si el Trabajador puede entrar al Centro, y «no
/// lo sé» no es «sí».
/// </para>
///
/// <para>
/// Mientras no haya integraciones Inbound, quien lo confirma es el Gestor CAE a
/// mano, cuando marca que la plataforma aceptó el documento. Puede no saberlo:
/// para eso está <see cref="SinConfirmar"/>, que es un registro deliberado de
/// ignorancia, no un hueco.
/// </para>
/// </summary>
public enum EstadoVigenciaEnPlataforma
{
    /// <summary>Nadie ha dicho todavía hasta cuándo vale aquí. NO es «no caduca».</summary>
    SinConfirmar = 0,

    /// <summary>La plataforma no exige vigencia para este documento: una vez aceptado, vale.</summary>
    NoVenceAqui = 1,

    /// <summary>Vale hasta una fecha concreta, que acompaña a este estado y nunca es nula.</summary>
    VenceEnFecha = 2
}
