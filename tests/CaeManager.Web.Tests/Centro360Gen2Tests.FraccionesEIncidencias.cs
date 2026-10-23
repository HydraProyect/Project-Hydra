using System.Globalization;
using Bunit;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

public partial class Centro360Gen2Tests
{
    [Theory]
    [InlineData("es-ES")]
    [InlineData("ca-ES")]
    public void La_fraccion_de_un_Trabajador_sin_documentos_al_dia_se_escribe_con_barra(string cultura)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var id = Guid.NewGuid();
            Registrar(new MediatorFalso()).Asignaciones[id] =
            [
                Asignado("Ana Alba", EstadoDocumento.Faltante,
                    Documento("Formación PRL", EstadoDocumento.Faltante),
                    Documento("Aptitud médica", EstadoDocumento.Faltante),
                    Documento("Contrato de trabajo", EstadoDocumento.Faltante),
                    Documento("Alta en Seguridad Social", EstadoDocumento.Faltante))
            ];

            var cut = RenderizarAcordeon(id);

            cut.Find(".fila-trabajador-fraccion").TextContent.Trim().Should().Be("0/4");
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("ca-ES")]
    public async Task La_fraccion_de_un_Vehiculo_sin_documentos_validados_se_escribe_con_barra(string cultura)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var id = Guid.NewGuid();
            var (cut, _) = RenderizarConVehiculos(id,
                VehiculoCentro("4821KLM", "Furgón Azul", "Montajes Jalón", documentos:
                [
                    DocumentoEnPlataforma("ITV", EstadoAcreditacion.Subida),
                    DocumentoEnPlataforma("Seguro obligatorio", EstadoAcreditacion.Subida),
                    DocumentoEnPlataforma("Permiso de circulación", EstadoAcreditacion.Subida),
                    DocumentoEnPlataforma("Ficha técnica", EstadoAcreditacion.Subida)
                ]));

            await ElegirVehiculosAsync(cut);

            cut.Find("[data-vehiculos-centro] [data-fraccion-validados]").TextContent.Trim().Should().Be("0/4");
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_pastilla_de_la_Subcontrata_cuenta_el_recurso_sin_datos_de_plataforma(bool vehiculos)
    {
        var id = Guid.NewGuid();
        var subcontrata = Guid.NewGuid();
        var sinDatos = Documento("Formación PRL", EstadoDocumento.Vigente);
        var validado = DocumentoEnPlataforma("ITV", EstadoAcreditacion.Aceptada);
        var noExigido = DocumentoEnPlataforma("Seguro", EstadoAcreditacion.NoRequerida);
        var trabajador = Asignado("Ana Alba", EstadoDocumento.Vigente, sinDatos);
        var otroTrabajador = Asignado("Leo Mas", EstadoDocumento.Vigente, validado, noExigido);
        var (cut, _) = RenderizarConVehiculos(id, m =>
        {
            m.Asignaciones[id] = [trabajador, otroTrabajador];
            m.Empleadores[id] =
            [
                new(trabajador.TrabajadorId, "Ana Alba", "Montajes Jalón", trabajador.FechaAlta,
                    SubcontrataId: subcontrata, EmpresaContratistaNombre: "Obras del Ebro S.L."),
                new(otroTrabajador.TrabajadorId, "Leo Mas", "Montajes Jalón", otroTrabajador.FechaAlta,
                    SubcontrataId: subcontrata, EmpresaContratistaNombre: "Obras del Ebro S.L.")
            ];
        },
            VehiculoCentro("4821KLM", "Furgón Azul", "Montajes Jalón", subcontrata, "Obras del Ebro S.L.", sinDatos),
            VehiculoCentro("7310JHB", "Furgón Verde", "Montajes Jalón", subcontrata, "Obras del Ebro S.L.", validado, noExigido));

        if (vehiculos) await ElegirVehiculosAsync(cut);

        var cabecera = cut.Find("[data-subcontrata-grupo] button.grupo-lista-cabecera");
        cabecera.GetAttribute("aria-expanded").Should().Be("false");
        cabecera.QuerySelector(".grupo-lista-incidencias").Should().NotBeNull();
        cabecera.QuerySelector(".grupo-lista-incidencias")!.TextContent.Trim().Should().Be("1 con incidencia");

        await cabecera.ClickAsync(new MouseEventArgs());

        cut.FindAll("[data-subcontrata-grupo] button.grupo-lista-cabecera .grupo-lista-incidencias").Should().BeEmpty();
    }
}
