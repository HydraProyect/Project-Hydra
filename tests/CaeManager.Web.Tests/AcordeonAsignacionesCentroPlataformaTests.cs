using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

public partial class AcordeonAsignacionesCentroVigenciaSinConfirmarTests
{
    [Theory]
    [InlineData(EstadoAcreditacion.Rechazada, true, "Rechazado")]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, true, "Pendiente de subir")]
    [InlineData(EstadoAcreditacion.Subida, false, "Pendiente de validación")]
    [InlineData(EstadoAcreditacion.Aceptada, false, "Validado")]
    [InlineData(EstadoAcreditacion.NoRequerida, false, "No exigido")]
    public async Task Centro_360_separa_estado_plataforma_y_vigencia_en_Talveg(EstadoAcreditacion acreditacion, bool bloquea, string texto)
    {
        var trabajador = new TrabajadorAsignacionDocumentacionDto(Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana",
            new DateOnly(2026, 1, 15), EstadoDocumento.Vigente,
            [new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Formación PRL", EstadoDocumento.Vigente,
                new DateOnly(2027, 1, 1), AcreditacionesEnElCentro: [new AcreditacionResumenDto(Guid.NewGuid(), "Nalanda", acreditacion)])],
            CumplimientoDocumental.Evaluar([EstadoDocumento.Vigente]));
        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(c => c.CentroId, Guid.NewGuid()).Add(c => c.MostrarEstadoPlataforma, true));
        cut.WaitForAssertion(() => cut.FindAll(".fila-relacion").Should().NotBeEmpty());
        await cut.Find("button.fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
        var celdas = cut.FindAll(".fila-plataforma-centro [role=cell]");
        celdas.Should().HaveCount(5, "Documento, Estado en la plataforma, Caducidad, Bloquea y Acción (bloque 3 del 21-10)");
        celdas[0].QuerySelector(".indicador-talveg")!.GetAttribute("title").Should().Contain("Vigente");
        celdas[1].TextContent.Should().Contain("Nalanda").And.Contain(texto);
        celdas[3].TextContent.Trim().Should().Be(bloquea ? "Sí" : "No");
        cut.FindAll(".fila-plataforma-bloquea").Count.Should().Be(bloquea ? 1 : 0);
    }

    [Fact]
    public async Task Centro_360_no_infiere_validacion_plataforma_de_documento_vigente()
    {
        var trabajador = new TrabajadorAsignacionDocumentacionDto(Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana",
            new DateOnly(2026, 1, 15), EstadoDocumento.Vigente,
            [new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Formación PRL", EstadoDocumento.Vigente, null)],
            CumplimientoDocumental.Evaluar([EstadoDocumento.Vigente]));
        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(c => c.CentroId, Guid.NewGuid()).Add(c => c.MostrarEstadoPlataforma, true));
        cut.WaitForAssertion(() => cut.FindAll(".fila-relacion").Should().NotBeEmpty());
        await cut.Find("button.fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
        var celdas = cut.FindAll(".fila-plataforma-centro [role=cell]");
        celdas[1].TextContent.Should().Contain("Sin datos de plataforma");
        celdas[3].TextContent.Trim().Should().Be("—");
        cut.Find(".fila-trabajador-fraccion").TextContent.Should().Be("1/1");
    }
}
