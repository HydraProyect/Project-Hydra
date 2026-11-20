using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Integraciones.ConectoresPlataformaCae;

/// <summary>
/// Contrato común de una conexión por API oficial con una plataforma CAE de terceros. Ni Domain ni
/// Application conocen ninguna plataforma: cada una es una implementación en Infrastructure, elegida
/// por <see cref="CodigoProveedor"/> contra <c>ProveedorPlataformaCae.Codigo</c>. Hoy hay dos registradas,
/// la de CTAIMACAE.net y la de dokify (de solo lectura), y ninguna llama: aún no existe contrato de API con
/// ninguna plataforma, el interruptor de cada proveedor nace apagado y a los conectores todavía no les
/// llegan todos los datos que sus API exigen.
///
/// <para>
/// Solo lo llama <see cref="IOperacionesConectorPlataformaCae"/>, que ya ha hecho todas las
/// comprobaciones. Un conector no devuelve texto del tercero: un fallo es un
/// <see cref="MotivoUsoConector"/>.
/// </para>
/// </summary>
public interface IConectorPlataformaCae
{
    /// <summary>Debe coincidir con <c>ProveedorPlataformaCae.Codigo</c>.</summary>
    string CodigoProveedor { get; }

    /// <summary>
    /// Si el conector hace esta operación con su plataforma. La puerta lo pregunta antes de abrir el uso: una operación
    /// que el conector no hace se deniega como <c>SinConector</c>, sin llamada y sin uso iniciado, igual que una plataforma
    /// sin conector. Por defecto, todas.
    /// </summary>
    bool AdmiteOperacion(OperacionConectorPlataformaCae operacion) => true;

    Task<ResultadoConector<EstadoDocumentoEnPlataforma>> LeerEstadoDocumentoAsync(
        CredencialApiConector credencial, SolicitudLecturaEstado solicitud, CancellationToken cancellationToken);

    Task<ResultadoConector<ReferenciaEnPlataforma>> DarDeAltaTrabajadorAsync(
        CredencialApiConector credencial, SolicitudAltaTrabajador solicitud, CancellationToken cancellationToken);

    Task<ResultadoConector<ReferenciaEnPlataforma>> SubirDocumentoAsync(
        CredencialApiConector credencial, SolicitudSubidaDocumento solicitud, CancellationToken cancellationToken);
}

/// <summary>
/// La credencial en memoria, de camino al conector. Una clase y no un <c>string</c> para que acabar en
/// un log o en una interpolación escriba <c>***</c> (OWASP ASVS 4.0.3 V7.1.1).
/// </summary>
public sealed class CredencialApiConector(string valor)
{
    public string Revelar() => valor;

    public override string ToString() => "***";
}

/// <summary>El estado tal como lo declara la plataforma, traducido por el conector. <c>Desconocido</c> si no sabe traducirlo.</summary>
public enum EstadoDocumentoEnPlataforma
{
    Desconocido,
    PendienteDeValidar,
    Validado,
    Rechazado,
}

/// <summary>El identificador de un Trabajador o de un Documento en la plataforma.</summary>
public sealed record ReferenciaEnPlataforma(string Valor);

public sealed record SolicitudLecturaEstado(Guid AcreditacionId, Guid DocumentoId);

/// <summary>Solo lo que hace falta para el alta (OWASP ASVS 4.0.3 V7.1.2): <see cref="ToString"/> no los escribe.</summary>
public sealed record SolicitudAltaTrabajador(Guid TrabajadorId, string Nombre, string Apellidos, string Dni, string ClaveIdempotencia)
{
    public override string ToString() => nameof(SolicitudAltaTrabajador);
}

/// <summary><see cref="Contenido"/> lo abre y lo cierra quien llama al conector.</summary>
public sealed record SolicitudSubidaDocumento(
    Guid AcreditacionId, Guid DocumentoId, Stream Contenido, string ClaveIdempotencia);

public sealed class ResultadoConector<T>
{
    private readonly T? _valor;

    private ResultadoConector(T? valor, MotivoUsoConector? fallo)
    {
        _valor = valor;
        Fallo = fallo;
    }

    /// <summary>Null si fue bien.</summary>
    public MotivoUsoConector? Fallo { get; }

    public T Valor => Fallo is null ? _valor! : throw new InvalidOperationException("Un fallo del conector no tiene valor.");

    public static ResultadoConector<T> Exito(T valor) => new(valor, null);

    public static ResultadoConector<T> Falla(MotivoUsoConector motivo) => new(default, motivo);
}
