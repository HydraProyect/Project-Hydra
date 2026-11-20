namespace CaeManager.Web.Features.Comunicaciones.Components;

/// <summary>
/// A quién puede asignarse un adjunto en el panel de revisión del visor: un Trabajador o una Empresa que quien abre el visor ya leyó
/// con su alcance (los selectores de Trabajadores y Empresas del reparto). <paramref name="Detalle"/> es la segunda línea de la
/// tarjeta («Trabajador de Obras Norte»); sin DNI por diseño, como los selectores de los que sale.
/// </summary>
public sealed record DestinoRevision(Guid Id, bool EsEmpresa, string Nombre, string? Detalle = null);

/// <summary>Un tipo de documento que se ofrece al asignar. <paramref name="ParaEmpresa"/> dice a qué clase de destino sirve (un tipo de Empresa no sirve a un Trabajador).</summary>
public sealed record TipoRevision(Guid Id, string Nombre, bool ParaEmpresa);

/// <summary>
/// Lo que el servidor ya tiene guardado de un adjunto (destino y tipo propuestos, y el motivo si se rechazó) más las fechas que
/// una persona revisó para darlo de alta. Las fechas no las inventa el panel: sin <paramref name="FechaEmision"/> no se asigna.
/// </summary>
public sealed record PropuestaRevision(
    Guid? TrabajadorId, Guid? EmpresaId, Guid? TipoDocumentoId,
    DateOnly? FechaEmision = null, DateOnly? FechaVencimiento = null, bool NoCaduca = false, string? MotivoRechazo = null);

/// <summary>
/// Los datos del panel de revisión del visor, ya resueltos por quien abre el visor (el panel no lee nada del servidor para pintarse):
/// la conversación a la que pertenecen los adjuntos, lo guardado de cada uno por su Id y los destinos y tipos que se ofrecen.
/// </summary>
public sealed record RevisionAdjuntos(
    Guid ConversacionId,
    IReadOnlyDictionary<Guid, PropuestaRevision> Propuestas,
    IReadOnlyList<DestinoRevision> Destinos,
    IReadOnlyList<TipoRevision> Tipos);
