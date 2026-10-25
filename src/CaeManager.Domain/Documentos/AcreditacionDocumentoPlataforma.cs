using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Estado de un Documento frente a una plataforma destino concreta
/// (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (b), bloque "Acreditación
/// por plataforma destino" — alcance MVP1, decisión ya cerrada con el
/// propietario en Project-Hydra-Negocio/DECISION_LOG.md 2026-08-05). Motivación: un
/// mismo documento puede estar vigente en Hydra, aceptado en Dokify y
/// pendiente en Nalanda — sin esta entidad, Hydra no puede ni preguntar qué
/// falta en qué portal.
///
/// Referencia el <see cref="Centros.CanalGestionDocumental"/> concreto (el
/// acceso de un Centro a una plataforma), no directamente el catálogo
/// <c>ProveedorPlataformaCae</c>: con N accesos por Centro (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md
/// § 0.6), cada acceso es la unidad real de "dónde hay que subir esto".
///
/// Ya no es una entidad sin consumidor: el drill-down por plataforma
/// (<c>PlataformaTab.razor</c>) lee y escribe estos estados, y la extensión de
/// navegador sube documentos contra ellos. El comentario anterior decía que la
/// lógica de "Lote 2-D" estaba todavía sin construir; dejó de ser verdad.
/// </summary>
public class AcreditacionDocumentoPlataforma : EntidadBase, IAccionAuditoriaPropia
{
    private readonly List<RechazoAcreditacionDocumentoPlataforma> _historialRechazos = [];

    public Guid DocumentoId { get; private set; }
    public Guid CanalGestionDocumentalId { get; private set; }
    public EstadoAcreditacion Estado { get; private set; }

    /// <summary>
    /// Persistido en dos columnas porque son dos columnas en la base de datos,
    /// pero nunca se leen sueltas: <see cref="Vigencia"/> es la única puerta, y
    /// se niega a devolver una combinación incoherente.
    /// </summary>
    public EstadoVigenciaEnPlataforma EstadoVigencia { get; private set; }

    public DateOnly? FechaVencimientoEnPlataforma { get; private set; }

    /// <summary>
    /// Hasta cuándo vale este documento <b>en esta plataforma</b>, que no tiene
    /// por qué ser hasta cuándo vale en TALVEG. Nace
    /// <see cref="EstadoVigenciaEnPlataforma.SinConfirmar"/>: que nadie lo haya
    /// anotado todavía es un hecho distinto de que no caduque, y el semáforo del
    /// Centro necesita poder distinguirlos.
    /// </summary>
    public VigenciaEnPlataforma Vigencia =>
        VigenciaEnPlataforma.Rehidratar(EstadoVigencia, FechaVencimientoEnPlataforma);

    /// <summary>Nunca se sobreescribe ni se borra — invariante explícita del plan. El rechazo vigente (si Estado es Rechazada) es siempre el último de la lista.</summary>
    public IReadOnlyList<RechazoAcreditacionDocumentoPlataforma> HistorialRechazos => _historialRechazos.AsReadOnly();

    private readonly List<CambioEstadoAcreditacionPlataforma> _cambiosEstado = [];

    /// <summary>Cambios de estado anotados a mano (<see cref="CambiarEstadoAMano"/>) desde Centro 360 o con «Lo he enviado» de la extensión, deshechos incluidos.</summary>
    public IReadOnlyList<CambioEstadoAcreditacionPlataforma> CambiosEstado => _cambiosEstado.AsReadOnly();

    /// <summary>
    /// El cambio a mano que dejó el estado actual, si lo último que le pasó al estado fue ese cambio. Cualquier otra
    /// vía que toque el estado (panel «Gestionar», renovación del Documento, «Deshacer») lo vacía: así la
    /// anotación de Centro 360 nunca describe otro estado ni otra versión del documento, y «Deshacer» solo deshace el
    /// cambio que sigue en pie. Confirmar la vigencia no cambia el estado y no lo vacía.
    /// </summary>
    public Guid? UltimoCambioEstadoAManoId { get; private set; }

    private AcreditacionDocumentoPlataforma()
    {
    }

    public AcreditacionDocumentoPlataforma(Guid documentoId, Guid canalGestionDocumentalId)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La acreditación debe pertenecer a un documento.", nameof(documentoId));
        if (canalGestionDocumentalId == Guid.Empty)
            throw new ArgumentException("La acreditación debe referenciar un acceso de gestión documental.", nameof(canalGestionDocumentalId));

        DocumentoId = documentoId;
        CanalGestionDocumentalId = canalGestionDocumentalId;
        Estado = EstadoAcreditacion.PendienteDeSubir;
    }

    /// <summary>
    /// Enviada a la plataforma y sin validar. Si venía validada, la vigencia que se confirmó entonces ya no respalda nada
    /// (la plataforma la está revisando otra vez): vuelve a sin confirmar, igual que al rechazar o devolver a pendiente.
    /// </summary>
    public void MarcarSubida()
    {
        if (Estado == EstadoAcreditacion.Aceptada)
            AnotarVigencia(VigenciaEnPlataforma.SinConfirmar);
        Estado = EstadoAcreditacion.Subida;
        UltimoCambioEstadoAManoId = null;
    }

    /// <summary>
    /// La plataforma aceptó el documento, y ese es justo el momento en que el
    /// Gestor CAE sabe hasta cuándo vale allí: por eso la vigencia se confirma
    /// aquí y no en una pantalla aparte que nadie volvería a abrir.
    ///
    /// <para>
    /// Puede no saberla, y entonces pasa
    /// <see cref="VigenciaEnPlataforma.SinConfirmar"/>. Eso queda registrado
    /// como ignorancia deliberada, que es lo que es — no como «no caduca».
    /// </para>
    /// </summary>
    public void MarcarAceptada(VigenciaEnPlataforma vigencia)
    {
        Estado = EstadoAcreditacion.Aceptada;
        UltimoCambioEstadoAManoId = null;
        AnotarVigencia(vigencia);
    }

    /// <summary>
    /// Corregir la vigencia sin volver a tocar el estado de la acreditación: el
    /// Gestor entra en la plataforma más veces que una, y la primera vez puede
    /// no haber mirado la fecha.
    /// </summary>
    public void ConfirmarVigencia(VigenciaEnPlataforma vigencia) => AnotarVigencia(vigencia);

    private void AnotarVigencia(VigenciaEnPlataforma vigencia)
    {
        EstadoVigencia = vigencia.Estado;
        FechaVencimientoEnPlataforma = vigencia.FechaVencimiento;
    }

    /// <summary>
    /// ¿Se puede devolver la acreditación a este estado y vigencia como «Deshacer» de una anotación?
    /// El valor previo llega del llamador, así que se valida en vez de creerlo:
    /// <list type="bullet">
    /// <item>El estado solo cambia si hoy es Aceptada, que es lo único que una anotación deja
    /// atrás; si no, solo se corrige la vigencia y el estado previo debe ser el actual.</item>
    /// <item>Rechazada exige un rechazo en el historial y vigencia Sin confirmar, el valor que
    /// fija <see cref="Rechazar"/>.</item>
    /// <item>La vigencia debe ser interpretable (<see cref="VigenciaEnPlataforma.Rehidratar"/>).</item>
    /// </list>
    /// </summary>
    public bool PuedeDeshacerAnotacion(EstadoAcreditacion estadoPrevio, VigenciaEnPlataforma vigenciaPrevia)
    {
        if (!Enum.IsDefined(estadoPrevio)) return false;
        if (estadoPrevio != Estado && Estado != EstadoAcreditacion.Aceptada) return false;
        if (estadoPrevio == EstadoAcreditacion.Rechazada
            && (_historialRechazos.Count == 0 || !vigenciaPrevia.EstaSinConfirmar))
            return false;

        try
        {
            _ = VigenciaEnPlataforma.Rehidratar(vigenciaPrevia.Estado, vigenciaPrevia.FechaVencimiento);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// «Deshacer» de una anotación: devuelve el estado y la vigencia EXACTOS que había
    /// antes (incluido «Sin confirmar», que no es lo mismo que «anotada»). No toca
    /// el historial de rechazos, que nunca se sobrescribe. La fila de auditoría
    /// sale como <see cref="RegistroAuditoria.AccionRestaurado"/>. Se llama antes a
    /// <see cref="PuedeDeshacerAnotacion"/>; aquí se vuelve a exigir.
    /// No se llama <c>Restaurar</c> porque ese nombre es el de deshacer un borrado lógico.
    /// </summary>
    public void DeshacerAnotacion(EstadoAcreditacion estadoPrevio, VigenciaEnPlataforma vigenciaPrevia)
    {
        if (!PuedeDeshacerAnotacion(estadoPrevio, vigenciaPrevia))
            throw new InvalidOperationException("Esta acreditación no puede devolverse a ese estado y vigencia.");

        Estado = estadoPrevio;
        UltimoCambioEstadoAManoId = null;
        AnotarVigencia(vigenciaPrevia);
        _accionAuditoria = RegistroAuditoria.AccionRestaurado;
    }

    // Solo campo: sin propiedad, EF no lo mapea y nunca llega a la base de datos.
    private string? _accionAuditoria;

    string? IAccionAuditoriaPropia.ConsumirAccionAuditoria()
    {
        var accion = _accionAuditoria;
        _accionAuditoria = null;
        return accion;
    }

    public void MarcarNoRequerida()
    {
        Estado = EstadoAcreditacion.NoRequerida;
        UltimoCambioEstadoAManoId = null;
    }

    /// <summary>
    /// «Pendiente de subir» del panel «Gestionar acreditación en plataforma»: el Gestor CAE ha mirado la
    /// plataforma y la versión vigente en TALVEG no está allí (se dio por subida o validada y no lo estaba, o la
    /// plataforma sigue mostrando la anterior). En ESTE panel, solo desde Subida o Aceptada: una Rechazada sale con
    /// una versión corregida (<see cref="ReiniciarPorRenovacionDocumento"/>), y una NoRequerida no es trabajo de nadie.
    /// La vigencia confirmada se pierde por el mismo motivo que al renovar.
    /// <para>
    /// Desde el bloque 3 de la sesión del 21-10, «Cambiar» de Centro 360 (<see cref="CambiarEstadoAMano"/>) sí lleva
    /// una Rechazada a Pendiente de subir: el Gestor CAE anota lo que ve en la plataforma del Cliente, también cuando
    /// corrige un rechazo marcado por error. Esta regla no limita esa vía.
    /// </para>
    /// </summary>
    public bool PuedeDevolverAPendienteDeSubir =>
        Estado is EstadoAcreditacion.Subida or EstadoAcreditacion.Aceptada;

    public void DevolverAPendienteDeSubir()
    {
        if (!PuedeDevolverAPendienteDeSubir)
            throw new InvalidOperationException("Solo una acreditación subida o validada vuelve a quedar pendiente de subir.");

        Estado = EstadoAcreditacion.PendienteDeSubir;
        UltimoCambioEstadoAManoId = null;
        AnotarVigencia(VigenciaEnPlataforma.SinConfirmar);
    }

    /// <summary>
    /// Causa tipificada + motivo literal de la plataforma son siempre
    /// obligatorios al rechazar — sin excepción, ni siquiera desde código
    /// (RechazoAcreditacionDocumentoPlataforma ya valida el motivo). El
    /// rechazo se construye ANTES de tocar Estado a propósito: si el motivo
    /// no valida, la entidad tiene que quedar exactamente como estaba, no a
    /// medio camino (Rechazada sin ningún rechazo en el historial).
    /// </summary>
    public void Rechazar(CausaRechazoAcreditacion causa, string motivoLiteral, DateTime fechaUtc)
    {
        var rechazo = new RechazoAcreditacionDocumentoPlataforma(Id, causa, motivoLiteral, fechaUtc);
        Estado = EstadoAcreditacion.Rechazada;
        UltimoCambioEstadoAManoId = null;
        // Lo que se hubiera confirmado se refería a un documento que esta
        // plataforma ya no acepta: conservarlo dejaría una fecha de vigencia
        // respaldando algo que no está acreditado.
        AnotarVigencia(VigenciaEnPlataforma.SinConfirmar);
        _historialRechazos.Add(rechazo);
    }

    /// <summary>
    /// Estados que el Gestor CAE puede anotar a mano desde Centro 360 (bloque 3 de la sesión del 21-10): los cuatro de la
    /// plataforma del Cliente. «No requerida» no es algo que diga la plataforma, y no se anota desde ahí.
    /// </summary>
    public static bool EsEstadoAnotableAMano(EstadoAcreditacion estado) =>
        estado is EstadoAcreditacion.PendienteDeSubir or EstadoAcreditacion.Subida
            or EstadoAcreditacion.Aceptada or EstadoAcreditacion.Rechazada;

    /// <summary>
    /// «Marcar subido», «Marcar validado» y «Cambiar» de Centro 360: el Gestor CAE anota el estado que ve en la
    /// plataforma, con la fecha, el motivo (obligatorio si es un rechazo), una nota opcional, quién y cuándo. A
    /// diferencia de <see cref="DevolverAPendienteDeSubir"/>, desde aquí cualquier estado lleva a cualquiera de los
    /// cuatro: el modal sirve también para corregir un estado marcado por error.
    /// <list type="bullet">
    /// <item>Rechazada añade el rechazo al historial (causa <see cref="CausaRechazoAcreditacion.Otro"/>: el modal pide
    /// el motivo literal, no la clasificación).</item>
    /// <item>La vigencia confirmada se pierde salvo al volver a anotar Validado sobre algo que ya lo estaba: la misma
    /// regla que <see cref="MarcarSubida"/>, <see cref="Rechazar"/> y <see cref="DevolverAPendienteDeSubir"/>.</item>
    /// </list>
    /// Devuelve el cambio, que es lo que «Deshacer» identifica. <paramref name="origen"/> dice desde dónde se anotó
    /// (Centro 360, o «Lo he enviado» de la extensión con <see cref="ConfirmarEnvioDesdeExtension"/>).
    /// </summary>
    public CambioEstadoAcreditacionPlataforma CambiarEstadoAMano(
        EstadoAcreditacion destino, DateOnly fecha, string? motivo, string? nota, Guid? usuarioId, DateTime momentoUtc,
        OrigenCambioEstadoAcreditacion origen = OrigenCambioEstadoAcreditacion.Centro360)
    {
        if (!EsEstadoAnotableAMano(destino))
            throw new ArgumentException("Ese estado no se anota a mano en la plataforma.", nameof(destino));

        // Se construye ANTES de tocar nada: si el motivo o la nota no validan, la acreditación queda como estaba.
        var cambio = new CambioEstadoAcreditacionPlataforma(
            Id, Estado, Vigencia, destino, fecha, motivo, nota, usuarioId, momentoUtc, origen);

        switch (destino)
        {
            case EstadoAcreditacion.Rechazada:
                Rechazar(CausaRechazoAcreditacion.Otro, cambio.Motivo!, momentoUtc);
                break;
            case EstadoAcreditacion.Subida:
                MarcarSubida();
                break;
            case EstadoAcreditacion.Aceptada:
                MarcarAceptada(Estado == EstadoAcreditacion.Aceptada ? Vigencia : VigenciaEnPlataforma.SinConfirmar);
                break;
            default:
                Estado = EstadoAcreditacion.PendienteDeSubir;
                AnotarVigencia(VigenciaEnPlataforma.SinConfirmar);
                break;
        }

        _cambiosEstado.Add(cambio);
        UltimoCambioEstadoAManoId = cambio.Id;
        // El cambio vive en otra tabla y el estado puede no variar (Validado sobre Validado): sin tocar la raíz, el
        // token de concurrencia no se renovaría y el «Deshacer» de un cambio anterior no vería que hubo otro después.
        RenovarVersion();
        return cambio;
    }

    /// <summary>
    /// «Lo he enviado» de la extensión de navegador: el Gestor CAE confirma que envió en la plataforma el documento que la
    /// extensión puso en el formulario. Queda Subida (Pendiente de validar) con un cambio de origen
    /// <see cref="OrigenCambioEstadoAcreditacion.Extension"/>, que se ve en el Historial del Centro y se deshace igual que
    /// uno de Centro 360. Poner el archivo en el formulario no cambia nada: solo esta confirmación. Si la plataforma ya
    /// rechazó esta versión, el llamador lo impide antes (<c>ReglaSubidaAcreditacion</c>).
    /// </summary>
    public CambioEstadoAcreditacionPlataforma ConfirmarEnvioDesdeExtension(DateOnly fecha, Guid? usuarioId, DateTime momentoUtc) =>
        CambiarEstadoAMano(EstadoAcreditacion.Subida, fecha, motivo: null, nota: null, usuarioId, momentoUtc,
            OrigenCambioEstadoAcreditacion.Extension);

    /// <summary>
    /// ¿Se puede deshacer este cambio? Solo si es el que dejó el estado actual (<see cref="UltimoCambioEstadoAManoId"/>):
    /// si después la cambió otra vía (otra pestaña, la extensión, una renovación) o se anotó otro cambio, deshacer
    /// pisaría ese cambio. El llamador comprueba además la versión que vio (concurrencia optimista).
    /// </summary>
    public bool PuedeDeshacerCambioEstado(Guid cambioId) =>
        UltimoCambioEstadoAManoId == cambioId
        && _cambiosEstado.Any(c => c.Id == cambioId && !c.EstaDeshecho && c.Estado == Estado);

    /// <summary>
    /// «Deshacer» del aviso que sale tras «Marcar subido» o «Marcar validado»: devuelve el estado y la vigencia que
    /// guardó el propio cambio (nunca los que mande el llamador) y lo marca deshecho, sin borrarlo. El historial de
    /// rechazos no se toca. La fila de auditoría sale como <see cref="RegistroAuditoria.AccionRestaurado"/>.
    /// </summary>
    public void DeshacerCambioEstado(Guid cambioId, DateTime momentoUtc)
    {
        if (!PuedeDeshacerCambioEstado(cambioId))
            throw new InvalidOperationException("Este cambio de estado ya no se puede deshacer.");

        var cambio = _cambiosEstado.Single(c => c.Id == cambioId);
        Estado = cambio.EstadoAnterior;
        UltimoCambioEstadoAManoId = null;
        AnotarVigencia(cambio.VigenciaAnterior);
        cambio.MarcarDeshecho(momentoUtc);
        _accionAuditoria = RegistroAuditoria.AccionRestaurado;
    }

    /// <summary>
    /// Invariante del plan: renovar el Documento reinicia todas sus
    /// acreditaciones a Pendiente de subir — la versión anterior del
    /// documento ya no es la que hay que validar en ningún portal. El
    /// historial de rechazos no se toca: sigue siendo un hecho pasado real.
    /// </summary>
    public void ReiniciarPorRenovacionDocumento()
    {
        Estado = EstadoAcreditacion.PendienteDeSubir;
        UltimoCambioEstadoAManoId = null;
        // La vigencia confirmada era la del documento anterior. Heredarla daría
        // por acreditado en la plataforma un papel que todavía no se ha subido.
        AnotarVigencia(VigenciaEnPlataforma.SinConfirmar);
    }
}
