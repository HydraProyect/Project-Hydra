using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.DerechosInteresado;

/// <summary>
/// Solicitud de un derecho del interesado (RGPD arts. 15 a 22) recibida por el Tenant propietario, responsable
/// del tratamiento de sus datos (#2002). Es el contenedor que referenciarán después las salidas de la #11
/// (restringir, bloquear, suprimir) y de la #2003 (paquete de acceso): esta entidad no produce esas salidas.
///
/// <para>
/// <b>Quién hace qué</b> (decisión D-3 del 2026-11-15). La registra cualquier cuenta con escritura en el Tenant,
/// también un Gestor CAE de un Operador CAE externo que opera el Tenant por Asignación de Operación. La tramitan
/// y la resuelven solo el Administrador del Tenant propietario y las cuentas del propio Tenant a las que él haya
/// concedido <c>CapacidadTenant.DerechosDelInteresado</c>. Esa autorización vive en Application
/// (<c>AutoridadCapacidadesTenant</c>); aquí solo están los invariantes de la solicitud.
/// </para>
///
/// <para>
/// <b>Ciclo.</b> Recibida → IdentidadVerificada → EnCurso → Resuelta. Se puede denegar, con motivo, desde
/// cualquier estado abierto, también sin identidad verificada (no poder verificarla es uno de los motivos). Nada
/// se resuelve sin identidad verificada: Resolver exige EnCurso, y a EnCurso solo se llega desde
/// IdentidadVerificada. <see cref="EstadoSolicitudDerecho.Reenviada"/> existe para el registro de un Actor de
/// Plataforma TALVEG que reenvía la solicitud al Tenant propietario (PR posterior) y hoy no la produce nada.
/// </para>
///
/// <para>
/// <b>Plazo</b> (RGPD art. 12.3): un mes desde la recepción, prorrogable una sola vez hasta dos meses más con
/// motivo escrito, y solo mientras no haya vencido el plazo ordinario.
/// </para>
/// </summary>
public class SolicitudDerechoInteresado : EntidadConTenant, IVersionable, IAccionAuditoriaPropia
{
    public const int LongitudMaximaNombreInteresado = 200;
    public const int LongitudMaximaDescripcion = 2000;
    public const int LongitudMaximaMotivo = 2000;

    /// <summary>Meses del plazo ordinario desde la recepción.</summary>
    public const int MesesPlazoOrdinario = 1;

    /// <summary>Meses que como mucho añade la única prórroga.</summary>
    public const int MesesMaximosProrroga = 2;

    public TipoDerechoInteresado Tipo { get; private set; }
    public CanalEntradaSolicitudDerecho Canal { get; private set; }
    public DateOnly FechaRecepcion { get; private set; }

    /// <summary>Datos mínimos del interesado: su nombre tal como lo identifica la solicitud.</summary>
    public string NombreInteresado { get; private set; } = string.Empty;

    /// <summary>El Trabajador del mismo Tenant al que se refiere la solicitud, si se identificó.</summary>
    public Guid? TrabajadorId { get; private set; }

    public string? Descripcion { get; private set; }

    public EstadoSolicitudDerecho Estado { get; private set; }

    /// <summary>Recepción + un mes; no cambia.</summary>
    public DateOnly PlazoOrdinario { get; private set; }

    /// <summary>El plazo que rige hoy: el ordinario o, si se prorrogó, el prorrogado.</summary>
    public DateOnly PlazoVencimiento { get; private set; }

    public Guid RegistradaPorUsuarioId { get; private set; }
    public DateTime RegistradaEnUtc { get; private set; }

    public Guid? IdentidadVerificadaPorUsuarioId { get; private set; }
    public DateTime? IdentidadVerificadaEnUtc { get; private set; }

    public Guid? TramitacionIniciadaPorUsuarioId { get; private set; }
    public DateTime? TramitacionIniciadaEnUtc { get; private set; }

    public string? MotivoProrroga { get; private set; }
    public Guid? ProrrogadaPorUsuarioId { get; private set; }
    public DateTime? ProrrogadaEnUtc { get; private set; }

    /// <summary>Quién la cerró (resuelta o denegada) y cuándo.</summary>
    public Guid? CerradaPorUsuarioId { get; private set; }
    public DateTime? CerradaEnUtc { get; private set; }

    /// <summary>Lo que se respondió al resolverla; opcional, y solo en Resuelta.</summary>
    public string? Resultado { get; private set; }

    /// <summary>Obligatorio al denegar; nulo en cualquier otro estado.</summary>
    public string? MotivoDenegacion { get; private set; }

    public Guid Version { get; private set; } = Guid.NewGuid();

    public bool EstaProrrogada => ProrrogadaEnUtc is not null;

    public bool EstaCerrada => Estado is EstadoSolicitudDerecho.Resuelta
        or EstadoSolicitudDerecho.Denegada
        or EstadoSolicitudDerecho.Reenviada;

    /// <summary>
    /// El último día al que puede llevarse el plazo con la prórroga: el plazo ordinario más dos meses. Se cuenta
    /// desde el plazo ordinario y no desde la recepción más tres meses porque a fin de mes la segunda cuenta da un
    /// día más tarde (31-ene: 28-abr frente a 30-abr); se toma la lectura que nunca alarga el plazo.
    /// </summary>
    public DateOnly PlazoMaximoProrrogable => PlazoOrdinario.AddMonths(MesesMaximosProrroga);

    private SolicitudDerechoInteresado()
    {
    }

    public static SolicitudDerechoInteresado Registrar(
        TipoDerechoInteresado tipo,
        CanalEntradaSolicitudDerecho canal,
        DateOnly fechaRecepcion,
        string nombreInteresado,
        Guid? trabajadorId,
        string? descripcion,
        Guid registradaPorUsuarioId,
        DateOnly hoy,
        DateTime ahoraUtc)
    {
        if (!Enum.IsDefined(tipo))
            throw new ArgumentOutOfRangeException(nameof(tipo), tipo, "Tipo de derecho desconocido.");
        if (!Enum.IsDefined(canal))
            throw new ArgumentOutOfRangeException(nameof(canal), canal, "Canal de entrada desconocido.");
        if (fechaRecepcion > hoy)
            throw new ArgumentException("La fecha de recepción no puede ser posterior a hoy.", nameof(fechaRecepcion));
        if (string.IsNullOrWhiteSpace(nombreInteresado))
            throw new ArgumentException("La solicitud necesita el nombre del interesado.", nameof(nombreInteresado));
        if (nombreInteresado.Trim().Length > LongitudMaximaNombreInteresado)
            throw new ArgumentException("El nombre del interesado es demasiado largo.", nameof(nombreInteresado));
        if (descripcion is not null && descripcion.Trim().Length > LongitudMaximaDescripcion)
            throw new ArgumentException("La descripción es demasiado larga.", nameof(descripcion));
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("El Trabajador indicado no es válido.", nameof(trabajadorId));
        ExigirActor(registradaPorUsuarioId, nameof(registradaPorUsuarioId));

        var plazo = fechaRecepcion.AddMonths(MesesPlazoOrdinario);
        return new SolicitudDerechoInteresado
        {
            Tipo = tipo,
            Canal = canal,
            FechaRecepcion = fechaRecepcion,
            NombreInteresado = nombreInteresado.Trim(),
            TrabajadorId = trabajadorId,
            Descripcion = string.IsNullOrWhiteSpace(descripcion) ? null : descripcion.Trim(),
            Estado = EstadoSolicitudDerecho.Recibida,
            PlazoOrdinario = plazo,
            PlazoVencimiento = plazo,
            RegistradaPorUsuarioId = registradaPorUsuarioId,
            RegistradaEnUtc = ahoraUtc,
        };
    }

    public void VerificarIdentidad(Guid actorUsuarioId, DateTime ahoraUtc)
    {
        ExigirEstado("verificar la identidad de", EstadoSolicitudDerecho.Recibida);
        ExigirActor(actorUsuarioId, nameof(actorUsuarioId));

        Estado = EstadoSolicitudDerecho.IdentidadVerificada;
        IdentidadVerificadaPorUsuarioId = actorUsuarioId;
        IdentidadVerificadaEnUtc = ahoraUtc;
        _accionAuditoria = RegistroAuditoria.AccionIdentidadVerificada;
    }

    public void IniciarTramitacion(Guid actorUsuarioId, DateTime ahoraUtc)
    {
        ExigirEstado("empezar a tramitar", EstadoSolicitudDerecho.IdentidadVerificada);
        ExigirActor(actorUsuarioId, nameof(actorUsuarioId));

        Estado = EstadoSolicitudDerecho.EnCurso;
        TramitacionIniciadaPorUsuarioId = actorUsuarioId;
        TramitacionIniciadaEnUtc = ahoraUtc;
        _accionAuditoria = RegistroAuditoria.AccionTramitacionIniciada;
    }

    /// <summary>
    /// La única prórroga: lleva el plazo a <paramref name="nuevoPlazo"/>, posterior al ordinario y como mucho dos
    /// meses después de él, con motivo escrito. Solo se acuerda dentro del primer mes (RGPD art. 12.3: la prórroga
    /// se informa al interesado dentro del primer mes): el día de negocio de <paramref name="ahoraUtc"/> tiene que
    /// ser, como tarde, el del plazo ordinario. Con el plazo ordinario vencido ya no hay prórroga. No cambia el
    /// estado.
    /// </summary>
    public void Prorrogar(DateOnly nuevoPlazo, string motivo, Guid actorUsuarioId, DateTime ahoraUtc)
    {
        ExigirAbierta("prorrogar");
        if (EstaProrrogada)
            throw new InvalidOperationException("El plazo de esta solicitud ya se prorrogó una vez.");
        if (DiaDeNegocio.De(ahoraUtc) > PlazoOrdinario)
            throw new InvalidOperationException(
                "El plazo ordinario ya venció: la prórroga solo se acuerda dentro del primer mes.");
        ExigirActor(actorUsuarioId, nameof(actorUsuarioId));
        var motivoLimpio = ExigirMotivo(motivo, nameof(motivo), "Prorrogar el plazo exige un motivo escrito.");
        if (nuevoPlazo <= PlazoOrdinario)
            throw new ArgumentException("El plazo prorrogado tiene que ser posterior al plazo ordinario.", nameof(nuevoPlazo));
        if (nuevoPlazo > PlazoMaximoProrrogable)
            throw new ArgumentException("La prórroga no puede superar dos meses sobre el plazo ordinario.", nameof(nuevoPlazo));

        PlazoVencimiento = nuevoPlazo;
        MotivoProrroga = motivoLimpio;
        ProrrogadaPorUsuarioId = actorUsuarioId;
        ProrrogadaEnUtc = ahoraUtc;
        _accionAuditoria = RegistroAuditoria.AccionPlazoProrrogado;
    }

    public void Resolver(string? resultado, Guid actorUsuarioId, DateTime ahoraUtc)
    {
        // Nada se resuelve sin identidad verificada: a EnCurso solo se llega desde IdentidadVerificada.
        ExigirEstado("resolver", EstadoSolicitudDerecho.EnCurso);
        ExigirActor(actorUsuarioId, nameof(actorUsuarioId));
        if (resultado is not null && resultado.Trim().Length > LongitudMaximaMotivo)
            throw new ArgumentException("El resultado es demasiado largo.", nameof(resultado));
        if (IdentidadVerificadaEnUtc is null)
            throw new InvalidOperationException("Una solicitud sin identidad verificada no se puede resolver.");

        Estado = EstadoSolicitudDerecho.Resuelta;
        Resultado = string.IsNullOrWhiteSpace(resultado) ? null : resultado.Trim();
        CerradaPorUsuarioId = actorUsuarioId;
        CerradaEnUtc = ahoraUtc;
        _accionAuditoria = RegistroAuditoria.AccionSolicitudResuelta;
    }

    public void Denegar(string motivo, Guid actorUsuarioId, DateTime ahoraUtc)
    {
        ExigirAbierta("denegar");
        ExigirActor(actorUsuarioId, nameof(actorUsuarioId));
        var motivoLimpio = ExigirMotivo(motivo, nameof(motivo), "Denegar la solicitud exige un motivo escrito.");

        Estado = EstadoSolicitudDerecho.Denegada;
        MotivoDenegacion = motivoLimpio;
        CerradaPorUsuarioId = actorUsuarioId;
        CerradaEnUtc = ahoraUtc;
        _accionAuditoria = RegistroAuditoria.AccionSolicitudDenegada;
    }

    private void ExigirEstado(string accion, EstadoSolicitudDerecho permitido)
    {
        if (Estado != permitido)
            throw new InvalidOperationException($"Una solicitud en estado {Estado} no se puede {accion}.");
    }

    private void ExigirAbierta(string accion)
    {
        if (EstaCerrada)
            throw new InvalidOperationException($"Una solicitud en estado {Estado} no se puede {accion}.");
    }

    private static void ExigirActor(Guid actorUsuarioId, string parametro)
    {
        if (actorUsuarioId == Guid.Empty)
            throw new ArgumentException("El paso necesita el actor real que lo da.", parametro);
    }

    private static string ExigirMotivo(string? motivo, string parametro, string mensaje)
    {
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException(mensaje, parametro);
        var limpio = motivo.Trim();
        if (limpio.Length > LongitudMaximaMotivo)
            throw new ArgumentException("El motivo es demasiado largo.", parametro);
        return limpio;
    }

    // Solo campo: sin propiedad, EF no lo mapea (patrón de ContratoAcceso).
    private string? _accionAuditoria;

    string? IAccionAuditoriaPropia.ConsumirAccionAuditoria()
    {
        var accion = _accionAuditoria;
        _accionAuditoria = null;
        return accion;
    }
}

/// <summary>Derecho que ejerce el interesado (RGPD arts. 15 a 22). Se guarda como texto.</summary>
public enum TipoDerechoInteresado
{
    Acceso = 1,
    Rectificacion = 2,
    Supresion = 3,
    Limitacion = 4,
    Portabilidad = 5,
    Oposicion = 6,
    DecisionesAutomatizadas = 7,
}

/// <summary>Por dónde llegó la solicitud al Tenant propietario. Se guarda como texto.</summary>
public enum CanalEntradaSolicitudDerecho
{
    CorreoElectronico = 1,
    CorreoPostal = 2,
    Presencial = 3,
    Telefono = 4,
    Otro = 5,
}

/// <summary>Estado de una <see cref="SolicitudDerechoInteresado"/>. Se guarda como texto.</summary>
public enum EstadoSolicitudDerecho
{
    Recibida = 1,
    IdentidadVerificada = 2,
    EnCurso = 3,
    Resuelta = 4,
    Denegada = 5,

    /// <summary>Reservado para el registro de un Actor de Plataforma TALVEG (PR posterior); hoy sin productor.</summary>
    Reenviada = 6,
}
