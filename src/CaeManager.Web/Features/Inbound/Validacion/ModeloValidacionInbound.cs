namespace CaeManager.Web.Features.Inbound.Validacion;

// Modelo de lectura y de peticiones de las dos pantallas de validación del Tenant TCE (Documentos por validar y
// Validación). Es el contrato que la pantalla pide al servicio: cuando entren B-21 (cola), B-22 (veredicto con Deshacer de
// 10 s), B-24 (veredicto del sistema) y B-26 (propuesta de la IA), el servicio real devuelve estos mismos registros y la
// pantalla no cambia. En la demo mínima, ServicioValidacionInboundConBackend rellena la cola y el documento con lo que la
// lectura Inbound ya da (sin lectura con IA, páginas ni historial) y registra Validar y Rechazar; el resto responde
// «no disponible».

/// <summary>A qué pertenece el documento: la Empresa contratista, uno de sus Trabajadores o uno de sus vehículos.</summary>
public enum EntidadValidable
{
    Empresa,
    Trabajador,
    Vehiculo
}

/// <summary>Resultado de la lectura con IA (validación previa) de un dato o del documento entero.</summary>
public enum ResultadoLectura
{
    Correcto,
    SinDetectar,
    ConError
}

/// <summary>Cuánto lleva un documento en la cola, para el filtro de antigüedad.</summary>
public enum TramoAntiguedad
{
    MenosDe24h,
    UnoATresDias,
    MasDeTresDias
}

/// <summary>Cómo agrupa la cola la pantalla «Documentos por validar».</summary>
public enum AgrupacionCola
{
    Requerimiento,
    EmpresaContratista,
    Centro,
    Entidad,
    SinAgrupar
}

/// <summary>Estado de vigencia del documento a validar, con el mismo vocabulario que Outbound.</summary>
public enum EstadoVigenciaValidacion
{
    Vigente,
    Proximo,
    Urgente,
    Vencido
}

/// <summary>Veredicto del Gestor CAE sobre un documento.</summary>
public enum VeredictoValidacion
{
    Validado,
    Rechazado,
    Incompleto
}

/// <summary>
/// Una fila de la cola. <paramref name="ExclusionLote"/> la dice el servidor (B-27: tipos que nunca se validan en lote,
/// como la aptitud médica, los trabajos de riesgo especial o la maquinaria); sin valor, la fila entra en lote si su
/// lectura es «Todo correcto».
/// </summary>
public sealed record DocumentoPorValidar(
    Guid Id,
    string Requerimiento,
    EntidadValidable Entidad,
    string Titular,
    string Identificador,
    string EmpresaContratista,
    string RolEmpresaContratista,
    IReadOnlyList<string> Centros,
    DateTime SubidoUtc,
    ResultadoLectura Lectura,
    int DatosAfectados,
    string? ExclusionLote = null,
    bool Propio = false);

/// <summary>La cola del Gestor CAE: sus documentos pendientes y los límites del lote que fija el Administrador del Tenant TCE.</summary>
public sealed record ColaValidacion(
    IReadOnlyList<DocumentoPorValidar> Documentos,
    int HechosHoy,
    int TopeLote,
    int MuestraUnoDeCada);

/// <summary>Un dato leído en el documento: dónde está en la página, como fracción de la página (0 a 1).</summary>
public sealed record CajaDato(string Campo, int Pagina, double X, double Y, double Ancho, double Alto, ResultadoLectura Resultado, string? Nota);

public sealed record PaginaDocumento(string UrlImagen, string Alt, IReadOnlyList<CajaDato> Cajas);

/// <summary>Lo que pone el documento frente a la ficha en TALVEG, para el titular y su identificador.</summary>
public sealed record DatoAVerificar(
    string Campo,
    string Etiqueta,
    string ValorFicha,
    string? ValorDocumento,
    ResultadoLectura Resultado,
    string? Nota,
    int? Pagina);

/// <summary>Un chequeo de la validación previa (tipo, titular, identificador, emisión, vigencia, firma y sello).</summary>
public sealed record ChequeoLectura(string Campo, string Etiqueta, ResultadoLectura Resultado, string? Detalle, int? Pagina);

/// <summary>El criterio de aceptación del requerimiento (configuración del Tenant TCE) y los datos que lee la IA.</summary>
public sealed record CriterioAceptacion(string Texto, IReadOnlyList<string> CamposLeidos);

public sealed record VersionAnterior(
    Guid Id,
    DateTime FechaUtc,
    string Estado,
    string Por,
    string? Motivo,
    DateOnly? Emision,
    string UrlArchivo,
    IReadOnlyList<PaginaDocumento> Paginas);

/// <summary>Otro Centro del mismo Cliente donde el mismo documento también cuenta.</summary>
public sealed record DestinoValidacion(Guid Id, string Centro, string Ahora);

/// <summary>La propuesta de la IA antes del veredicto (R-1). Sin ella, <see cref="DocumentoAValidar.NotaSinPropuesta"/> dice por qué.</summary>
public sealed record PropuestaIa(string Texto, IReadOnlyList<ChequeoLectura> Chequeos);

/// <summary>El documento abierto en la pantalla de validación, con todo lo que se ve a su alrededor.</summary>
public sealed record DocumentoAValidar(
    Guid Id,
    string Requerimiento,
    EntidadValidable Entidad,
    string Titular,
    string Identificador,
    string EmpresaContratista,
    string RolEmpresaContratista,
    string CifEmpresaContratista,
    IReadOnlyList<string> Centros,
    DateTime SubidoUtc,
    string Origen,
    string Requisito,
    string NombreArchivo,
    string UrlDescarga,
    IReadOnlyList<PaginaDocumento> Paginas,
    IReadOnlyList<DatoAVerificar> DatosAVerificar,
    IReadOnlyList<ChequeoLectura> Chequeos,
    CriterioAceptacion Criterio,
    DateOnly? Emision,
    DateOnly? Vencimiento,
    int? VigenciaMeses,
    EstadoVigenciaValidacion Vigencia,
    IReadOnlyList<VersionAnterior> Anteriores,
    IReadOnlyList<DestinoValidacion> Destinos,
    PropuestaIa? Propuesta,
    string? NotaSinPropuesta,
    string MotivoRechazoSugerido,
    string FaltaSugerida,
    string ExplicacionSugerida,
    bool Propio);

public sealed record SolicitudValidar(Guid Id, DateOnly? Emision, DateOnly? Vencimiento, bool ConfirmadoConErrores, IReadOnlyList<Guid> DestinosAdicionales);

public sealed record SolicitudRechazar(Guid Id, string Motivo, string Explicacion);

public sealed record SolicitudMarcarIncompleto(Guid Id, string Falta, string Explicacion);

/// <summary>
/// Un veredicto registrado; el aviso a la Empresa contratista sale al terminar el plazo de Deshacer (B-22). Sin
/// <paramref name="Deshacible"/> (la demo mínima, antes de B-22) el veredicto ya es firme: la pantalla no ofrece Deshacer
/// ni promete un aviso diferido.
/// </summary>
public sealed record VeredictoRegistrado(Guid VeredictoId, VeredictoValidacion Veredicto, bool Deshacible = true);

public sealed record LoteRegistrado(Guid LoteId, int Documentos, int ParaRevision);

/// <summary>
/// Respuesta del servicio. «No disponible» no es un error: es la pantalla sin backend, que no pinta datos ni inventa
/// ninguno. <see cref="Mensaje"/> lleva el motivo cuando el servidor rechaza una petición.
/// </summary>
public sealed record RespuestaValidacion<T>(bool Disponible, T? Valor, string? Mensaje = null)
{
    public static RespuestaValidacion<T> NoDisponible { get; } = new(false, default);

    public static RespuestaValidacion<T> Correcta(T valor) => new(true, valor);

    public static RespuestaValidacion<T> Rechazada(string mensaje) => new(true, default, mensaje);

    public bool Aceptada => Disponible && Mensaje is null && Valor is not null;
}
