using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.Reclamaciones;

/// <summary>
/// Registro de un lote de reclamación documental enviado al titular de la
/// documentación — agrupa varios Documentos que vencen en la misma ventana
/// (1 a 3 meses) en un único correo, en vez de uno por Documento (ver el
/// hilo de trabajo que originó esta feature: no se pueden mandar 30 correos
/// por cada vencimiento suelto). Append-only: una vez enviado no se edita ni
/// se borra, es el historial de qué se reclamó y cuándo.
///
/// UltimaReclamacionFechaUtc (ver ObtenerLoteReclamacionQuery) se calcula
/// para que el Gestor CAE decida informado, no para bloquear: no existe ni
/// debe existir una guarda temporal que impida reclamar de nuevo el mismo
/// lote — reclamar otra vez puede ser una acción operativa legítima (DEC-17,
/// 2026-09-02). Un control técnico contra doble envío accidental o
/// concurrencia, si algún día se añade, no es esta regla ni la sustituye.
///
/// El titular es polimórfico y excluyente, mismo patrón que
/// <see cref="Documento"/> (una y solo una ancla informada):
/// <list type="bullet">
/// <item><see cref="ClienteId"/> — reclamación de documentos de Trabajador,
/// cuyo titular es la Empresa contraparte dueña del Centro donde están
/// asignados (ver ObtenerLoteReclamacionQuery);</item>
/// <item><see cref="EmpresaId"/> — reclamación de documentos de empresa,
/// cuyo titular es la propia Empresa contraparte a la que pertenecen (DEC-7,
/// caso literal "todos los documentos de empresa de una empresa").</item>
/// </list>
/// Las dos anclas repuntan contra <c>Empresas</c> (ADR-011: Empresa es una
/// entidad única sin rol; "Cliente" y "proveedora" son posiciones en una
/// RelacionEmpresarial, no tipos distintos) — se guardan por separado, y no
/// como un par (TitularId, Ambito), porque una misma Empresa puede ocupar
/// las dos posiciones a la vez y colapsarlas haría indistinguibles dos
/// reclamaciones con significado y alcance distintos.
///
/// MVP1 es solo vista previa + envío manual por el Gestor CAE — no hay
/// todavía un job en segundo plano que envíe solo (decisión explícita del
/// usuario, 2026-08-08).
/// </summary>
public class ReclamacionDocumental : EntidadBase
{
    public const int LongitudMaximaDestinatarioEmail = 500;

    private readonly List<ReclamacionDocumentalDocumento> _documentos = [];

    /// <summary>Empresa contraparte en posición de cliente, cuando la reclamación agrupa documentos de Trabajador. Null en una reclamación de ámbito Empresa.</summary>
    public Guid? ClienteId { get; private set; }

    /// <summary>Empresa contraparte titular de los documentos de empresa reclamados. Null en una reclamación de ámbito Trabajador.</summary>
    public Guid? EmpresaId { get; private set; }

    public Guid EnviadoPorUsuarioId { get; private set; }

    /// <summary>
    /// Snapshot del/los email(s) usados en el envío (varios destinatarios se
    /// unen con "; ") — no una referencia viva al usuario de portal, para que
    /// el historial no cambie de significado si ese contacto cambia de email
    /// después.
    /// </summary>
    public string DestinatarioEmail { get; private set; } = string.Empty;

    public DateTime FechaEnvioUtc { get; private set; }

    /// <summary>
    /// Identificador de la conversación generada en Comunicaciones si se envió por un buzón conectado (P3-33, Fase A). Null si se envió por IEmailService (sin buzón) o en reclamaciones históricas.
    /// </summary>
    public Guid? ConversacionId { get; private set; }

    public IReadOnlyList<ReclamacionDocumentalDocumento> Documentos => _documentos.AsReadOnly();

    /// <summary>
    /// Qué ancla está informada — derivado, nunca persistido: el ámbito ES
    /// cuál de las dos columnas tiene valor, y guardarlo aparte permitiría
    /// que se contradijeran.
    /// </summary>
    public AmbitoAplicacion AmbitoTitular =>
        ClienteId is not null ? AmbitoAplicacion.Cliente : AmbitoAplicacion.Empresa;

    /// <summary>La Empresa a la que se le reclamó, sea cual sea la posición que ocupa — para los lectores que solo necesitan "a quién".</summary>
    public Guid TitularId => ClienteId ?? EmpresaId!.Value;

    private ReclamacionDocumental()
    {
    }

    private ReclamacionDocumental(
        Guid? clienteId, Guid? empresaId, Guid enviadoPorUsuarioId, string destinatarioEmail, DateTime fechaEnvioUtc,
        IEnumerable<Guid> documentoIds, Guid? conversacionId)
    {
        if (enviadoPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La reclamación debe registrar quién la envió.", nameof(enviadoPorUsuarioId));
        if (string.IsNullOrWhiteSpace(destinatarioEmail))
            throw new ArgumentException("La reclamación debe tener al menos un destinatario.", nameof(destinatarioEmail));

        var destinatarioNormalizado = destinatarioEmail.Trim();
        if (destinatarioNormalizado.Length > LongitudMaximaDestinatarioEmail)
            throw new ArgumentException(
                $"El destinatario no puede superar {LongitudMaximaDestinatarioEmail} caracteres.", nameof(destinatarioEmail));

        var idsUnicos = documentoIds.Distinct().ToList();
        if (idsUnicos.Count == 0)
            throw new ArgumentException("La reclamación debe incluir al menos un documento.", nameof(documentoIds));

        ClienteId = clienteId;
        EmpresaId = empresaId;
        EnviadoPorUsuarioId = enviadoPorUsuarioId;
        DestinatarioEmail = destinatarioNormalizado;
        FechaEnvioUtc = fechaEnvioUtc;
        ConversacionId = conversacionId;

        foreach (var documentoId in idsUnicos)
            _documentos.Add(new ReclamacionDocumentalDocumento(Id, documentoId));
    }

    /// <summary>Reclamación de documentos de Trabajador, dirigida a la Empresa contraparte en posición de cliente.</summary>
    public static ReclamacionDocumental ParaCliente(
        Guid clienteId, Guid enviadoPorUsuarioId, string destinatarioEmail, DateTime fechaEnvioUtc,
        IEnumerable<Guid> documentoIds, Guid? conversacionId = null)
    {
        if (clienteId == Guid.Empty)
            throw new ArgumentException("La reclamación debe pertenecer a un cliente.", nameof(clienteId));

        return new ReclamacionDocumental(
            clienteId, null, enviadoPorUsuarioId, destinatarioEmail, fechaEnvioUtc, documentoIds, conversacionId);
    }

    /// <summary>Reclamación de documentos de empresa, dirigida a la Empresa contraparte titular de esos documentos (DEC-7).</summary>
    public static ReclamacionDocumental ParaEmpresa(
        Guid empresaId, Guid enviadoPorUsuarioId, string destinatarioEmail, DateTime fechaEnvioUtc,
        IEnumerable<Guid> documentoIds, Guid? conversacionId = null)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("La reclamación debe pertenecer a una empresa.", nameof(empresaId));

        return new ReclamacionDocumental(
            null, empresaId, enviadoPorUsuarioId, destinatarioEmail, fechaEnvioUtc, documentoIds, conversacionId);
    }
}
