using CaeManager.Domain.Common;
using CaeManager.Web.Services;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Queries.ObtenerPaqueteDocumentalVisita;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Visitas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Visitas.Recursos;
using ClosedXML.Excel;
using MediatR;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Visitas;

/// <summary>
/// Descarga del zip de documentación de una Visita a un Centro gestionado por correo
/// (P1-X1): el Gestor CAE lo adjunta a mano al correo de solicitud de acceso, sin M365
/// (D-1). Endpoint autenticado (FallbackPolicy global), nunca un archivo estático.
/// Autorización, selección y registro de accesos sensibles (DEC-36) viven en
/// <see cref="ObtenerPaqueteDocumentalVisitaQuery"/>, no aquí.
/// </summary>
public static class VisitasEndpoints
{
    public static async Task<IResult> ServirPaqueteAsync(Guid id, HttpContext contexto, IMediator mediator, ILoggerFactory fabricaRegistro, CancellationToken cancellationToken)
    {
        Result<PaqueteDocumentalDescargaDto> resultado;
        try
        {
            resultado = await mediator.Send(new ObtenerPaqueteDocumentalVisitaQuery(id), cancellationToken);
        }
        catch (Exception ex) when (ErroresLecturaSensible.EsMapeable(ex))
        {
            return ErroresLecturaSensible.Responder(contexto, ex, id,
                fabricaRegistro.CreateLogger(typeof(VisitasEndpoints).FullName!));
        }

        if (resultado.EsFallido)
        {
            // Fuera de alcance responde igual que inexistente; un rol que no descarga
            // (Consulta), 403; el resto son estados de la Visita que el Gestor CAE
            // puede entender.
            if (resultado.Error.Equals(ObtenerPaqueteDocumentalVisitaQueryHandler.RolSinDescarga))
                return Results.Text(resultado.Error.Mensaje, "text/plain; charset=utf-8", statusCode: StatusCodes.Status403Forbidden);

            return resultado.Error.Equals(ObtenerSolicitudAccesoCorreoQueryHandler.NoEncontrada)
                ? Results.NotFound()
                : Results.Text(resultado.Error.Mensaje, "text/plain; charset=utf-8", statusCode: StatusCodes.Status409Conflict);
        }

        CabecerasArchivoSensible.ProhibirCache(contexto);
        return Results.File(resultado.Valor.Contenido, "application/zip", resultado.Valor.NombreArchivo);
    }

    public static IEndpointRouteBuilder MapVisitasEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/visitas/{id:guid}/paquete-documental.zip", ServirPaqueteAsync);

        endpoints.MapGet("/visitas/exportar.xlsx", ExportarAsync).SoloRolesDelListado();

        return endpoints;
    }

    /// <remarks>
    /// Los parámetros de consulta son los de la exportación de la vista filtrada: los mismos criterios que
    /// <c>Visitas.razor.cs</c> pasa a <see cref="ObtenerVisitasQuery"/>. Sin ninguno, es
    /// «Exportar todo», que incluye el historial: <c>activas</c> solo restringe cuando la
    /// pantalla lo envía. Las dos variantes pasan por esa misma consulta: su autorización, su
    /// alcance y su Tenant son los del listado.
    /// <para>
    /// De las personas sale lo que la lista ya enseña: el recuento y, como en su ventana de
    /// contexto, «Nombre Apellidos» de quién entra. Sin DNI. La descarga deja rastro.
    /// </para>
    /// </remarks>
    public static async Task<IResult> ExportarAsync(
        IMediator mediator, IRegistroExportacionService registroExportacion,
        IStringLocalizer<TextosVisitas> textos, CancellationToken cancellationToken,
        string? q = null, string? activas = null, string? notificado = null, bool urgentes = false,
        string? orden = null, bool desc = false, string? estado = null,
        string? cliente = null, string? empresa = null, string? ventana = null, string? agrupar = null)
    {
        var soloActivas = activas == "true";
        bool? notificadoAplicado = notificado switch { "si" => true, "no" => false, _ => null };
        var estados = SeleccionEstados.Separar<EstadoDocumentacionVisita>(estado);

        var visitas = PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
            mediator.Send(
                new ObtenerVisitasQuery(
                    Busqueda: string.IsNullOrWhiteSpace(q) ? null : q,
                    SoloActivas: soloActivas,
                    NotificadoCliente: notificadoAplicado,
                    SoloUrgentes: urgentes,
                    EstadosDocumentacion: estados,
                    Pagina: pagina,
                    TamanoPagina: tamanoPagina,
                    OrdenarPor: string.IsNullOrWhiteSpace(orden) ? null : orden,
                    Descendente: desc, ClienteFiltro: cliente, EmpresaFiltro: empresa,
                    VentanaDias: SeleccionFiltroVisitas.Ventana(ventana),
                    AgruparPorCliente: agrupar == "cliente"),
                cancellationToken));

        var (libro, filas) = await LibroDeListado.EscribirAsync(
            "Visitas",
            [
                textos["EtiquetaCentro"].Value, textos["DetalleTitularCentro"].Value, textos["DetalleEmpresa"].Value,
                textos["EtiquetaFechaInicio"].Value, textos["EtiquetaFechaFin"].Value,
                textos["EtiquetaTrabajadores"].Value, textos["EtiquetaTrabajadoresQueEntran"].Value,
                textos["EtiquetaOrigen"].Value, textos["ColumnaDocumentacion"].Value, textos["EtiquetaNotificadaTitular"].Value
            ],
            FilasAsync(visitas, textos), cancellationToken);

        await registroExportacion.RegistrarAsync(
            nameof(Visita), filas,
            CriteriosExportacion.Desde(
                ("busqueda", string.IsNullOrWhiteSpace(q) ? null : "true"),
                ("activas", soloActivas ? "true" : null),
                ("notificado", notificadoAplicado switch { true => "si", false => "no", _ => null }),
                ("urgentes", urgentes ? "true" : null),
                ("estado", estados.Count == 0 ? null : SeleccionEstados.Unir(estados.Select(e => e.ToString()))),
                ("cliente", string.IsNullOrWhiteSpace(cliente) ? null : "true"),
                ("empresa", string.IsNullOrWhiteSpace(empresa) ? null : "true"),
                ("ventana", SeleccionFiltroVisitas.Ventana(ventana)?.ToString()),
                ("agrupar", agrupar == "cliente" ? "cliente" : null),
                ("orden", CriteriosExportacion.SoloNombre(orden)), ("desc", desc ? "true" : null)),
            cancellationToken);

        return Results.File(libro, LibroDeListado.TipoContenido, "visitas.xlsx");
    }

    private static async IAsyncEnumerable<IReadOnlyList<XLCellValue>> FilasAsync(
        IAsyncEnumerable<VisitaListaDto> visitas, IStringLocalizer<TextosVisitas> textos)
    {
        await foreach (var visita in visitas)
        {
            yield return
            [
                visita.CentroNombre, visita.ClienteRazonSocial, visita.EmpresaRazonSocial,
                LibroDeListado.Fecha(visita.FechaInicio), LibroDeListado.Fecha(visita.FechaFin),
                visita.TotalTrabajadores, string.Join("; ", visita.Trabajadores ?? []),
                textos[visita.Origen switch
                {
                    OrigenVisita.Correo => "OrigenCorreo",
                    OrigenVisita.WhatsApp => "OrigenWhatsApp",
                    OrigenVisita.Manual => "OrigenManual",
                    _ => "OrigenPlataforma"
                }].Value,
                // Las mismas cuatro lecturas, en el mismo orden, que la columna «Documentación».
                textos[visita.EstaCancelada ? "BadgeCancelada"
                    : !visita.CentroRequiereGestionCae ? "BadgeSinGestionCae"
                    : visita.DocumentacionGestionadaEnUtc is not null ? "BadgeDocumentacionGestionada"
                    : "BadgeDocumentacionPorGestionar"].Value,
                textos[visita.NotificadoCliente ? "OpcionSi" : "OpcionNo"].Value
            ];
        }
    }
}
