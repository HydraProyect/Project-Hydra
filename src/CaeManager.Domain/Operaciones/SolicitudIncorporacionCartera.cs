using CaeManager.Domain.Common;

namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Un Gestor CAE pide incorporarse a la cartera de un Tenant propietario entero
/// que su Operador CAE ya opera (una <see cref="AsignacionOperacion"/> externa,
/// universal y vigente), y un Coordinador CAE del mismo Operador CAE la acepta o
/// la rechaza.
///
/// <b>La solicitud no concede nada.</b> Mientras está <see cref="EstadoSolicitudIncorporacionCartera.Pendiente"/>
/// el solicitante no ve ni un dato del Tenant propietario: solo la aceptación
/// crea la <see cref="AsignacionCartera"/>, universal y sin caducidad, que se
/// suma a las que ya tuvieran otros Gestores CAE sobre la misma operación.
///
/// Es un catálogo, no una <see cref="EntidadConTenant"/>, por la misma razón que
/// <see cref="AsignacionResponsabilidad"/>: cruza dos Tenants. Pertenece al
/// Operador CAE —es su flujo de trabajo interno—, y la base de datos la acota a
/// él por <c>app.tenant_origen_id</c>. Tiene que poder escribirse en la misma
/// transacción que la cartera, que exige como Tenant activo el propietario.
///
/// Quién pide, quién resuelve, quién revoca y cuándo quedan en la propia fila,
/// además de en la auditoría genérica: son la respuesta a «¿por qué tiene este
/// Gestor CAE acceso a este Tenant?».
/// </summary>
public class SolicitudIncorporacionCartera : Entity, IVersionable
{
    public const int LongitudMaximaMensaje = 1000;

    /// <summary>El Operador CAE en cuyo seno se pide y se resuelve.</summary>
    public Guid OperadorTenantId { get; private set; }

    /// <summary>El Tenant propietario al que se pide entrar, entero.</summary>
    public Guid PropietarioTenantId { get; private set; }

    /// <summary>La operación bajo la que colgará la cartera si se acepta.</summary>
    public Guid AsignacionOperacionId { get; private set; }

    /// <summary>Gestor CAE que pide. Guid suelto hacia <c>ApplicationUser</c>, mismo patrón que <see cref="AsignacionCartera.UsuarioId"/>.</summary>
    public Guid SolicitanteUsuarioId { get; private set; }

    /// <summary>Texto libre del Gestor CAE. Dato del usuario: se escapa al mostrarlo.</summary>
    public string Mensaje { get; private set; } = string.Empty;

    public EstadoSolicitudIncorporacionCartera Estado { get; private set; }

    public DateTime CreadaEnUtc { get; private set; }

    /// <summary>Coordinador CAE que aceptó o rechazó. Nulo si sigue pendiente o si se anuló sola.</summary>
    public Guid? ResueltaPorUsuarioId { get; private set; }

    public DateTime? ResueltaEnUtc { get; private set; }

    public MotivoAnulacionSolicitudCartera? MotivoAnulacion { get; private set; }

    /// <summary>La cartera creada al aceptar. Es la única que una revocación de esta solicitud puede cerrar.</summary>
    public Guid? AsignacionCarteraId { get; private set; }

    /// <summary>
    /// La fila heredada de Operador Delegado que se creó al aceptar, si hizo
    /// falta crearla. Solo esa se retira al revocar: si el Gestor CAE ya la
    /// tenía antes de pedir, no es de esta solicitud y no se toca.
    /// </summary>
    public Guid? AsignacionOperadorDelegadoId { get; private set; }

    public Guid? RevocadaPorUsuarioId { get; private set; }

    public DateTime? RevocadaEnUtc { get; private set; }

    /// <inheritdoc cref="IVersionable" />
    public Guid Version { get; private set; } = Guid.NewGuid();

    private SolicitudIncorporacionCartera()
    {
        // Requerido por EF Core.
    }

    /// <summary>
    /// Abre una solicitud sobre una operación externa, universal y vigente. Que
    /// el solicitante sea un Gestor CAE del Operador CAE de esa operación y que
    /// no tenga ya el Tenant en su cartera lo comprueba el Command: Domain no
    /// ve Identity ni el resto de carteras.
    /// </summary>
    public static SolicitudIncorporacionCartera Crear(
        AsignacionOperacion operacion, Guid solicitanteUsuarioId, string mensaje, DateTime ahora)
    {
        ArgumentNullException.ThrowIfNull(operacion);
        if (solicitanteUsuarioId == Guid.Empty)
            throw new ArgumentException("La solicitud debe tener un solicitante.", nameof(solicitanteUsuarioId));
        if (operacion.EsRaiz || operacion.EsOperacionInterna)
            throw new ArgumentException(
                "Solo se puede pedir la cartera de una operación externa de un Operador CAE.", nameof(operacion));
        if (!operacion.Ambito.EsUniversal)
            throw new ArgumentException(
                "Se pide el Tenant propietario entero: la operación tiene que ser universal.", nameof(operacion));
        if (operacion.Estado != EstadoAsignacion.Vigente)
            throw new ArgumentException("La operación no está vigente.", nameof(operacion));

        return new SolicitudIncorporacionCartera
        {
            OperadorTenantId = operacion.OperadorTenantId,
            PropietarioTenantId = operacion.PropietarioTenantId,
            AsignacionOperacionId = operacion.Id,
            SolicitanteUsuarioId = solicitanteUsuarioId,
            Mensaje = NormalizarMensaje(mensaje),
            Estado = EstadoSolicitudIncorporacionCartera.Pendiente,
            CreadaEnUtc = ahora,
        };
    }

    /// <summary>
    /// El mensaje sin espacios en los extremos, o una excepción si queda vacío o
    /// pasa de <see cref="LongitudMaximaMensaje"/>. No se recorta en silencio:
    /// truncar lo que el Gestor CAE escribió cambiaría lo que el Coordinador CAE
    /// lee sin que ninguno de los dos lo sepa.
    /// </summary>
    public static string NormalizarMensaje(string? mensaje)
    {
        var limpio = mensaje?.Trim() ?? string.Empty;
        if (limpio.Length == 0)
            throw new ArgumentException("Explica por qué pides incorporarte.", nameof(mensaje));
        if (limpio.Length > LongitudMaximaMensaje)
            throw new ArgumentException(
                $"El mensaje no puede pasar de {LongitudMaximaMensaje} caracteres.", nameof(mensaje));
        return limpio;
    }

    /// <summary>
    /// Pendiente → Aceptada, enlazando la cartera que se acaba de crear. La
    /// cartera tiene que ser exactamente la que la solicitud pedía: del
    /// solicitante, bajo esta operación y universal.
    /// </summary>
    public void Aceptar(
        Guid resolutorUsuarioId, AsignacionCartera cartera, Guid? asignacionOperadorDelegadoId, DateTime ahora)
    {
        ArgumentNullException.ThrowIfNull(cartera);
        ExigirResolucionPorOtro(resolutorUsuarioId);
        if (cartera.AsignacionOperacionId != AsignacionOperacionId
            || cartera.UsuarioId != SolicitanteUsuarioId
            || !cartera.Ambito.EsUniversal)
            throw new ArgumentException(
                "La cartera no corresponde a lo que pedía la solicitud.", nameof(cartera));

        Estado = EstadoSolicitudIncorporacionCartera.Aceptada;
        ResueltaPorUsuarioId = resolutorUsuarioId;
        ResueltaEnUtc = ahora;
        AsignacionCarteraId = cartera.Id;
        AsignacionOperadorDelegadoId = asignacionOperadorDelegadoId;
    }

    /// <summary>Pendiente → Rechazada. No crea ni cierra nada más.</summary>
    public void Rechazar(Guid resolutorUsuarioId, DateTime ahora)
    {
        ExigirResolucionPorOtro(resolutorUsuarioId);

        Estado = EstadoSolicitudIncorporacionCartera.Rechazada;
        ResueltaPorUsuarioId = resolutorUsuarioId;
        ResueltaEnUtc = ahora;
    }

    /// <summary>
    /// Pendiente → Anulada, sin resolutor: la solicitud dejó de tener sentido
    /// antes de que nadie la resolviera.
    /// </summary>
    public void Anular(MotivoAnulacionSolicitudCartera motivo, DateTime ahora)
    {
        ExigirPendiente();

        Estado = EstadoSolicitudIncorporacionCartera.Anulada;
        MotivoAnulacion = motivo;
        ResueltaEnUtc = ahora;
    }

    /// <summary>
    /// Aceptada → Revocada. Quién puede revocar (un Coordinador CAE del mismo
    /// Operador CAE, o el propio solicitante) lo decide el Command; aquí solo
    /// se registra quién lo hizo. Cerrar la cartera es cosa del escritor de
    /// asignaciones, en la misma transacción.
    /// </summary>
    public void Revocar(Guid actorUsuarioId, DateTime ahora)
    {
        if (actorUsuarioId == Guid.Empty)
            throw new ArgumentException("La revocación debe tener un autor.", nameof(actorUsuarioId));
        if (Estado != EstadoSolicitudIncorporacionCartera.Aceptada)
            throw new InvalidOperationException($"Solo se puede revocar una solicitud Aceptada (estaba {Estado}).");

        Estado = EstadoSolicitudIncorporacionCartera.Revocada;
        RevocadaPorUsuarioId = actorUsuarioId;
        RevocadaEnUtc = ahora;
    }

    /// <summary>Nadie resuelve su propia solicitud (contrato § 13).</summary>
    private void ExigirResolucionPorOtro(Guid resolutorUsuarioId)
    {
        ExigirPendiente();
        if (resolutorUsuarioId == Guid.Empty)
            throw new ArgumentException("La resolución debe tener un autor.", nameof(resolutorUsuarioId));
        if (resolutorUsuarioId == SolicitanteUsuarioId)
            throw new InvalidOperationException("Nadie puede resolver su propia solicitud.");
    }

    private void ExigirPendiente()
    {
        if (Estado != EstadoSolicitudIncorporacionCartera.Pendiente)
            throw new InvalidOperationException($"La solicitud ya no está pendiente (está {Estado}).");
    }
}
