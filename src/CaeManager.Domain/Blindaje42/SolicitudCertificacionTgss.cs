using CaeManager.Domain.Common;

namespace CaeManager.Domain.Blindaje42;

/// <summary>
/// Registro de una solicitud a la TGSS de "certificación negativa por
/// descubiertos" sobre una Empresa (contratista), hecha por el Cliente que
/// la contrata como empresa principal (art. 42.1 ET — texto verificado
/// contra el BOE, no contra una paráfrasis: "Las empresas que contraten o
/// subcontraten con otras la realización de obras o servicios
/// correspondientes a la propia actividad de aquellas deberán comprobar que
/// dichas contratistas están al corriente en el pago de las cuotas de la
/// Seguridad Social... recabarán por escrito, con identificación de la
/// empresa afectada, certificación negativa por descubiertos en la
/// Tesorería General de la Seguridad Social, que deberá librar
/// inexcusablemente dicha certificación en el término de treinta días
/// improrrogables"). Es un hecho puntual, no un estado editable: cada
/// solicitud es una fotografía de la situación de deudas de la Empresa en
/// su fecha de solicitud — <see cref="RegistrarRespuesta"/> solo puede
/// completarse una vez, igual que la TGSS solo responde una vez a cada
/// solicitud.
///
/// Límite importante que el producto no debe difuminar (STS 124/2021, 3 de
/// febrero de 2021 — verificar cita exacta si se cuestiona en producción):
/// la certificación negativa (o el silencio de 30 días) solo exonera de la
/// responsabilidad solidaria del art. 42.2 ET por los descubiertos
/// ANTERIORES a la fecha de esta solicitud, nunca por los que la Empresa
/// genere después mientras dure el encargo. El blindaje de una contrata
/// larga exige solicitudes periódicas — una sola no cubre los tres años de
/// responsabilidad solidaria del art. 42.2 ET, y la interfaz no debe
/// sugerir lo contrario.
///
/// Alcance de este primer incremento: solo la arista de <c>RelacionEmpresarial</c>
/// con <c>EnmarcadaEnId == null</c> (relación de primer nivel, Cliente↔Empresa
/// — ADR-011 § 2.4), que es donde el catálogo documental ya situaba el
/// "Certificado corriente Seguridad Social". Una Empresa puede encadenar
/// Subcontratas propias mediante <c>EnmarcadaEnId</c>, y esos tramos también
/// son relaciones de empresa principal a efectos del art. 42.1 ET, pero
/// quedan fuera de este incremento — extender el cómputo a toda la cadena es
/// una decisión de producto propia, no una consecuencia automática de que el
/// dato exista.
///
/// <see cref="ClienteId"/> apunta a <c>Empresa</c>, no a <c>Cliente</c> —
/// como <c>RelacionEmpresarial.ClienteId</c> (ADR-011, F4 cerrado 2026-08-27):
/// <c>Cliente</c> está congelada desde F3b (PR #279) y el modelo aprobado la
/// retira. Que hoy el Id de un Cliente coincida con el de su Empresa espejo
/// es un accidente de la migración F3, no un contrato en el que apoyarse.
///
/// El chequeo de "¿existe relación?" (ver el Command que crea esta entidad)
/// no exige que la <c>RelacionEmpresarial</c> siga vigente hoy — solo que
/// existiera en la fecha de la solicitud. La responsabilidad solidaria del
/// art. 42.2 ET dura 3 años DESPUÉS de terminar el encargo, así que una
/// solicitud tardía sobre una relación ya cerrada sigue siendo legítima.
/// </summary>
public class SolicitudCertificacionTgss : EntidadBase
{
    /// <summary>
    /// Plazo "improrrogable" del art. 42.1 ET. El texto legal remite a
    /// desarrollo reglamentario para el cómputo exacto y no precisa si son
    /// días naturales o hábiles — se cuenta aquí como naturales (uso más
    /// extendido en fuentes secundarias), pero <see cref="FechaLimiteOrientativa"/>
    /// es orientativa, no una fecha de corte verificada contra el Reglamento
    /// General de Recaudación de la Seguridad Social.
    /// </summary>
    public const int PlazoDiasTgss = 30;

    public const int LongitudMaximaObservaciones = 1000;
    public const int LongitudMaximaNombreArchivo = 260;

    public Guid EmpresaId { get; private set; }
    public Guid ClienteId { get; private set; }
    public DateOnly FechaSolicitud { get; private set; }
    public Guid SolicitadaPorUsuarioId { get; private set; }

    public ResultadoCertificacionTgss? Resultado { get; private set; }
    public DateOnly? FechaRespuesta { get; private set; }
    public Guid? RespuestaRegistradaPorUsuarioId { get; private set; }

    public string? EvidenciaArchivoRuta { get; private set; }
    public string? EvidenciaNombreArchivo { get; private set; }
    public string? Observaciones { get; private set; }

    /// <summary>Ver el comentario de <see cref="PlazoDiasTgss"/> — orientativa, no verificada contra el reglamento.</summary>
    public DateOnly FechaLimiteOrientativa => FechaSolicitud.AddDays(PlazoDiasTgss);

    private SolicitudCertificacionTgss()
    {
    }

    public SolicitudCertificacionTgss(
        Guid empresaId, Guid clienteId, DateOnly fechaSolicitud, Guid solicitadaPorUsuarioId, string? observaciones = null)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("La solicitud debe referirse a una empresa.", nameof(empresaId));
        if (clienteId == Guid.Empty)
            throw new ArgumentException("La solicitud debe registrarse a nombre de un cliente.", nameof(clienteId));
        if (solicitadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La solicitud debe registrar quién la hizo.", nameof(solicitadaPorUsuarioId));
        if (fechaSolicitud > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("La fecha de solicitud no puede ser futura.", nameof(fechaSolicitud));

        EmpresaId = empresaId;
        ClienteId = clienteId;
        FechaSolicitud = fechaSolicitud;
        SolicitadaPorUsuarioId = solicitadaPorUsuarioId;
        EstablecerObservaciones(observaciones);
    }

    /// <summary>
    /// Registra la respuesta de la TGSS. Solo una vez: una segunda respuesta
    /// a la misma solicitud no tiene sentido — si la TGSS se pronuncia de
    /// nuevo (p. ej. tras una reclamación), es una solicitud distinta con su
    /// propia fecha.
    /// </summary>
    public void RegistrarRespuesta(ResultadoCertificacionTgss resultado, DateOnly fechaRespuesta, Guid usuarioId)
    {
        if (Resultado is not null)
            throw new InvalidOperationException("Esta solicitud ya tiene una respuesta registrada.");
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El registro de la respuesta debe atribuirse a un usuario.", nameof(usuarioId));
        if (fechaRespuesta < FechaSolicitud)
            throw new ArgumentException("La respuesta no puede ser anterior a la solicitud.", nameof(fechaRespuesta));
        if (fechaRespuesta > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("La fecha de respuesta no puede ser futura.", nameof(fechaRespuesta));

        Resultado = resultado;
        FechaRespuesta = fechaRespuesta;
        RespuestaRegistradaPorUsuarioId = usuarioId;
    }

    /// <summary>Misma razón de orden que en VerificacionExternaSubcontrata: el archivo se guarda primero, la entidad solo conoce su ruta después.</summary>
    public void AdjuntarEvidencia(string archivoRuta, string nombreArchivo)
    {
        if (string.IsNullOrWhiteSpace(archivoRuta))
            throw new ArgumentException("La evidencia debe tener una ruta de archivo.", nameof(archivoRuta));
        if (string.IsNullOrWhiteSpace(nombreArchivo))
            throw new ArgumentException("La evidencia debe tener un nombre de archivo.", nameof(nombreArchivo));

        var nombreNormalizado = nombreArchivo.Trim();
        if (nombreNormalizado.Length > LongitudMaximaNombreArchivo)
            throw new ArgumentException(
                $"El nombre del archivo no puede superar {LongitudMaximaNombreArchivo} caracteres.", nameof(nombreArchivo));

        EvidenciaArchivoRuta = archivoRuta;
        EvidenciaNombreArchivo = nombreNormalizado;
    }

    private void EstablecerObservaciones(string? observaciones)
    {
        if (string.IsNullOrWhiteSpace(observaciones))
        {
            Observaciones = null;
            return;
        }

        var normalizadas = observaciones.Trim();
        if (normalizadas.Length > LongitudMaximaObservaciones)
            throw new ArgumentException(
                $"Las observaciones no pueden superar {LongitudMaximaObservaciones} caracteres.", nameof(observaciones));

        Observaciones = normalizadas;
    }
}
