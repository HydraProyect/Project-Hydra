using CaeManager.Application.Common;
using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Services;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Facturacion;

/// <summary>
/// Mismo patrón que ClientesEndpoints.cs (Project-Hydra-Negocio/tecnico/docs/ux-audit/11-reportes-facturacion.md H2)
/// — exporta el resumen exacto que la pestaña "Resumen mensual" ya calculó
/// para el cliente/año/mes seleccionados, no "todo".
/// </summary>
public static class FacturacionEndpoints
{
    /// <summary>Excel de «Facturación»: la cuota del Operador CAE a cada Empresa gestionada en un mes.</summary>
    public const string RutaCuotaDelOperador = "/facturacion/cuota-operador.xlsx";

    /// <summary>CSV de la liquidación interna del mes, para importarla en el programa de facturación del emisor (V-2).</summary>
    public const string RutaLiquidacionDelOperador = "/facturacion/liquidacion-operador.csv";

    public static IEndpointRouteBuilder MapFacturacionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Los mismos roles que la página /facturacion. Las consultas vuelven a exigir su propia autorización
        // (AutorizacionCuadroDireccion y AutorizacionTarifasOperadorCae): este endpoint no decide quién ve importes.
        // Admite Coordinador CAE, así que (como la página) no entra en las exclusiones del Encargo de administración,
        // que son de lo que pide Administrador o Dirección CAE sin Coordinador CAE.
        endpoints.MapGet(RutaCuotaDelOperador, ExportarCuotaDelOperadorAsync)
            .RequireAuthorization(policy => policy.RequireRole(
                CaeManager.Infrastructure.Identity.Roles.Administrador,
                CaeManager.Infrastructure.Identity.Roles.DireccionCae,
                CaeManager.Infrastructure.Identity.Roles.CoordinadorCae));

        // La liquidación para importar en el programa de facturación lleva importes: solo Administrador y Dirección CAE (D-F9).
        // La consulta del modelo vuelve a exigir sus autorizaciones, y el endpoint rechaza el modelo sin importes.
        endpoints.MapGet(RutaLiquidacionDelOperador, ExportarLiquidacionDelOperadorAsync)
            .RequireAuthorization(policy => policy.RequireRole(
                CaeManager.Infrastructure.Identity.Roles.Administrador, CaeManager.Infrastructure.Identity.Roles.DireccionCae))
            .ExcluidoDelEncargoDeAdministracion();

        endpoints.MapGet("/facturacion/resumen.xlsx", ExportarResumenAsync).RequireAuthorization(policy => policy.RequireRole(
            CaeManager.Infrastructure.Identity.Roles.Administrador, CaeManager.Infrastructure.Identity.Roles.DireccionCae))
        .ExcluidoDelEncargoDeAdministracion();

        return endpoints;
    }

    public static async Task<IResult> ExportarResumenAsync(
        Guid clienteId, int anyo, int mes, IMediator mediator, HttpContext contexto,
        [Microsoft.AspNetCore.Mvc.FromServices] IAlcanceDatosService alcanceDatos, [Microsoft.AspNetCore.Mvc.FromServices] IRegistroExportacionService registroExportacion,
        CancellationToken cancellationToken)
    {
        CabecerasArchivoSensible.ProhibirCache(contexto);
        if (!await alcanceDatos.ClienteVisibleAsync(clienteId, cancellationToken))
            return Results.NotFound();

        var consulta = new ObtenerResumenFacturacionQuery(clienteId, anyo, mes);
        var resumen = await mediator.Send(consulta, cancellationToken);
        if (resumen is null)
            return Results.NotFound();

        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Resumen");

        hoja.Cell(1, 1).Value = $"Resumen — {resumen.ClienteNombre} — {Pages.FacturacionPorClienteEmpresarial.NombreMes(mes)} {anyo}";
        // Referencia calificada: el namespace CaeManager.Web.Features.Facturacion
        // y la página Facturacion comparten nombre — "Pages.Facturacion" desambigua.
        hoja.Range(1, 1, 1, 4).Merge().Style.Font.Bold = true;

        hoja.Cell(3, 1).Value = "Concepto";
        hoja.Cell(3, 2).Value = "Unidades";
        hoja.Cell(3, 3).Value = "Precio unitario";
        hoja.Cell(3, 4).Value = "Subtotal";
        hoja.Row(3).Style.Font.Bold = true;

        var fila = 4;
        foreach (var linea in resumen.Lineas)
        {
            hoja.Cell(fila, 1).Value = linea.ConceptoNombre;
            hoja.Cell(fila, 2).Value = linea.Unidades;
            hoja.Cell(fila, 3).Value = linea.PrecioUnitario;
            hoja.Cell(fila, 4).Value = linea.Subtotal;
            fila++;
        }

        // Una fila de total por moneda: sumar subtotales de monedas distintas
        // en una sola cifra (100 EUR + 50 USD = "150") no significa nada.
        foreach (var total in resumen.TotalesPorMoneda)
        {
            hoja.Cell(fila, 1).Value = resumen.TotalesPorMoneda.Count == 1
                ? "Total estimado"
                : $"Total estimado en {total.MonedaIso}";
            hoja.Cell(fila, 1).Style.Font.Bold = true;
            hoja.Cell(fila, 4).Value = total.Total;
            hoja.Cell(fila, 4).Style.Font.Bold = true;
            fila++;
        }

        hoja.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        libro.SaveAs(stream);

        var (cliente, _, _, _) = consulta;
        await registroExportacion.RegistrarAsync(
            nameof(CaeManager.Domain.Facturacion.TarifaCliente), resumen.Lineas.Count,
            CriteriosExportacion.Desde(
                ("listado", "resumenFacturacion"),
                ("cliente", cliente.ToString()),
                ("anyo", anyo.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("mes", mes.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            cancellationToken);

        return Results.File(
            stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"facturacion-{resumen.ClienteNombre}-{anyo}-{mes:00}.xlsx");
    }

    /// <summary>
    /// El Excel de «Facturación» es lo que la pantalla enseña ese mes: se compone con las mismas consultas y el mismo
    /// <see cref="FacturacionOperadorModelo"/>, así que no puede divergir del Cuadro de dirección. Sin importes para quien no
    /// puede verlos (Coordinador CAE: solo unidades). No lleva datos personales: el Trabajador extranjero no sale aquí.
    /// </summary>
    public static async Task<IResult> ExportarCuotaDelOperadorAsync(
        int anyo, int mes, HttpContext contexto, IMediator mediator, TimeProvider reloj, [Microsoft.AspNetCore.Mvc.FromServices] IRegistroExportacionService registroExportacion,
        Microsoft.Extensions.Localization.IStringLocalizer<Recursos.TextosFacturacion> textos, CancellationToken cancellationToken)
    {
        if (!CaeManager.Application.Dashboard.Queries.MesCuadroDireccion.EsValido(anyo, mes))
            return Results.BadRequest();

        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;
        if (new CaeManager.Application.Dashboard.Queries.MesCuadroDireccion(anyo, mes).InicioUtc > ahoraUtc)
            return Results.BadRequest();

        if (await ConstruirModeloAsync(mediator, anyo, mes, ahoraUtc, cancellationToken) is not { } modelo)
            return Results.Forbid();

        var nota = modelo.TenantsSinAlcanceCompleto > 0
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, textos["ExcelNotaSinAlcance"].Value, modelo.TenantsSinAlcanceCompleto)
            : null;
        var nombreMes = $"{textos[$"Mes{mes}"].Value} {anyo.ToString(System.Globalization.CultureInfo.CurrentCulture)}";

        var (libro, _) = await Exportacion.LibroDeListado.EscribirAsync(
            textos["ExcelHoja"].Value,
            [
                textos["ExcelColEmpresa"].Value, textos["ExcelColMes"].Value, textos["ExcelColEstado"].Value,
                textos["ExcelColExtranjeros"].Value, textos["ExcelColImporteExtranjeros"].Value,
                textos["ExcelColCentros"].Value, textos["ExcelColImporteCentros"].Value,
                textos["ExcelColObras"].Value, textos["ExcelColImporteObras"].Value,
                textos["ExcelColCuota"].Value, textos["ExcelColMoneda"].Value,
            ],
            FilasAsync(modelo, nombreMes, textos, nota), cancellationToken);

        // El libro lleva lo que el Operador CAE cobra a cada Empresa gestionada: dato comercial que solo ven el Administrador
        // del Tenant y la Dirección CAE. Se deja el rastro de la descarga (filas = Empresas, no el total ni la nota) y no se cachea.
        await registroExportacion.RegistrarAsync(
            nameof(CaeManager.Domain.Dashboard.TarifaOperadorCae), modelo.Empresas.Count,
            CriteriosExportacion.Desde(
                ("listado", "cuotaOperador"),
                ("anyo", anyo.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("mes", mes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("importesOcultos", modelo.ImportesOcultos ? "true" : null),
                ("tenantsSinAlcanceCompleto", modelo.AlcanceParcial
                    ? modelo.TenantsSinAlcanceCompleto.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null)),
            cancellationToken);

        CabecerasArchivoSensible.ProhibirCache(contexto);
        return Results.File(libro, Exportacion.LibroDeListado.TipoContenido, $"facturacion-cuota-operador-{anyo:0000}-{mes:00}.xlsx");
    }

    /// <summary>
    /// El modelo de «Facturación» de un mes, compuesto con las mismas consultas que la pantalla; null si quien pide no puede
    /// leer el Cuadro de dirección.
    /// </summary>
    private static async Task<ModeloFacturacion?> ConstruirModeloAsync(
        IMediator mediator, int anyo, int mes, DateTime ahoraUtc, CancellationToken cancellationToken)
    {
        try
        {
            var serie = await mediator.Send(
                new CaeManager.Application.Dashboard.Queries.ObtenerSerieCuadroDireccionQuery(anyo, mes, 1, ConFacturacion: true), cancellationToken);
            // Una sola fuente de vigencia (la del Cuadro de dirección): las Empresas gestionadas del mes y sus tramos. El conteo de
            // extranjeros se acota a esos tramos, igual que la cifra del Cuadro, para que cifra, Excel e importe coincidan.
            var gestionadas = await mediator.Send(
                new CaeManager.Application.Dashboard.Queries.ObtenerEmpresasGestionadasDelMesQuery(anyo, mes), cancellationToken);
            var extranjeros = await mediator.Send(
                new CaeManager.Application.Dashboard.Queries.ContarExtranjerosCobrablesDelMesQuery(
                    anyo, mes, gestionadas.EsOperadorCae ? gestionadas.Tramos : null), cancellationToken);
            var tarifas = await mediator.Send(
                new CaeManager.Application.Dashboard.TarifasOperadorCae.ObtenerTarifasOperadorCaeQuery(), cancellationToken);
            return FacturacionOperadorModelo.Construir(anyo, mes, serie, extranjeros, tarifas, gestionadas.Tramos.Keys.ToList(), ahoraUtc);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// La liquidación mensual del Operador CAE externo a cada Tenant beneficiario, en CSV para importarla en el programa de
    /// facturación del emisor (V-2 y D-F3, 2026-11-14). Es un documento interno del emisor y no una factura: no lleva
    /// número de factura, ni serie, ni datos fiscales, y cada fila dice que no tiene valor fiscal. La factura la emite el
    /// Operador CAE con su propio programa; aquí solo se le dan las líneas (Empresa, concepto, unidades, precio e importe,
    /// los mismos que enseña la pantalla, por <see cref="FacturacionOperadorModelo"/>) y la marca de cobro que anotó.
    ///
    /// <para>
    /// Solo un mes terminado, y solo para quien ve importes (Administrador o Dirección CAE del Operador, D-F9): un
    /// Coordinador CAE recibe 403 porque, sin importes, el fichero no sirve para facturar. Formato pensado para hojas de
    /// cálculo y programas de facturación en español: UTF-8 con BOM, separador punto y coma, coma decimal y sin separador de
    /// miles.
    /// </para>
    /// </summary>
    public static async Task<IResult> ExportarLiquidacionDelOperadorAsync(
        int anyo, int mes, HttpContext contexto, IMediator mediator, TimeProvider reloj, [Microsoft.AspNetCore.Mvc.FromServices] IRegistroExportacionService registroExportacion,
        Microsoft.Extensions.Localization.IStringLocalizer<Recursos.TextosFacturacion> textos, CancellationToken cancellationToken)
    {
        if (!CaeManager.Application.Dashboard.Queries.MesCuadroDireccion.EsValido(anyo, mes))
            return Results.BadRequest();

        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;
        if (new CaeManager.Application.Dashboard.Queries.MesCuadroDireccion(anyo, mes).FinUtc > ahoraUtc)
            return Results.BadRequest();

        if (await ConstruirModeloAsync(mediator, anyo, mes, ahoraUtc, cancellationToken) is not { } modelo || modelo.ImportesOcultos)
            return Results.Forbid();

        var cobros = await mediator.Send(
            new CaeManager.Application.Facturacion.LiquidacionesOperadorCae.ObtenerCobrosLiquidacionOperadorCaeQuery(anyo, mes), cancellationToken);
        var csv = LiquidacionOperadorCsv.Escribir(modelo, cobros, textos);

        await registroExportacion.RegistrarAsync(
            nameof(CaeManager.Domain.Facturacion.CobroLiquidacionOperadorCae), modelo.Empresas.Count,
            CriteriosExportacion.Desde(
                ("listado", "liquidacionOperador"),
                ("anyo", anyo.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("mes", mes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("tenantsSinAlcanceCompleto", modelo.AlcanceParcial
                    ? modelo.TenantsSinAlcanceCompleto.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null)),
            cancellationToken);

        CabecerasArchivoSensible.ProhibirCache(contexto);
        return Results.File(csv, LiquidacionOperadorCsv.TipoContenido, $"liquidacion-interna-operador-{anyo:0000}-{mes:00}.csv");
    }

    private static string EstadoTexto(
        CaeManager.Application.Dashboard.Queries.EstadoCifraMensual estado,
        Microsoft.Extensions.Localization.IStringLocalizer<Recursos.TextosFacturacion> textos)
    {
        return estado switch
        {
            CaeManager.Application.Dashboard.Queries.EstadoCifraMensual.Existe => textos["EstadoCerrado"].Value,
            CaeManager.Application.Dashboard.Queries.EstadoCifraMensual.Estimado => textos["EstadoSinCerrar"].Value,
            _ => textos["EstadoEnCurso"].Value,
        };
    }

    private static XLCellValue Unidades(int? unidades, Microsoft.Extensions.Localization.IStringLocalizer<Recursos.TextosFacturacion> textos) =>
        unidades is { } u ? u : textos["SinDato"].Value;

    private static XLCellValue Importe(
        EmpresaFacturada empresa, ConceptoFacturado concepto, Microsoft.Extensions.Localization.IStringLocalizer<Recursos.TextosFacturacion> textos)
    {
        if (empresa.ImportesOcultos)
            return string.Empty;
        return concepto.Estado switch
        {
            EstadoTarifaConcepto.Cobra when concepto.Importe is { } importe => importe,
            EstadoTarifaConcepto.NoSeCobra => textos["NoSeCobra"].Value,
            EstadoTarifaConcepto.Cobra => string.Empty,
            EstadoTarifaConcepto.SinDato when concepto.Importe is { } importe => importe,
            EstadoTarifaConcepto.SinDato => textos["SinDato"].Value,
            _ => textos["SinTarifa"].Value,
        };
    }

    private static async IAsyncEnumerable<IReadOnlyList<XLCellValue>> FilasAsync(
        ModeloFacturacion modelo, string nombreMes,
        Microsoft.Extensions.Localization.IStringLocalizer<Recursos.TextosFacturacion> textos, string? nota)
    {
        foreach (var e in modelo.Empresas)
        {
            var cuota = e.ImportesOcultos
                ? (XLCellValue)string.Empty
                : e.SinTarifa ? (XLCellValue)textos["SinTarifa"].Value
                : e.SinTarifaEnElMes ? (XLCellValue)textos["SinTarifaEnElMes"].Value
                : e.Cuota.Count == 1 ? (XLCellValue)e.Cuota[0].Importe
                : (XLCellValue)string.Join(" + ", e.Cuota.Select(c => $"{c.Importe} {c.Moneda}"));
            var extranjeros = e.Concepto(CaeManager.Domain.Dashboard.ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero);
            var centros = e.Concepto(CaeManager.Domain.Dashboard.ConceptoTarifaOperadorCae.CentroNuevo);
            var obras = e.Concepto(CaeManager.Domain.Dashboard.ConceptoTarifaOperadorCae.ObraNueva);
            yield return
            [
                e.Nombre, nombreMes, EstadoTexto(e.Estado, textos),
                Unidades(extranjeros.Unidades, textos), Importe(e, extranjeros, textos),
                Unidades(centros.Unidades, textos), Importe(e, centros, textos),
                Unidades(obras.Unidades, textos), Importe(e, obras, textos),
                cuota, string.Join(" + ", e.Cuota.Select(c => c.Moneda)),
            ];
        }

        if (!modelo.ImportesOcultos)
        {
            foreach (var total in modelo.Total)
                yield return [modelo.AlcanceParcial ? textos["ExcelTotalParcial"].Value : textos["TotalMes"].Value, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, total.Importe, total.Moneda];
        }

        if (nota is not null)
        {
            yield return [];
            yield return [nota];
        }

        await Task.CompletedTask;
    }
}
