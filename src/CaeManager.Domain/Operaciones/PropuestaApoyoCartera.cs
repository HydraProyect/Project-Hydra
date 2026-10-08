using CaeManager.Domain.Common;

namespace CaeManager.Domain.Operaciones;

/// <summary>
/// El Gestor CAE principal —o el Coordinador CAE principal— de una
/// <see cref="AsignacionOperacion"/> externa propone a otro Gestor CAE del <b>mismo Operador
/// CAE</b> una Asignación de Cartera de apoyo sobre ese Tenant propietario, y el destinatario la
/// acepta o la rechaza (ADR-011 § 2.7, enmienda 2026-10-08).
///
/// <para>
/// <b>La propuesta no concede nada.</b> Mientras está <see cref="EstadoPropuestaApoyoCartera.Pendiente"/>
/// el destinatario no ve ni un dato del Tenant propietario: solo su aceptación emite la
/// <see cref="AsignacionCartera"/>, del Tenant entero, con rol Gestor CAE y <b>sin la marca de
/// principal</b>. Quien propone no elige el rol, ni el ámbito, ni la marca.
/// </para>
///
/// <para>
/// Es el reverso de <see cref="SolicitudIncorporacionCartera"/> y no se funde con ella: allí
/// pide el beneficiario y resuelve otro (un Coordinador CAE); aquí propone un igual y resuelve
/// <b>el beneficiario</b>. Quien propone solo puede retirarla.
/// </para>
///
/// <para>
/// Catálogo del Operador CAE, no una <see cref="EntidadConTenant"/>: cruza dos Tenants y la
/// base de datos la acota por <c>app.tenant_origen_id</c>, de modo que el destinatario la ve
/// con cualquier Tenant activo y se puede aceptar en la misma transacción que escribe la
/// cartera, que exige como Tenant activo el propietario.
/// </para>
/// </summary>
public class PropuestaApoyoCartera : Entity, IVersionable
{
    /// <summary>El Operador CAE en cuyo seno se propone y se responde.</summary>
    public Guid OperadorTenantId { get; private set; }

    /// <summary>El Tenant propietario sobre el que se propone el apoyo, entero.</summary>
    public Guid PropietarioTenantId { get; private set; }

    /// <summary>La operación bajo la que colgará la cartera de apoyo si se acepta.</summary>
    public Guid AsignacionOperacionId { get; private set; }

    /// <summary>Quien propone: el principal de la operación al proponer. Guid suelto hacia <c>ApplicationUser</c>.</summary>
    public Guid ProponenteUsuarioId { get; private set; }

    /// <summary>El Gestor CAE al que se propone el apoyo, y el único que puede aceptar o rechazar.</summary>
    public Guid DestinatarioUsuarioId { get; private set; }

    public EstadoPropuestaApoyoCartera Estado { get; private set; }

    /// <summary>
    /// Fecha de fin que el proponente sugiere para el apoyo. Se guarda, pero ninguna vía la
    /// ofrece ni la aplica todavía: la cartera de apoyo se emite sin caducidad.
    /// </summary>
    public DateTime? VigenciaHastaPropuesta { get; private set; }

    public DateTime CreadaEnUtc { get; private set; }

    /// <summary>Cuándo dejó de estar pendiente, por cualquiera de las cuatro salidas.</summary>
    public DateTime? ResueltaEnUtc { get; private set; }

    public MotivoAnulacionPropuestaApoyo? MotivoAnulacion { get; private set; }

    /// <summary>La cartera de apoyo emitida al aceptar.</summary>
    public Guid? AsignacionCarteraId { get; private set; }

    /// <summary>La fila heredada de Operador Delegado creada al aceptar, para poder retirar solo esa.</summary>
    public Guid? AsignacionOperadorDelegadoId { get; private set; }

    /// <inheritdoc cref="IVersionable" />
    public Guid Version { get; private set; } = Guid.NewGuid();

    private PropuestaApoyoCartera()
    {
        // Requerido por EF Core.
    }

    /// <summary>
    /// Abre una propuesta sobre una operación externa, del Tenant entero y vigente. Que quien
    /// propone sea el principal vigente de esa operación, y que el destinatario sea un Gestor
    /// CAE activo del mismo Operador CAE sin ese Tenant en su cartera, lo comprueba el
    /// Command: Domain no ve Identity ni las carteras.
    /// </summary>
    public static PropuestaApoyoCartera Crear(
        AsignacionOperacion operacion, Guid proponenteUsuarioId, Guid destinatarioUsuarioId,
        DateTime? vigenciaHastaPropuesta, DateTime ahora)
    {
        ArgumentNullException.ThrowIfNull(operacion);
        if (proponenteUsuarioId == Guid.Empty)
            throw new ArgumentException("La propuesta de apoyo debe tener un proponente.", nameof(proponenteUsuarioId));
        if (destinatarioUsuarioId == Guid.Empty)
            throw new ArgumentException("La propuesta de apoyo debe tener un destinatario.", nameof(destinatarioUsuarioId));
        if (destinatarioUsuarioId == proponenteUsuarioId)
            throw new ArgumentException("Nadie se propone un apoyo a sí mismo.", nameof(destinatarioUsuarioId));
        if (operacion.EsRaiz || operacion.EsOperacionInterna)
            throw new ArgumentException(
                "Solo se propone un apoyo sobre una operación externa de un Operador CAE.", nameof(operacion));
        if (!operacion.Ambito.EsUniversal)
            throw new ArgumentException(
                "El apoyo es sobre el Tenant propietario entero: la operación tiene que ser universal.", nameof(operacion));
        if (operacion.Estado != EstadoAsignacion.Vigente)
            throw new ArgumentException("La operación no está vigente.", nameof(operacion));
        if (vigenciaHastaPropuesta is { } hasta && hasta <= ahora)
            throw new ArgumentException("La fecha de fin propuesta tiene que ser futura.", nameof(vigenciaHastaPropuesta));

        return new PropuestaApoyoCartera
        {
            OperadorTenantId = operacion.OperadorTenantId,
            PropietarioTenantId = operacion.PropietarioTenantId,
            AsignacionOperacionId = operacion.Id,
            ProponenteUsuarioId = proponenteUsuarioId,
            DestinatarioUsuarioId = destinatarioUsuarioId,
            Estado = EstadoPropuestaApoyoCartera.Pendiente,
            VigenciaHastaPropuesta = vigenciaHastaPropuesta,
            CreadaEnUtc = ahora,
        };
    }

    /// <summary>
    /// Pendiente → Aceptada, por el destinatario, enlazando la cartera recién emitida. La
    /// cartera tiene que ser exactamente la de un apoyo: del destinatario, bajo esta operación,
    /// del Tenant entero y <b>sin la marca de principal</b>.
    /// </summary>
    public void Aceptar(
        Guid actorUsuarioId, AsignacionCartera cartera, Guid? asignacionOperadorDelegadoId, DateTime ahora)
    {
        ArgumentNullException.ThrowIfNull(cartera);
        ExigirRespuestaDelDestinatario(actorUsuarioId);
        if (cartera.AsignacionOperacionId != AsignacionOperacionId
            || cartera.UsuarioId != DestinatarioUsuarioId
            || !cartera.Ambito.EsUniversal)
            throw new ArgumentException(
                "La cartera no corresponde a lo que proponía la propuesta de apoyo.", nameof(cartera));
        if (cartera.EsPrincipal)
            throw new InvalidOperationException("Una cartera de apoyo nunca nace con la marca de principal.");

        Estado = EstadoPropuestaApoyoCartera.Aceptada;
        ResueltaEnUtc = ahora;
        AsignacionCarteraId = cartera.Id;
        AsignacionOperadorDelegadoId = asignacionOperadorDelegadoId;
    }

    /// <summary>Pendiente → Rechazada, por el destinatario. No emite nada.</summary>
    public void Rechazar(Guid actorUsuarioId, DateTime ahora)
    {
        ExigirRespuestaDelDestinatario(actorUsuarioId);

        Estado = EstadoPropuestaApoyoCartera.Rechazada;
        ResueltaEnUtc = ahora;
    }

    /// <summary>Pendiente → Retirada, por quien la propuso. Es lo único que el proponente puede hacer con ella.</summary>
    public void Retirar(Guid actorUsuarioId, DateTime ahora)
    {
        ExigirPendiente();
        if (actorUsuarioId != ProponenteUsuarioId)
            throw new InvalidOperationException("Solo quien propuso el apoyo puede retirar la propuesta.");

        Estado = EstadoPropuestaApoyoCartera.Retirada;
        ResueltaEnUtc = ahora;
    }

    /// <summary>
    /// Pendiente → Anulada: al ir a aceptarla, lo que la sostenía ya no se cumple. Nadie la
    /// resolvió; se registra por qué.
    /// </summary>
    public void Anular(MotivoAnulacionPropuestaApoyo motivo, DateTime ahora)
    {
        ExigirPendiente();

        Estado = EstadoPropuestaApoyoCartera.Anulada;
        MotivoAnulacion = motivo;
        ResueltaEnUtc = ahora;
    }

    /// <summary>Acepta o rechaza el destinatario, y nadie más: ni quien propuso ni un tercero.</summary>
    private void ExigirRespuestaDelDestinatario(Guid actorUsuarioId)
    {
        ExigirPendiente();
        if (actorUsuarioId != DestinatarioUsuarioId)
            throw new InvalidOperationException("Solo el destinatario puede responder a la propuesta de apoyo.");
    }

    private void ExigirPendiente()
    {
        if (Estado != EstadoPropuestaApoyoCartera.Pendiente)
            throw new InvalidOperationException($"La propuesta de apoyo ya no está pendiente (está {Estado}).");
    }
}
