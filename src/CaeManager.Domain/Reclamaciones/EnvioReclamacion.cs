using System.Security.Cryptography;
using System.Text;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.Reclamaciones;

/// <summary>
/// Registro previo de un envío de reclamación: se guarda ANTES de que salga el primer correo y apunta, a medida que
/// salen, a quién le llegó. Es lo que hace idempotente el envío: el correo a un tercero no se puede deshacer, así que
/// si lo que falla es el guardado posterior (la base cae, el circuito se corta), el reintento de la MISMA reclamación
/// encuentra este registro y no vuelve a mandar el correo a quien ya lo recibió; solo completa lo que faltaba.
///
/// <para>
/// No es la reclamación ni cuenta como tal: el historial append-only sigue siendo <see cref="ReclamacionDocumental"/>,
/// que solo se crea cuando al menos un destinatario recibió el correo. Un envío que no llegó a nadie deja este registro
/// pendiente y sin entregas, y no aparece en ningún lector de reclamaciones.
/// </para>
///
/// <para>
/// «La misma reclamación» la decide <see cref="Huella"/> —titular, documentos, lo que falta y destinatarios—, no un
/// identificador que traiga la pantalla: así el reintento es idempotente aunque se repita desde otra pantalla o tras
/// perder el circuito. Solo se reutiliza un registro pendiente (sin <see cref="ReclamacionDocumentalId"/>), del mismo
/// usuario y dentro de la ventana de reintento; una reclamación ya registrada que se vuelve a enviar es otra reclamación.
/// </para>
///
/// Vive en el Tenant propietario de la reclamación, con la misma RLS que <c>ReclamacionesDocumentales</c>.
///
/// <para>
/// <see cref="IVersionable"/>: dos circuitos que reanudan a la vez el mismo envío pendiente reescribirían la lista
/// entera de entregas (gana el último) y el segundo cierre pisaría la reclamación del primero. Con el token, el segundo
/// guardado falla y se informa como registro fallido, en vez de perder una entrega o dejar una reclamación huérfana.
/// El token no evita el correo duplicado: cuando el segundo guardado falla, su correo ya ha salido. Tampoco hay índice
/// único sobre la huella del registro sin cerrar, así que dos pestañas del mismo usuario que envían a la vez la misma
/// reclamación pueden mandarla dos veces (riesgo residual aceptado: la pantalla ya bloquea el doble clic).
/// </para>
/// </summary>
public class EnvioReclamacion : EntidadConTenant, IVersionable
{
    public const int LongitudHuella = 64;

    /// <inheritdoc cref="IVersionable" />
    public Guid Version { get; private set; } = Guid.NewGuid();

    private List<string> _entregados = [];
    private List<string> _entregadosEnCopia = [];

    /// <summary>SHA-256 en hexadecimal de la forma canónica de la reclamación (<see cref="CalcularHuella"/>).</summary>
    public string Huella { get; private set; } = string.Empty;

    /// <summary>Quién la envía: el reintento de otro usuario no completa el envío de este.</summary>
    public Guid UsuarioId { get; private set; }

    public DateTime IniciadoEnUtc { get; private set; }

    /// <summary>Hilo creado al enviar por el buzón conectado. Con él, el correo ya salió y no se vuelve a mandar.</summary>
    public Guid? ConversacionId { get; private set; }

    /// <summary>La reclamación registrada. Con valor, el envío está completo y no se reutiliza nunca.</summary>
    public Guid? ReclamacionDocumentalId { get; private set; }

    /// <summary>Destinatarios a los que ya les llegó el correo por SMTP.</summary>
    public IReadOnlyList<string> Entregados => _entregados;

    /// <summary>Direcciones en copia a las que ya les llegó su copia por SMTP.</summary>
    public IReadOnlyList<string> EntregadosEnCopia => _entregadosEnCopia;

    public bool EstaRegistrado => ReclamacionDocumentalId is not null;

    private EnvioReclamacion()
    {
    }

    public static EnvioReclamacion Iniciar(Guid usuarioId, string huella, DateTime iniciadoEnUtc)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El envío necesita a quien lo emite.", nameof(usuarioId));
        if (huella.Length != LongitudHuella)
            throw new ArgumentException("La huella no tiene la forma de CalcularHuella.", nameof(huella));

        return new EnvioReclamacion { UsuarioId = usuarioId, Huella = huella, IniciadoEnUtc = iniciadoEnUtc };
    }

    public bool YaEntregadoA(string direccion, bool enCopia) =>
        (enCopia ? _entregadosEnCopia : _entregados).Contains(direccion, StringComparer.OrdinalIgnoreCase);

    /// <summary>Se llama justo después de que el correo salga, y se guarda en el acto: es lo que el reintento consulta.</summary>
    public void RegistrarEntrega(string direccion, bool enCopia)
    {
        if (YaEntregadoA(direccion, enCopia))
            return;

        // Lista nueva y no Add: el cambio de la colección primitiva se detecta sin depender de la comparación de EF.
        if (enCopia)
            _entregadosEnCopia = [.. _entregadosEnCopia, direccion];
        else
            _entregados = [.. _entregados, direccion];
    }

    public void RegistrarConversacion(Guid conversacionId) => ConversacionId ??= conversacionId;

    public void MarcarRegistrado(Guid reclamacionDocumentalId)
    {
        if (ReclamacionDocumentalId is not null && ReclamacionDocumentalId != reclamacionDocumentalId)
            throw new InvalidOperationException("El envío ya está registrado con otra reclamación.");

        ReclamacionDocumentalId = reclamacionDocumentalId;
    }

    /// <summary>
    /// Forma canónica de una reclamación: el orden en que lleguen los documentos o los destinatarios, y las mayúsculas
    /// de las direcciones, no la convierten en otra. El asunto y el cuerpo no entran: se derivan de lo que sí entra.
    /// </summary>
    public static string CalcularHuella(
        Guid titularId,
        AmbitoAplicacion ambito,
        bool documentosDeTrabajadores,
        IEnumerable<Guid> documentoIds,
        IEnumerable<DocumentoQueFaltaPedido> documentosQueFaltan,
        IEnumerable<string> destinatarios,
        IEnumerable<string> enCopia)
    {
        static string Direcciones(IEnumerable<string> direcciones) =>
            string.Join(",", direcciones.Select(d => d.Trim().ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));

        var canonica = string.Join("|",
            titularId.ToString("N"),
            ambito.ToString(),
            documentosDeTrabajadores ? "1" : "0",
            string.Join(",", documentoIds.Distinct().Select(id => id.ToString("N")).Order(StringComparer.Ordinal)),
            string.Join(",", documentosQueFaltan.Distinct()
                .Select(f => $"{f.TipoDocumentoId:N}:{f.TrabajadorId?.ToString("N")}").Order(StringComparer.Ordinal)),
            Direcciones(destinatarios),
            Direcciones(enCopia));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonica))).ToLowerInvariant();
    }
}
