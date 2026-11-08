using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.Gestiones;

/// <summary>
/// Gestión automática de varios pasos (especificación «Gestión de varios pasos y Mi trabajo», 2026-10-23, § 4).
/// Nace sola cuando nace un Documento de Trabajador o de Empresa con archivo, por alta con archivo (también la
/// generada desde plantilla) o por renovación con archivo nuevo, y agrupa en <b>una sola</b> Gestión las
/// acreditaciones en plataforma (<see cref="AcreditacionDocumentoPlataforma"/>) que ese mismo acto hizo nacer: un
/// paso por acceso de plataforma (D-5). Ni el alta sin archivo ni la corrección de fechas sin archivo la hacen
/// nacer. Nace en el mismo guardado que el acto (<c>INacimientoGestionAutomaticaService</c>), no después.
///
/// <para>
/// <b>El paso no tiene estado propio</b> (§ 4.3): su estado es el de su acreditación, que se cambia con los
/// mismos comandos que el panel «Gestionar» de Centro 360. Por eso la Gestión no guarda «en curso» ni
/// «cerrada»: los resuelve <see cref="ResolverEstado"/> leyendo esas acreditaciones, y un rechazo posterior la
/// reabre sin hacer nada más (D-6) mientras su Documento siga operativo. Lo único que guarda es lo que no se puede
/// derivar: cuándo se sustituyó y por cuál. Que una Gestión abierta de un Documento ya sustituido es Sustituida lo
/// deriva también la lectura, haya marca o no (<see cref="ResolverEstado"/>): un rechazo sobre una acreditación del
/// Documento histórico no la reabre, y sin marca deja de leerse Sustituida si todos sus pasos llegan a cerrarse
/// (entonces es Cerrada: tampoco queda abierta).
/// </para>
///
/// <para>
/// Es de la Empresa gestionada, no de una persona (D-8): no tiene asignado. Su Tenant propietario es el del
/// Documento (clave foránea compuesta por Tenant), y su visibilidad es la del Documento más la de cada Centro
/// para cada paso (D-7). Coexiste con la <see cref="Gestion"/> creada a mano, que sigue funcionando hasta que
/// se retire (D-1, D-12).
/// </para>
///
/// <para>
/// Una por Documento (índice único): como alta y renovación con archivo hacen nacer siempre un Documento
/// nuevo, el Documento identifica el acto, y la base no admite dos Gestiones del mismo acto.
/// </para>
/// </summary>
public class GestionAutomatica : EntidadBase
{
    private readonly List<PasoGestionAutomatica> _pasos = [];

    public Guid DocumentoId { get; private set; }
    public OrigenGestionAutomatica Origen { get; private set; }

    /// <summary>Quién hizo el alta o la renovación que la hizo nacer («Nació al renovar… · Laura Pérez»). Null si no se conoce.</summary>
    public Guid? NacidaPorUsuarioId { get; private set; }

    /// <summary>Instante (UTC) en que se cerró porque el Documento se renovó con ella abierta (§ 4.1 regla 4).</summary>
    public DateTime? SustituidaEnUtc { get; private set; }

    /// <summary>La Gestión que nació con la renovación, si nació alguna (sin pasos no nace, § 4.1 regla 2).</summary>
    public Guid? SustituidaPorGestionId { get; private set; }

    public IReadOnlyCollection<PasoGestionAutomatica> Pasos => _pasos.AsReadOnly();

    public bool EstaSustituida => SustituidaEnUtc is not null;

    private GestionAutomatica()
    {
    }

    private GestionAutomatica(Guid documentoId, OrigenGestionAutomatica origen, Guid? nacidaPorUsuarioId)
    {
        DocumentoId = documentoId;
        Origen = origen;
        NacidaPorUsuarioId = nacidaPorUsuarioId;
    }

    /// <summary>
    /// La Gestión de las acreditaciones <paramref name="acreditacionIds"/> del Documento, o null si no hay
    /// ninguna: sin pasos no hay Gestión (§ 4.1 regla 2). Las repetidas cuentan una vez.
    /// </summary>
    public static GestionAutomatica? Nacer(
        Guid documentoId, OrigenGestionAutomatica origen, IEnumerable<Guid> acreditacionIds, Guid? nacidaPorUsuarioId)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La gestión debe nacer de un documento.", nameof(documentoId));
        if (!Enum.IsDefined(origen))
            throw new ArgumentOutOfRangeException(nameof(origen), origen, "Origen de gestión desconocido.");

        var ids = acreditacionIds.Distinct().ToList();
        if (ids.Contains(Guid.Empty))
            throw new ArgumentException("Un paso debe tener una acreditación.", nameof(acreditacionIds));
        if (ids.Count == 0)
            return null;

        var gestion = new GestionAutomatica(documentoId, origen, nacidaPorUsuarioId);
        foreach (var id in ids)
            gestion._pasos.Add(new PasoGestionAutomatica(gestion.Id, id));
        return gestion;
    }

    /// <summary>
    /// Cierra la Gestión porque su Documento se renovó con ella abierta (§ 4.1 regla 4), con enlace a la que
    /// nació con la renovación (<paramref name="nuevaGestionId"/>, null si no nació ninguna). Idempotente: una
    /// sustituida conserva el instante y el enlace originales.
    /// </summary>
    public void Sustituir(Guid? nuevaGestionId, DateTime ahoraUtc)
    {
        if (ahoraUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("El instante de la sustitución debe estar en UTC.", nameof(ahoraUtc));
        if (nuevaGestionId == Id)
            throw new ArgumentException("Una gestión no se sustituye por sí misma.", nameof(nuevaGestionId));
        if (EstaSustituida)
            return;

        SustituidaEnUtc = ahoraUtc;
        SustituidaPorGestionId = nuevaGestionId;
    }

    /// <summary>
    /// Paso hecho (§ 4.2): su acreditación está subida y pendiente de validar, validada o no se pide en esa
    /// plataforma. «Pendiente de subir» y «Rechazado» son pasos por hacer.
    /// </summary>
    public static bool PasoHecho(EstadoAcreditacion estado) =>
        estado is EstadoAcreditacion.Subida or EstadoAcreditacion.Aceptada or EstadoAcreditacion.NoRequerida;

    /// <summary>
    /// Estado de la Gestión (§ 4.4) a partir del estado de las acreditaciones de los pasos que la persona ve
    /// (D-7). Sustituida manda sobre todo. Con algún paso por hacer, En curso; con todos hechos y alguno solo
    /// subido, Pendiente de validar (sigue abierta, D-15); con todos validados o «no lo pide», Cerrada.
    ///
    /// <para>
    /// <paramref name="documentoSustituido"/>: su Documento ya está en el historial (otro lo sustituyó). Entonces la
    /// Gestión no puede seguir abierta: si no está Cerrada, es Sustituida aunque nadie la marcara (§ 4.4, «se renovó
    /// el documento con la Gestión abierta»). La marca persistida (<paramref name="sustituida"/>) guarda el instante y
    /// el enlace cuando la renovación la vio abierta, pero no es la única vía: la decisión de marcarla se toma con
    /// estados de acreditación que otro guardado puede cambiar antes del commit sin tocar el Documento (un rechazo o
    /// una validación concurrentes), la ranura anterior de un relevo renueva sin conocer las Gestiones, y un rechazo
    /// posterior sobre una acreditación del Documento histórico reabriría una Gestión Cerrada. Derivarlo aquí hace que
    /// el estado visible dependa solo de lo confirmado, no del orden en que se confirmó.
    /// </para>
    /// </summary>
    public static EstadoGestionAutomatica ResolverEstado(
        bool sustituida, bool documentoSustituido, IEnumerable<EstadoAcreditacion> estadosDePasos)
    {
        if (sustituida)
            return EstadoGestionAutomatica.Sustituida;

        var estados = estadosDePasos.ToList();
        var porSusPasos = estados.Count == 0 || estados.Any(e => !PasoHecho(e))
            ? EstadoGestionAutomatica.EnCurso
            : estados.Any(e => e == EstadoAcreditacion.Subida)
                ? EstadoGestionAutomatica.PendienteDeValidar
                : EstadoGestionAutomatica.Cerrada;

        return documentoSustituido && porSusPasos != EstadoGestionAutomatica.Cerrada
            ? EstadoGestionAutomatica.Sustituida
            : porSusPasos;
    }
}

/// <summary>
/// Un paso de una <see cref="GestionAutomatica"/>: una acreditación en plataforma que atender dentro de ella.
/// Sin estado propio (§ 4.3): el estado es el de <see cref="AcreditacionId"/>. Inmutable: el conjunto de pasos
/// queda fijado al nacer la Gestión; lo que nazca después para el mismo Documento no entra en ella.
/// </summary>
public class PasoGestionAutomatica : EntidadConTenant
{
    public Guid GestionAutomaticaId { get; private set; }
    public Guid AcreditacionId { get; private set; }

    private PasoGestionAutomatica()
    {
    }

    internal PasoGestionAutomatica(Guid gestionAutomaticaId, Guid acreditacionId)
    {
        GestionAutomaticaId = gestionAutomaticaId;
        AcreditacionId = acreditacionId;
    }
}

/// <summary>Qué acto hizo nacer la Gestión automática. Persistido como entero: valores congelados.</summary>
public enum OrigenGestionAutomatica
{
    /// <summary>Se subió un Documento nuevo («Subir {tipo} de {titular}»).</summary>
    AltaDocumento = 0,

    /// <summary>Se renovó un Documento con archivo nuevo («Actualizar {tipo} de {titular}»).</summary>
    RenovacionDocumento = 1,
}

/// <summary>Estado derivado de la Gestión automática (§ 4.4). No se persiste.</summary>
public enum EstadoGestionAutomatica
{
    EnCurso = 0,
    PendienteDeValidar = 1,
    Cerrada = 2,
    Sustituida = 3,
}
