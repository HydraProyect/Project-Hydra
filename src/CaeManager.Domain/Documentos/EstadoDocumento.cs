namespace CaeManager.Domain.Documentos;

/// <summary>
/// Estado de vigencia de un Documento. Nunca se persiste: se calcula siempre
/// a partir de la vigencia explícita del Documento (<see cref="VigenciaDocumento"/>)
/// y los umbrales configurables de
/// ParametroSistema (ver CalculadoraEstadoDocumento y DATABASE.md).
///
/// <para>
/// <b>Valores numéricos explícitos y congelados.</b> Este enum sale por la
/// API pública, así que sus ordinales dejaron de ser un detalle interno: una
/// inserción en medio habría cambiado en silencio el significado de todo lo
/// entregado. Declararlos hace visible el contrato y permite que un ratchet
/// lo vigile (<c>OrdinalesDeEnumsPublicadosTests</c>). Desde 2026-08-27 la
/// API los serializa como CADENA, así que el número ya no viaja — pero se
/// mantiene fijo porque el enum también se compara y ordena por él.
/// </para>
/// </summary>
public enum EstadoDocumento
{
    /// <summary>
    /// Confirmado que el documento no caduca (p. ej. Formación 60h). Solo sale
    /// de <see cref="EstadoVigenciaDocumento.NoCaduca"/>; una fecha que nadie
    /// ha anotado es <see cref="SinConfirmar"/>, no esto.
    /// </summary>
    SinCaducidad = 0,
    Vigente = 1,
    Proximo = 2,
    Urgente = 3,
    Vencido = 4,

    /// <summary>
    /// No es un estado de vigencia — no hay ningún Documento que evaluar.
    /// Un Trabajador con Asignación activa a un Centro que exige un
    /// TipoDocumento obligatorio (ver ObtenerAlertasQuery) y ningún
    /// Documento de ese tipo. <see cref="CalculadoraEstadoDocumento"/> nunca
    /// produce este valor — solo lo calcula la Query de Alertas, que sí
    /// sabe qué debería existir y no solo qué existe.
    /// </summary>
    Faltante = 5,

    /// <summary>
    /// El Documento existe pero nadie ha confirmado hasta cuándo vale
    /// (<see cref="EstadoVigenciaDocumento.SinConfirmar"/>). No es vigente ni
    /// vencido: es no saberlo, y no puede contar como «en regla» ni ganar a una
    /// copia con vigencia comprobada.
    /// </summary>
    SinConfirmar = 6
}
