namespace CaeManager.Domain.Documentos;

/// <summary>
/// Estado de vigencia de un Documento. Nunca se persiste: se calcula siempre
/// a partir de la vigencia explícita del Documento (<see cref="VigenciaDocumento"/>)
/// y los umbrales configurables de
/// ParametroSistema (ver CalculadoraEstadoDocumento y Project-Hydra-Negocio/tecnico/DATABASE.md).
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
    /// (<see cref="EstadoVigenciaDocumento.SinConfirmar"/>). No es vencido: es no
    /// saberlo. Según la superficie (decisiones del propietario):
    /// <list type="bullet">
    /// <item>Paneles e incidencias (Documentación base, Trabajador 360): cuenta como
    /// «al día con aviso» visible (2026-10-01).</item>
    /// <item>Porcentajes de cumplimiento: entra en el denominador como no conforme y
    /// baja el porcentaje (2026-10-03).</item>
    /// <item>Elección de la copia que representa al tipo en el estado y el cumplimiento
    /// (<c>PreferenciaDocumentoPorTipo</c>): no gana a una copia con vigencia comprobada.</item>
    /// <item>Paquete de acreditación: es vigente y entra, compitiendo por su fecha de
    /// emisión como cualquier otra copia (2026-10-01).</item>
    /// </list>
    /// </summary>
    SinConfirmar = 6,

    /// <summary>
    /// Estado <b>de contexto</b> (aprobado 2026-10-03): el Documento está Vencido, pero en ESTE Centro sigue valiendo para
    /// acceder porque el Centro (o su Cliente empresarial) concede tolerancia y todavía no se ha agotado. Nunca lo produce
    /// <see cref="CalculadoraEstadoDocumento"/> —el estado de vigencia del Documento sigue siendo
    /// <see cref="Vencido"/>—: solo lo asignan las vistas con contexto de Centro (Centro 360 y Trabajador 360 por Centro),
    /// con <see cref="ReglaBloqueoDeAcceso.EnToleranciaHasta"/>. Las vistas generales, sin contexto, siguen diciendo
    /// «Vencido». Se rotula «Vencido · en tolerancia hasta dd/MM», con severidad entre Urgente y Vencido
    /// (<see cref="SeveridadEstadoDocumento"/>). No entra en el porcentaje de cumplimiento en este incremento: que un vencido
    /// dentro de la tolerancia cuente como al día es el incremento 2 del porcentaje (<c>EsConforme(ParDocumentalExigido)</c>).
    /// </summary>
    EnTolerancia = 7
}
