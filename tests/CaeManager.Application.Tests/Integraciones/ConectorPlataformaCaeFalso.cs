using CaeManager.Application.Integraciones.ConectoresPlataformaCae;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>Conector solo de tests: no corresponde a ninguna plataforma real ni imita la API de ninguna.</summary>
public class ConectorPlataformaCaeFalso(string codigoProveedor = ConectorPlataformaCaeFalso.Codigo) : IConectorPlataformaCae
{
    public const string Codigo = "plataforma-de-prueba";

    public string CodigoProveedor { get; } = codigoProveedor;

    public int Llamadas { get; private set; }
    public string? UltimaCredencial { get; private set; }
    public object? UltimaSolicitud { get; private set; }
    public byte[]? UltimoContenido { get; private set; }

    /// <summary>Si se informa, cada operación falla con este motivo.</summary>
    public MotivoUsoConectorFalso? Fallo { get; set; }

    public Exception? Excepcion { get; set; }

    /// <summary>Se ejecuta al entrar en la llamada, para mirar qué estaba ya guardado.</summary>
    public Action? AlLlamar { get; set; }

    public Task<ResultadoConector<EstadoDocumentoEnPlataforma>> LeerEstadoDocumentoAsync(
        CredencialApiConector credencial, SolicitudLecturaEstado solicitud, CancellationToken cancellationToken) =>
        Responder(credencial, solicitud, EstadoDocumentoEnPlataforma.Validado, cancellationToken);

    public Task<ResultadoConector<ReferenciaEnPlataforma>> DarDeAltaTrabajadorAsync(
        CredencialApiConector credencial, SolicitudAltaTrabajador solicitud, CancellationToken cancellationToken) =>
        Responder(credencial, solicitud, new ReferenciaEnPlataforma("referencia-externa"), cancellationToken);

    public async Task<ResultadoConector<ReferenciaEnPlataforma>> SubirDocumentoAsync(
        CredencialApiConector credencial, SolicitudSubidaDocumento solicitud, CancellationToken cancellationToken)
    {
        using var memoria = new MemoryStream();
        await solicitud.Contenido.CopyToAsync(memoria, cancellationToken);
        UltimoContenido = memoria.ToArray();
        return await Responder(credencial, solicitud, new ReferenciaEnPlataforma("referencia-externa"), cancellationToken);
    }

    private Task<ResultadoConector<T>> Responder<T>(
        CredencialApiConector credencial, object solicitud, T valor, CancellationToken cancellationToken)
        where T : notnull
    {
        Llamadas++;
        UltimaCredencial = credencial.Revelar();
        UltimaSolicitud = solicitud;
        AlLlamar?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        if (Excepcion is { } excepcion)
            throw excepcion;

        return Task.FromResult(Fallo is { } motivo
            ? ResultadoConector<T>.Falla(motivo.Valor)
            : ResultadoConector<T>.Exito(valor));
    }
}

public record MotivoUsoConectorFalso(CaeManager.Domain.Integraciones.MotivoUsoConector Valor);
