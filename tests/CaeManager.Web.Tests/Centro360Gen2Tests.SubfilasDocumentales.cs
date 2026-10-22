using Bunit;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

public partial class Centro360Gen2Tests
{
    [Theory]
    [InlineData(EstadoDocumento.Vencido, "Aptitud médica", "Desde el 06/11/2026", false)]
    [InlineData(EstadoDocumento.EnTolerancia, "Aptitud médica", "Desde el 06/11/2026", true)]
    [InlineData(EstadoDocumento.Proximo, "Aptitud médica", "Caduca el 06/11/2026", true)]
    [InlineData(EstadoDocumento.Urgente, "Aptitud médica", "Caduca el 06/11/2026", true)]
    [InlineData(EstadoDocumento.SinConfirmar, "Contrato de trabajo", "Falta la fecha de fin", true)]
    [InlineData(EstadoDocumento.Faltante, "Aptitud médica", "Falta la fecha de vencimiento", false)]
    [InlineData(EstadoDocumento.Vigente, "Aptitud médica", "Caduca el 06/11/2026", true)]
    [InlineData(EstadoDocumento.SinCaducidad, "Aptitud médica", "Sin caducidad", true)]
    public async Task Cada_documento_conserva_nombre_estado_vigencia_y_su_unica_accion_en_la_misma_fila(
        EstadoDocumento estado, string nombre, string texto, bool gestionar)
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Asignaciones[id] =
            [Asignado("Ana Alba", estado, Documento(nombre, estado, new(2026, 11, 6)))];
        var cut = RenderizarAcordeon(id);
        await cut.Find(".lista-trabajadores-acordeon .fila-relacion-desplegar").ClickAsync(new MouseEventArgs());

        var fila = cut.Find(".documentos-trabajador-ficha .fila-documento-requerido");
        fila.Children.Should().HaveCount(4);
        fila.Children[0].TextContent.Should().Contain(nombre);
        fila.Children[1].QuerySelector(".badge").Should().NotBeNull();
        fila.Children[2].TextContent.Should().Contain(texto).And.NotBe("—");
        fila.Children[3].QuerySelectorAll("button").Should().HaveCount(gestionar ? 1 : 0);
        // Lo vencido o pendiente en TALVEG no se gestiona desde el Centro: se actualiza en la ficha 360 (regla del 2026-10-19).
        fila.Children[3].TextContent.Trim().Should().Be(gestionar ? "Gestionar" : "Actualizar en Trabajador 360");
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public async Task Consulta_conserva_el_desglose_y_los_textos_sin_acciones_de_gestion(string rol)
    {
        this.ConRolDeEscritura(rol);
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Asignaciones[id] =
            [Asignado("Ana Alba", EstadoDocumento.SinConfirmar, Documento("Contrato de trabajo", EstadoDocumento.SinConfirmar))];
        var cut = RenderizarAcordeon(id);
        await cut.Find(".lista-trabajadores-acordeon .fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
        var fila = cut.Find(".fila-documento-requerido");
        fila.TextContent.Should().Contain("Contrato de trabajo").And.Contain("Falta la fecha de fin");
        fila.QuerySelectorAll("button").Should().BeEmpty();
    }
}
