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
        int anyo, int mes, Guid? tenantId, HttpContext contexto, IMediator mediator, IRegistroExportacionService registroExportacion,
        IStringLocalizer<TextosCuadroDireccion> textos, CancellationToken cancellationToken)
    {
        var extranjeros = await mediator.Send(new ObtenerExtranjerosDelMesQuery(anyo, mes), cancellationToken);
        // Con tenantId, solo las filas de esa Empresa gestionada (el detalle de la pestaña Facturación). La consulta ya dejó fuera
        // a quien mira no alcanza: un Id ajeno da un libro sin filas, nunca datos de otro Tenant.
        var delMes = tenantId is { } empresa ? extranjeros.Filas.Where(f => f.TenantId == empresa).ToList() : extranjeros.Filas;
        // Un mes futuro no tiene Visitas que cobrar todavía: se rechaza igual que uno inexistente.
        if (extranjeros.SinCifras is CuadroDireccionSinCifras.MesNoValido or CuadroDireccionSinCifras.MesFuturo)
            return Results.BadRequest();

        // Si a algún Tenant beneficiario no se le alcanza entero, el libro lo dice al final: quien lo use para
        // facturar tiene que saber que le faltan Empresas gestionadas, no deducirlo de que salgan menos filas.
        var nota = extranjeros.TenantsSinAlcanceCompleto > 0
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                textos["ExtranjerosNotaSinAlcance"].Value, extranjeros.TenantsSinAlcanceCompleto)
            : null;

        var (libro, filasEscritas) = await LibroDeListado.EscribirAsync(
            textos["ExtranjerosHoja"].Value,
            [
                textos["ExtranjerosColumnaTenant"].Value, textos["ExtranjerosColumnaFechaSolicitud"].Value,
                textos["ExtranjerosColumnaContacto"].Value, textos["ExtranjerosColumnaEmpresa"].Value,
                textos["ExtranjerosColumnaSubcontrata"].Value, textos["ExtranjerosColumnaCliente"].Value,
                textos["ExtranjerosColumnaCentro"].Value, textos["ExtranjerosColumnaFechaIngreso"].Value,
                textos["ExtranjerosColumnaFechaSalida"].Value, textos["ExtranjerosColumnaTrabajador"].Value,
                textos["ExtranjerosColumnaDocumento"].Value, textos["ExtranjerosColumnaGestorCae"].Value,
            ],
            FilasAsync(delMes, nota), cancellationToken);

        // El rastro cuenta Trabajadores exportados, no la línea en blanco ni la nota.
        var filas = nota is null ? filasEscritas : filasEscritas - FilasDeNota;

        await registroExportacion.RegistrarAsync(
            nameof(Trabajador), filas,
            CriteriosExportacion.Desde(
                ("listado", "extranjerosDelMes"),
                ("anyo", anyo.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("tenantId", tenantId?.ToString()),
                ("mes", mes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("tenantsSinAlcanceCompleto", extranjeros.TenantsSinAlcanceCompleto > 0
                    ? extranjeros.TenantsSinAlcanceCompleto.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null)),
            cancellationToken);

        CabecerasArchivoSensible.ProhibirCache(contexto);
        return Results.File(libro, LibroDeListado.TipoContenido, $"trabajadores-extranjeros-{anyo:0000}-{mes:00}.xlsx");
    }

    /// <summary>Una línea en blanco y la nota de Tenants sin alcance completo.</summary>
    private const int FilasDeNota = 2;

    private static async IAsyncEnumerable<IReadOnlyList<XLCellValue>> FilasAsync(
        IReadOnlyList<FilaExtranjeroDelMesDto> filas, string? nota)
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

        if (nota is not null)
        {
            yield return [];
            yield return [nota];
        }

        await Task.CompletedTask;
    }
}
