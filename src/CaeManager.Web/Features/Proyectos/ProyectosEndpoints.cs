using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
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
    /// La pantalla enseña los Proyectos de un Cliente empresarial cada vez, elegido en su
    /// selector. «Exportar esta vista» lleva ese Cliente empresarial, la búsqueda y los estados
    /// de la franja; «Exportar todo» (sin criterios) recorre los Clientes empresariales del
    /// mismo selector. Las dos variantes usan las dos consultas de la página
    /// (<see cref="ObtenerClientesParaSelectorQuery"/> y <see cref="ObtenerProyectosQuery"/>)
    /// y el mismo filtro en memoria (<see cref="FiltroProyectos"/>): su autorización, su
    /// alcance y su Tenant son los del listado.
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

        // El selector es la lista de lo que este usuario puede elegir: un «cliente» que no
        // está en ella no exporta nada.
        var clientes = await mediator.Send(new ObtenerClientesParaSelectorQuery(), cancellationToken);
        Guid? clienteAplicado = Guid.TryParse(cliente, out var idPedido) ? idPedido : null;
        if (clienteAplicado is not null)
            clientes = clientes.Where(c => c.Id == clienteAplicado).ToList();

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
            var escritas = 0;
            foreach (var clienteEmpresarial in clientes)
            {
                var proyectos = await mediator.Send(new ObtenerProyectosQuery(clienteEmpresarial.Id), cancellationToken);
                foreach (var proyecto in proyectos.Where(p => FiltroProyectos.Cumple(p, estadosAplicados, q)))
                {
                    if (++escritas > PaginadorExportacion.MaximoElementosPorDefecto)
                        throw new InvalidOperationException(
                            $"La exportación supera el máximo de {PaginadorExportacion.MaximoElementosPorDefecto} filas — acota el filtro antes de exportar.");

                    yield return
                    [
                        clienteEmpresarial.RazonSocial, proyecto.Nombre, proyecto.CentroNombre,
                        LibroDeListado.Fecha(proyecto.FechaInicio), LibroDeListado.Fecha(proyecto.FechaFinPrevista),
                        proyecto.TecnicosActivos.Count,
                        string.Join("; ", proyecto.TecnicosActivos.Select(t => t.NombreCompleto)),
                        textos[proyecto.EstaAbierto ? "BadgeAbierto" : "BadgeCerrado"].Value
                    ];
                }
            }
        }
    }
}
