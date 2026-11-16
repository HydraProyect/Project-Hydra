namespace CaeManager.Web.Features.Inbound.TenantTec.Servicios;

/// <summary>
/// Estado de un requerimiento visto por el Tenant TEC (el Usuario único de la Empresa que aporta
/// documentación a sus Clientes empresariales). El nombre de cada valor es el código que viaja en la
/// franja de estados de la pantalla.
/// </summary>
public enum EstadoRequerimientoTec
{
    Rechazado,
    PendienteDeSubir,
    Incompleto,
    PendienteDeValidar,
    Validado,
}

public enum VigenciaRequerimientoTec
{
    SinVigencia,
    Vigente,
    Proximo,
    Urgente,
    Vencido,
    VencidoEnTolerancia,
}

public enum EntidadRequerimientoTec
{
    Empresa,
    Trabajador,
    Vehiculo,
}

public enum AccionRequerimientoTec
{
    Ninguna,
    Subir,
    Firmar,
}

public enum LecturaCampoTec
{
    Correcto,
    Aviso,
    Error,
}

/// <summary>Un campo del criterio de aceptación leído automáticamente del último documento.</summary>
public sealed record CriterioLecturaTec(string Campo, LecturaCampoTec Resultado);

/// <summary>Un nivel (Legal, Actividad, Centro, Riesgo) que pide el mismo documento.</summary>
public sealed record NivelRequerimientoTec(string Nivel, string Exige, DateOnly? Vence);

/// <summary>
/// Un documento anterior del requerimiento. <paramref name="Origen"/> es el texto ya resuelto de quién lo
/// aportó; <paramref name="AportadoPorAutorizacionUnica"/> distingue el que llegó solo desde la
/// documentación de la Empresa.
/// </summary>
public sealed record VersionDocumentoTec(
    Guid Id,
    DateTimeOffset Fecha,
    EstadoRequerimientoTec Estado,
    string? SubidoPor,
    bool AportadoPorAutorizacionUnica,
    string? Motivo,
    DateOnly? Emision);

/// <summary>
/// Un requerimiento que un Cliente empresarial pide al Tenant TEC, con lo necesario para listarlo,
/// abrir su detalle y subirle el documento.
/// </summary>
public sealed record RequerimientoTec(
    Guid Id,
    string ClienteEmpresarial,
    string Nombre,
    string Empresa,
    string? PapelEmpresa,
    EntidadRequerimientoTec Entidad,
    string ReferenciaEntidad,
    string? AmbitoEntidad,
    string Centro,
    EstadoRequerimientoTec Estado,
    VigenciaRequerimientoTec Vigencia,
    DateOnly? Hasta,
    int Vigencias,
    string? Motivo,
    string? Falta,
    string? Criterio,
    AccionRequerimientoTec Accion,
    bool HayDocumento,
    bool EsAptitudMedica,
    IReadOnlyList<CriterioLecturaTec> Lectura,
    IReadOnlyList<NivelRequerimientoTec> Niveles,
    IReadOnlyList<VersionDocumentoTec> Historial);

/// <summary>
/// Lectura inversa de los requerimientos que los Clientes empresariales piden al Tenant TEC.
/// </summary>
/// <remarks>
/// shortcut: sin backend, cablear cuando entre B-11 (lectura inversa X-2: contratos, requerimientos,
/// motivos e información del Centro para el Tenant TEC, sin identidad del Gestor CAE).
/// </remarks>
public interface IRequerimientosTenantTec
{
    /// <summary>Falso mientras no exista el backend de la lectura: la pantalla avisa en vez de enseñar una lista vacía.</summary>
    bool Disponible { get; }

    Task<IReadOnlyList<RequerimientoTec>> ListarAsync(CancellationToken ct = default);
}

// shortcut: sin backend, cablear cuando entre B-11
public sealed class RequerimientosTenantTecSinBackend : IRequerimientosTenantTec
{
    public bool Disponible => false;

    public Task<IReadOnlyList<RequerimientoTec>> ListarAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RequerimientoTec>>([]);
}

public enum ResultadoSubidaTec
{
    /// <summary>El documento se ha guardado y queda «Pendiente de validar».</summary>
    Subido,

    /// <summary>El documento contiene datos de salud: no se acepta ni se guarda.</summary>
    NoAceptadoPorDatosDeSalud,

    /// <summary>El backend de la subida todavía no existe.</summary>
    NoDisponible,

    Error,
}

/// <summary>El archivo elegido y lo que la persona ha rellenado. El flujo se consume una sola vez.</summary>
public sealed record SolicitudSubidaTec(
    Guid RequerimientoId,
    string NombreArchivo,
    string TipoContenido,
    long Tamano,
    Stream Contenido,
    DateOnly? Emision,
    DateOnly? Vencimiento,
    string? Comentarios);

public sealed record RespuestaSubidaTec(ResultadoSubidaTec Resultado, Guid? SiguientePendienteId = null, string? SiguientePendiente = null);

/// <summary>
/// Subida de un documento a un requerimiento: formatos, conversión a PDF, comprobaciones C0/C1,
/// filtro de datos de salud e historial.
/// </summary>
/// <remarks>shortcut: sin backend, cablear cuando entre B-20 (subida Inbound).</remarks>
public interface ISubidaDocumentoTenantTec
{
    bool Disponible { get; }

    Task<RespuestaSubidaTec> SubirAsync(SolicitudSubidaTec solicitud, CancellationToken ct = default);
}

// shortcut: sin backend, cablear cuando entre B-20
public sealed class SubidaDocumentoTenantTecSinBackend : ISubidaDocumentoTenantTec
{
    public bool Disponible => false;

    public Task<RespuestaSubidaTec> SubirAsync(SolicitudSubidaTec solicitud, CancellationToken ct = default) =>
        Task.FromResult(new RespuestaSubidaTec(ResultadoSubidaTec.NoDisponible));
}
