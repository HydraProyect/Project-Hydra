using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Domain.Documentos;

namespace CaeManager.Web.Features.Empresas.Components;

/// <summary>Una fila de la pestaña «Plataformas» de Empresa 360: un documento de la Empresa en la plataforma CAE de un Cliente, por Centro.</summary>
internal sealed record FilaPlataforma(
    string Cliente, Guid CentroId, string Centro, string Plataforma, string Documento, EstadoAcreditacion Estado, string? Motivo,
    bool Vencida = false, DateOnly? VenceEnPlataforma = null);

/// <summary>
/// El universo de la pestaña «Plataformas» de Empresa 360 y su regla de incidencia, en un solo sitio: la pestaña pinta sus filas y la
/// cabecera de la ficha cuenta con ellas las incidencias del contador de la pestaña, sin abrirla. Las dos leen
/// <see cref="ObtenerAcreditacionesPorProveedorQuery"/> con la misma petición (<see cref="Consulta"/>), así que la cifra de la pestaña
/// cerrada es la misma que enseña la pestaña abierta.
/// </summary>
internal static class FilasPlataformasEmpresa
{
    /// <summary>La petición que define el universo: los documentos de esta Empresa, aceptados y subidos incluidos, con el alcance de gestión de quien mira.</summary>
    public static ObtenerAcreditacionesPorProveedorQuery Consulta(Guid empresaId) =>
        new(IncluirAceptadas: true, IncluirSubidas: true, SoloDocumentosDeEmpresaId: empresaId);

    /// <summary>
    /// Un mismo documento puede llegar dos veces bajo la misma plataforma y Centro (p. ej. por dos canales): se queda con la fila más
    /// grave, que es la que hay que atender. Sin Centro no hay fila: la pestaña agrupa por Centro.
    /// </summary>
    public static List<FilaPlataforma> Construir(IEnumerable<ProveedorAcreditacionesDto> proveedores) =>
    [
        .. proveedores.SelectMany(p => p.Clientes.SelectMany(c => c.Documentos
            .Where(d => d.CentroId is not null)
            .Select(d => new FilaPlataforma(c.ClienteNombre, d.CentroId!.Value, d.CentroNombre ?? "—", p.ProveedorNombre,
                d.TipoDocumentoNombre, d.Estado, d.Estado == EstadoAcreditacion.Rechazada ? d.UltimoMotivoRechazo : null,
                d.Estado == EstadoAcreditacion.Aceptada && d.VencidaEnPlataforma, d.FechaVencimientoEnPlataforma))))
            .GroupBy(f => (f.Cliente, f.CentroId, f.Plataforma, f.Documento))
            .Select(g => g.OrderBy(Rango).First())
    ];

    /// <summary>Incidencia = todo lo que no está validado en la plataforma, y también lo validado que ya venció allí.</summary>
    public static bool EsIncidencia(FilaPlataforma fila) => fila.Estado != EstadoAcreditacion.Aceptada || fila.Vencida;

    public static int Incidencias(IEnumerable<FilaPlataforma> filas) => filas.Count(EsIncidencia);

    public static int Rango(FilaPlataforma fila) => fila.Vencida ? 0 : fila.Estado switch
    {
        EstadoAcreditacion.Rechazada => 0,
        EstadoAcreditacion.PendienteDeSubir => 1,
        EstadoAcreditacion.Subida => 2,
        _ => 3
    };
}
