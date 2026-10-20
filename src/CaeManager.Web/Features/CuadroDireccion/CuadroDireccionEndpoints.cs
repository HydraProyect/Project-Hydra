using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.CuadroDireccion.Recursos;
using ClosedXML.Excel;
using MediatR;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.CuadroDireccion;

/// <summary>
/// «Exportar extranjeros del mes» del Cuadro de dirección (decisión de Chris, 2026-10-20): el Excel general, de todos
/// los Tenants beneficiarios que alcanza quien mira, con una fila por Trabajador extranjero cobrable y Visita del mes.
/// Lo que entra, el recorrido por Tenant y la autorización viven en <see cref="ObtenerExtranjerosDelMesQuery"/>; aquí
/// solo se escribe el libro y se deja el rastro de la descarga, que lleva DNI y pasaportes.
/// </summary>
public static class CuadroDireccionEndpoints
{
    public const string RutaExtranjerosDelMes = "/direccion/extranjeros.xlsx";

    public static IEndpointRouteBuilder MapCuadroDireccionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Los mismos roles que la página /direccion (su [Authorize]); la consulta vuelve a exigir
        // AutorizacionCuadroDireccion, que es la regla, para que ninguna otra puerta la salte.
        endpoints.MapGet(RutaExtranjerosDelMes, ExportarExtranjerosDelMesAsync)
            .RequireAuthorization(politica => politica.RequireRole(Roles.DireccionCae, Roles.CoordinadorCae));

        return endpoints;
    }

    public static async Task<IResult> ExportarExtranjerosDelMesAsync(
        int anyo, int mes, HttpContext contexto, IMediator mediator, IRegistroExportacionService registroExportacion,
        IStringLocalizer<TextosCuadroDireccion> textos, CancellationToken cancellationToken)
    {
        var extranjeros = await mediator.Send(new ObtenerExtranjerosDelMesQuery(anyo, mes), cancellationToken);
        if (extranjeros.SinCifras == CuadroDireccionSinCifras.MesNoValido)
            return Results.BadRequest();

        var (libro, filas) = await LibroDeListado.EscribirAsync(
            textos["ExtranjerosHoja"].Value,
            [
                textos["ExtranjerosColumnaTenant"].Value, textos["ExtranjerosColumnaFechaSolicitud"].Value,
                textos["ExtranjerosColumnaContacto"].Value, textos["ExtranjerosColumnaEmpresa"].Value,
                textos["ExtranjerosColumnaSubcontrata"].Value, textos["ExtranjerosColumnaCliente"].Value,
                textos["ExtranjerosColumnaCentro"].Value, textos["ExtranjerosColumnaFechaIngreso"].Value,
                textos["ExtranjerosColumnaFechaSalida"].Value, textos["ExtranjerosColumnaTrabajador"].Value,
                textos["ExtranjerosColumnaDocumento"].Value, textos["ExtranjerosColumnaGestorCae"].Value,
            ],
            FilasAsync(extranjeros.Filas), cancellationToken);

        await registroExportacion.RegistrarAsync(
            nameof(Trabajador), filas,
            CriteriosExportacion.Desde(
                ("listado", "extranjerosDelMes"),
                ("anyo", anyo.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("mes", mes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("tenantsSinAlcanceCompleto", extranjeros.TenantsSinAlcanceCompleto > 0
                    ? extranjeros.TenantsSinAlcanceCompleto.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null)),
            cancellationToken);

        CabecerasArchivoSensible.ProhibirCache(contexto);
        return Results.File(libro, LibroDeListado.TipoContenido, $"trabajadores-extranjeros-{anyo:0000}-{mes:00}.xlsx");
    }

    private static async IAsyncEnumerable<IReadOnlyList<XLCellValue>> FilasAsync(IReadOnlyList<FilaExtranjeroDelMesDto> filas)
    {
        foreach (var fila in filas)
        {
            yield return
            [
                fila.TenantNombre, LibroDeListado.Fecha(fila.FechaSolicitud), fila.ContactoVisita ?? string.Empty,
                fila.Empresa ?? string.Empty, fila.Subcontrata ?? string.Empty, fila.Cliente ?? string.Empty,
                fila.Centro, LibroDeListado.Fecha(fila.FechaIngreso), LibroDeListado.Fecha(fila.FechaSalida),
                fila.NombreYApellidos, fila.Documento ?? string.Empty, fila.GestorCae ?? string.Empty,
            ];
        }

        await Task.CompletedTask;
    }
}
