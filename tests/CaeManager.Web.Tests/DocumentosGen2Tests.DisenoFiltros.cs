using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerEntidadesAsociadasDeDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class DocumentosGen2Tests
{
    [Fact]
    public async Task Consulta_filtra_por_entidad_asociada_y_exporta_el_mismo_universo_sin_escrituras()
    {
        var entidad = new EntidadAsociadaDocumentoDto(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Empresa de prueba");
        var mediador = ConDocumentos(Documento("Certificado"));
        mediador.Interceptar = q => q is ObtenerEntidadesAsociadasDeDocumentosQuery
            ? Task.FromResult<object?>(new ResultadoPaginado<EntidadAsociadaDocumentoDto>([entidad], 1, 1, 20))
            : null;
        var (cut, _) = Renderizar(mediador, rol: "Consulta");
        var navegacion = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => cut.Find(".selector-disparador"));

        await cut.Find(".selector-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".selector-opcion").Single(b => b.TextContent.Contains(entidad.Nombre)).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).PropietarioId.Should().Be(entidad.Id));
        UltimaConsultaDeDocumentos(mediador).Ambito.Should().Be(AmbitoAplicacion.Empresa);
        navegacion.Uri.Should().Contain($"Propietario={entidad.Id}").And.Contain("Ambito=Empresa");
        cut.Find(".selector-disparador").TextContent.Should().Contain("Propietario · 1");
        cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href")
            .Should().Contain($"propietario={entidad.Id}");

        await cut.Find(".selector-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".selector-opcion").Single(b => b.TextContent.Trim() == "Todos").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).PropietarioId.Should().BeNull());
        navegacion.Uri.Should().NotContain("Propietario=");

        await cut.Find(".selector-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".selector-opcion").Single(b => b.TextContent.Contains(entidad.Nombre)).ClickAsync(new MouseEventArgs());
        await cut.Find(".limpiar-filtros-barra").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() =>
        {
            UltimaConsultaDeDocumentos(mediador).PropietarioId.Should().BeNull();
            UltimaConsultaDeDocumentos(mediador).Ambito.Should().BeNull();
        });
        navegacion.Uri.Should().NotContain("Propietario=").And.NotContain("Ambito=");
        cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href").Should().NotContain("propietario=");
        mediador.Enviadas.Should().NotContain(q => q.GetType().Name.EndsWith("Command"));
    }
}
