namespace CaeManager.Application.Common;

/// <summary>
/// El único sitio donde se construye el <c>Id</c> de <c>ItemBandejaDto</c> (la fila de la cola de trabajo).
///
/// <para>
/// <b>Por qué hay un punto único.</b> El Id de una fila de la cola es la clave <c>@key</c> del <c>@foreach</c> que la
/// pinta (Mi trabajo, Bandeja, Inicio, GrupoCola) y la identidad del detalle abierto. Dos filas hermanas con el mismo Id
/// son dos <c>@key</c> iguales: el diff de Blazor lanza «Attempting to return wrong pooled instance» y MATA EL CIRCUITO
/// (~1 s tras conectar; los clics siguientes se pierden). Ya ocurrió: la misma Faltante de un Trabajador en dos Centros
/// compartía Id (#1064). Con la cadena escrita a mano en cada productor, la unicidad dependía de que cada autor
/// recordara qué dimensiones identifican su fila; aquí cada constructor declara TODAS sus dimensiones como parámetros
/// obligatorios, y quien añade un productor nuevo pasa por este fichero (lo exige
/// <c>ClavesDeListaUnicasPorConstruccionTests</c>, trinquete por ubicación).
/// </para>
///
/// <para>
/// <b>Qué dimensiones lleva cada Id, y cuáles no.</b> El Id es único dentro de la cola de <i>un</i> Tenant propietario
/// (cada productor corre sellado a su Tenant); el Tenant no viaja en el Id porque <c>ItemBandejaDto</c> no lo
/// lleva (la vista de Mi trabajo, que sí mezcla Tenants en un mismo grupo por severidad, lo añade a la identidad de su clave:
/// <c>ClavesDeHermanos</c>). Cada prefijo es un tipo de fila distinto, así que dos filas de tipos distintos
/// nunca chocan aunque compartan Guid.
/// </para>
///
/// <para>
/// <b>Lo que NO garantiza.</b> Que el productor no emita dos veces la MISMA fila (un requisito repetido por una
/// Asignación duplicada, un documento listado dos veces). Eso no se arregla con un identificador mejor: lo vigila la
/// red de tests de unicidad sobre datos que generan hermanos, y la vista numera las repeticiones para no morir.
/// <see cref="Duplicados"/> es la medida común de ambas.
/// </para>
/// </summary>
public static class IdDeFilaDeCola
{
    /// <summary>
    /// Alerta documental. Con documento, esa alerta es el documento. Sin documento (Faltante), el trío
    /// Trabajador-Tipo-Centro: el mismo Trabajador asignado a dos Centros a los que les falta el mismo Tipo da dos
    /// alertas, y con solo Trabajador-Tipo compartían Id (#1064).
    /// </summary>
    public static string Alerta(string prefijo, Guid? documentoId, Guid trabajadorId, Guid tipoDocumentoId, Guid? centroId) =>
        documentoId is { } documento
            ? $"{prefijo}-{documento}"
            : $"{prefijo}-{trabajadorId}-{tipoDocumentoId}-{centroId?.ToString() ?? "sin-centro"}";

    public static string Revision(Guid revisionId) => $"revision-{revisionId}";

    /// <summary>
    /// Requisito bloqueante: un Trabajador en un Centro por un Tipo. Un requisito de Empresa que bloquea a todos sus
    /// Trabajadores (R2) es una fila por Trabajador × Centro × Tipo, así que las tres dimensiones son obligatorias.
    /// </summary>
    public static string Requisito(Guid centroId, Guid trabajadorId, Guid tipoDocumentoId) =>
        $"requisito-{centroId}-{trabajadorId}-{tipoDocumentoId}";

    public static string Visita(Guid visitaId) => $"visita-{visitaId}";

    public static string SugerenciaVisita(Guid sugerenciaId) => $"sugerencia-visita-{sugerenciaId}";

    public static string Deteccion(Guid deteccionId) => $"deteccion-{deteccionId}";

    public static string Plataforma(Guid acreditacionId) => $"plataforma-{acreditacionId}";

    public static string PlataformaVencida(Guid acreditacionId) => $"plataforma-vencida-{acreditacionId}";

    public static string Seguimiento(Guid acreditacionId) => $"seguimiento-{acreditacionId}";

    /// <summary>
    /// Los Id que aparecen más de una vez entre <paramref name="items"/> (cada uno con cuántas veces; vacío = todos únicos). Genérico
    /// sobre el elemento: la cola de ItemBandejaDto lo usa con <c>i =&gt; i.Id</c>, y no obliga a esta clase a nombrar el tipo.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Duplicados<T>(IEnumerable<T> items, Func<T, string> id) => items
        .GroupBy(id, StringComparer.Ordinal)
        .Where(g => g.Count() > 1)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}
