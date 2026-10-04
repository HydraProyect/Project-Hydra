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

    public void MarcarSubida() => Estado = EstadoAcreditacion.Subida;

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

    public void MarcarNoRequerida() => Estado = EstadoAcreditacion.NoRequerida;

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
        // Lo que se hubiera confirmado se refería a un documento que esta
        // plataforma ya no acepta: conservarlo dejaría una fecha de vigencia
        // respaldando algo que no está acreditado.
        AnotarVigencia(VigenciaEnPlataforma.SinConfirmar);
        _historialRechazos.Add(rechazo);
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
        // La vigencia confirmada era la del documento anterior. Heredarla daría
        // por acreditado en la plataforma un papel que todavía no se ha subido.
        AnotarVigencia(VigenciaEnPlataforma.SinConfirmar);
    }
}
