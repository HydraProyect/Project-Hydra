using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.EstadoMaterializado;

/// <summary>
/// Estado en TALVEG de un par exigido (<see cref="EstadoRequisito.EstadoTalveg"/>). Enum propio y no
/// <see cref="EstadoDocumento"/> porque el par tiene un estado que un documento no tiene: <see cref="NoAplica"/>, la marca del
/// Gestor CAE de que ese requisito no aplica en ese Centro (D-3, sin comando todavía), que no cuenta en el denominador del
/// cumplimiento. Meterlo en <see cref="EstadoDocumento"/> lo haría aparecer en todos los <c>switch</c> de vigencia de un
/// documento, que nunca pueden recibirlo.
///
/// <para>
/// Los valores coinciden con los de <see cref="EstadoDocumento"/> a propósito (la conversión es un molde y lo comprueba
/// <c>EstadoTalvegRequisitoTests</c>); <see cref="NoAplica"/> va al final. <b>Valores congelados</b>: se guardan como
/// <c>smallint</c> (<c>OrdinalesDeEnumsPublicadosTests</c>).
/// </para>
/// </summary>
public enum EstadoTalvegRequisito
{
    SinCaducidad = 0,
    Vigente = 1,
    Proximo = 2,
    Urgente = 3,
    Vencido = 4,
    Faltante = 5,
    SinConfirmar = 6,
    EnTolerancia = 7,
    NoAplica = 8
}

/// <summary>Conversión entre el estado de un documento y el estado en TALVEG de un par.</summary>
public static class EstadoTalvegRequisitoExtensions
{
    public static EstadoTalvegRequisito ComoEstadoDeRequisito(this EstadoDocumento estado) => (EstadoTalvegRequisito)(int)estado;

    /// <summary><c>null</c> para <see cref="EstadoTalvegRequisito.NoAplica"/>, que no es un estado de documento.</summary>
    public static EstadoDocumento? ComoEstadoDocumento(this EstadoTalvegRequisito estado) =>
        estado == EstadoTalvegRequisito.NoAplica ? null : (EstadoDocumento)(int)estado;
}
