using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

public enum OperacionConectorPlataformaCae
{
    LeerEstadoDocumento,
    DarDeAltaTrabajador,
    SubirDocumento,
}

public enum ResultadoUsoConector
{
    /// <summary>La llamada salió hacia la plataforma y no consta cómo acabó: si la fila se queda así, no se sabe.</summary>
    EnCurso,
    Exito,
    Fallo,

    /// <summary>TALVEG no llegó a llamar a la plataforma.</summary>
    Denegado,
}

/// <summary>
/// Motivo cerrado de una denegación o de un fallo. Un catálogo y no el texto de la plataforma a
/// propósito: la respuesta de un tercero puede llevar datos de la cuenta o del token, y aquí no entra
/// (OWASP ASVS 4.0.3 V7.1.1 y V7.1.2).
/// </summary>
public enum MotivoUsoConector
{
    PlataformaDesactivada,
    AddOnInactivo,
    EstadoComercialRestringido,
    SinCredencial,
    SinConector,
    ObjetoNoEncontrado,
    ObjetoDeOtraPlataforma,
    DatosInsuficientes,
    SinAutoridad,
    TenantSuspendido,
    CredencialRechazada,
    NoEncontradoEnPlataforma,
    RechazadoPorPlataforma,
    LimiteSuperado,
    PlataformaNoDisponible,
    Inesperado,
    Cancelado,
}

/// <summary>
/// Registro de cada uso de una <see cref="ConexionPlataformaCae"/>: las llamadas a la plataforma y
/// también las denegadas (OWASP ASVS 4.0.3 V7.2.2). Nace antes de la llamada, para que un corte a
/// mitad deje rastro, y solo se escribe una vez más para cerrarla: <c>cae_app_runtime</c> no tiene
/// <c>DELETE</c>, su <c>UPDATE</c> es por columna y una política impide tocar una fila ya cerrada.
/// No guarda la credencial, ni datos del Trabajador, ni la respuesta de la plataforma.
/// </summary>
public class UsoConexionPlataformaCae : EntidadConTenant
{
    public const int LongitudMaximaClaveIdempotencia = 200;

    public Guid ConexionPlataformaCaeId { get; private set; }
    public OperacionConectorPlataformaCae Operacion { get; private set; }
    public ResultadoUsoConector Resultado { get; private set; }
    public MotivoUsoConector? Motivo { get; private set; }

    /// <summary>Null en un proceso de fondo, sin usuario.</summary>
    public Guid? UsuarioId { get; private set; }

    /// <summary>Lo que se pidió, sin clave foránea: una denegación puede nombrar un Id que no existe o que este Tenant no ve.</summary>
    public Guid? AcreditacionId { get; private set; }
    public Guid? TrabajadorId { get; private set; }

    /// <summary>La que se envió a la plataforma: si un uso se queda EnCurso, permite reconciliarlo con ella. Null en una lectura.</summary>
    public string? ClaveIdempotencia { get; private set; }

    public DateTime IniciadoEnUtc { get; private set; }
    public DateTime? FinalizadoEnUtc { get; private set; }

    private UsoConexionPlataformaCae()
    {
    }

    private UsoConexionPlataformaCae(
        Guid conexionId, OperacionConectorPlataformaCae operacion, Guid? usuarioId, Guid? acreditacionId, Guid? trabajadorId, DateTime ahoraUtc)
    {
        if (conexionId == Guid.Empty)
            throw new ArgumentException("El uso debe pertenecer a una conexión.", nameof(conexionId));

        ConexionPlataformaCaeId = conexionId;
        Operacion = operacion;
        UsuarioId = usuarioId;
        AcreditacionId = acreditacionId;
        TrabajadorId = trabajadorId;
        IniciadoEnUtc = ahoraUtc;
    }

    public static UsoConexionPlataformaCae Iniciar(
        Guid conexionId, OperacionConectorPlataformaCae operacion, Guid? usuarioId, Guid? acreditacionId, Guid? trabajadorId, DateTime ahoraUtc,
        string? claveIdempotencia = null) =>
        new(conexionId, operacion, usuarioId, acreditacionId, trabajadorId, ahoraUtc)
        {
            Resultado = ResultadoUsoConector.EnCurso,
            ClaveIdempotencia = claveIdempotencia,
        };

    public static UsoConexionPlataformaCae Denegar(
        Guid conexionId, OperacionConectorPlataformaCae operacion, Guid? usuarioId, Guid? acreditacionId, Guid? trabajadorId,
        MotivoUsoConector motivo, DateTime ahoraUtc) =>
        new(conexionId, operacion, usuarioId, acreditacionId, trabajadorId, ahoraUtc)
        {
            Resultado = ResultadoUsoConector.Denegado,
            Motivo = motivo,
            FinalizadoEnUtc = ahoraUtc,
        };

    public void FinalizarConExito(DateTime ahoraUtc) => Finalizar(ResultadoUsoConector.Exito, motivo: null, ahoraUtc);

    public void FinalizarConFallo(MotivoUsoConector motivo, DateTime ahoraUtc) => Finalizar(ResultadoUsoConector.Fallo, motivo, ahoraUtc);

    private void Finalizar(ResultadoUsoConector resultado, MotivoUsoConector? motivo, DateTime ahoraUtc)
    {
        if (Resultado != ResultadoUsoConector.EnCurso)
            throw new InvalidOperationException("Este uso ya está cerrado.");

        Resultado = resultado;
        Motivo = motivo;
        FinalizadoEnUtc = ahoraUtc;
    }
}
