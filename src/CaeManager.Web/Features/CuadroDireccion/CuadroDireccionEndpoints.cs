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
/// los Tenants beneficiarios que alcanza quien mira, con una fila por Trabajador extranjero cobrable y Visita del mes;
/// para un Operador CAE, solo las de la vigencia de su Asignación de Operación en cada Empresa gestionada (las de su cifra
/// facturada). Lo que entra, el recorrido por Tenant y la autorización viven en <see cref="ObtenerExtranjerosDelMesQuery"/>
/// (la acotación a la vigencia, en <see cref="ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery"/>); aquí
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
        // Con tenantId, solo las filas de esa Empresa gestionada y solo las de la vigencia de la Asignación de Operación externa
        // del Operador CAE: las mismas que cuenta la pestaña Facturación (ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery), para
        // que cifra y Excel no diverjan. Un Id que no es una Empresa gestionada en el mes (el propio Tenant del Operador, uno sin
        // Asignación externa en vigor o ajeno) da un libro sin filas, nunca datos de otro Tenant. Sin tenantId, el Excel general de
        // todas las Empresas: para un Operador CAE, acotado igualmente a la vigencia de cada Empresa gestionada (nº de filas = cifra
        // facturada de extranjeros, la que cuenta el KPI del Cuadro y /facturacion); quien no es de un Operador CAE lo recibe entero.
        var extranjeros = await mediator.Send(
            new ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery(anyo, mes, tenantId, GeneralSiNoEsOperadorCae: tenantId is null),
            cancellationToken);
        var delMes = extranjeros.Filas;
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
