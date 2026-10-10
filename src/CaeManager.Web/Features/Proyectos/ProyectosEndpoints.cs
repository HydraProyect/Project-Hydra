using CaeManager.Application.Common;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Proyectos;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Proyectos.Recursos;
using ClosedXML.Excel;
using MediatR;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Proyectos;

/// <summary>Mismo patrón de referencia que TrabajadoresEndpoints — ver comentario allí.</summary>
public static class ProyectosEndpoints
{
    public static IEndpointRouteBuilder MapProyectosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/proyectos/exportar.xlsx", ExportarAsync).SoloRolesDelListado();

        return endpoints;
    }

    /// <remarks>
    /// «Exportar esta vista» lleva los criterios de la pantalla: el Cliente empresarial del filtro (si
    /// hay uno), la búsqueda y los estados de la franja. «Exportar todo» (sin criterios) saca los
    /// Proyectos de todos los Clientes empresariales que el usuario alcanza. Las dos variantes piden
    /// las filas a la consulta del listado (<see cref="ObtenerProyectosQuery"/>), página a página y
    /// con sus mismos filtros: su autorización, su alcance y su Tenant son los del listado, y un
    /// «cliente» fuera del alcance no exporta nada.
    /// <para>
    /// De los técnicos sale lo que la fila ya enseña: el recuento y, como en su ventana de
    /// contexto, «Nombre Apellidos» de los que tienen alta vigente, tal como los devuelve la
    /// consulta del listado (quien ve un Proyecto ve a sus técnicos). Sin DNI y sin lectura
    /// aparte. La descarga deja rastro.
    /// </para>
    /// </remarks>
    public static async Task<IResult> ExportarAsync(
        IMediator mediator, ITenantActual tenantActual, IRegistroExportacionService registroExportacion,
        IStringLocalizer<TextosProyectos> textos, CancellationToken cancellationToken,
        string? cliente = null, string? q = null, string? estado = null)
    {
        // Estado 4a, igual que /trabajadores/exportar.xlsx.
        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        if (ClientesAutorizados.PideElegirEmpresa(autorizados, tenantActual.TenantId))
            return Results.Redirect("/proyectos");

        Guid? clienteAplicado = Guid.TryParse(cliente, out var idPedido) ? idPedido : null;

        var estadosAplicados = FiltroProyectos.EstadosValidos(estado);

        var (libro, filas) = await LibroDeListado.EscribirAsync(
            "Proyectos",
            [
                textos["EtiquetaCliente"].Value, textos["ListaProyecto"].Value, textos["EtiquetaCentro"].Value,
                textos["EtiquetaInicio"].Value, textos["EtiquetaFinPrevisto"].Value,
                textos["ColumnaTecnicos"].Value, textos["VentanaTecnicosTitulo"].Value, textos["EtiquetaEstado"].Value
            ],
            FilasAsync(), cancellationToken);

        await registroExportacion.RegistrarAsync(
            nameof(Proyecto), filas,
            CriteriosExportacion.Desde(
                ("cliente", clienteAplicado?.ToString()),
                ("busqueda", string.IsNullOrWhiteSpace(q) ? null : "true"),
                ("estado", estadosAplicados)),
            cancellationToken);

        return Results.File(libro, LibroDeListado.TipoContenido, "proyectos.xlsx");

        async IAsyncEnumerable<IReadOnlyList<XLCellValue>> FilasAsync()
        {
            await foreach (var proyecto in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerProyectosQuery(
                        ClienteId: clienteAplicado,
                        SoloAbiertos: FiltroProyectos.SoloAbiertos(estadosAplicados),
                        Busqueda: string.IsNullOrWhiteSpace(q) ? null : q,
                        Pagina: pagina,
                        TamanoPagina: tamanoPagina),
                    cancellationToken)))
            {
                yield return
                [
                    proyecto.ClienteRazonSocial, proyecto.Nombre, proyecto.CentroNombre,
                    LibroDeListado.Fecha(proyecto.FechaInicio), LibroDeListado.Fecha(proyecto.FechaFinPrevista),
                    proyecto.TecnicosActivos.Count,
                    string.Join("; ", proyecto.TecnicosActivos.Select(t => t.NombreCompleto)),
                    textos[proyecto.EstaAbierto ? "BadgeAbierto" : "BadgeCerrado"].Value
                ];
            }
        }
    }
}
