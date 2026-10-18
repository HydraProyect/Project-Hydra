using System.Globalization;
using Bunit;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentacionBaseTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Trabajadores.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Regla 8 de LEEME-AUTORIDAD (Chris, 2026-10-18): toda fila o documento que pide trabajo del Gestor CAE ofrece una sola
/// acción, «Gestionar», aunque por dentro sea renovar, subir o confirmar. Aquí se fija en las piezas compartidas de las
/// fichas 360: el rótulo único, qué estados piden trabajo y, por rol, quién ve el botón (Consulta y Cliente, nunca).
/// </summary>
public class GestionarUnicoFichas360Tests : BunitContext
{
    private sealed class MediadorQueNoSeUsa : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    public GestionarUnicoFichas360Tests()
    {
        Services.AddLocalization();
        Services.AddSingleton<IMediator>(new MediadorQueNoSeUsa());
    }

    private static readonly Guid Vencido = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SinConfirmar = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Proximo = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid ProximoUrgente = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    private static readonly Guid Vigente = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");

    private static DocumentacionBaseTrabajadorDto Base() => new(
    [
        new(TipoDocumentoBase.AptitudMedica, EstadoIndicadorBase.Vencido, Vencido, null),
        new(TipoDocumentoBase.FormacionArt19, EstadoIndicadorBase.SinConfirmar, SinConfirmar, null),
        new(TipoDocumentoBase.EntregaEpi, EstadoIndicadorBase.ProximoAVencer, ProximoUrgente, null),
        new(TipoDocumentoBase.InformacionArt18, EstadoIndicadorBase.Vigente, Vigente, null),
    ]);

    private IRenderedComponent<PanelDocumentacionBase> Panel(List<Guid> corregidos) => Render<PanelDocumentacionBase>(p => p
        .Add(c => c.Datos, Base())
        .Add(c => c.DocumentosUrgentes, new HashSet<Guid> { ProximoUrgente })
        .Add(c => c.OnCorregirDocumento, EventCallback.Factory.Create<Guid>(this, corregidos.Add)));

    private static List<string> Botones(AngleSharp.Dom.IElement fila) => fila.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList();

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    public async Task Documentacion_base_ofrece_a_quien_escribe_una_sola_accion_Gestionar_en_lo_que_pide_trabajo(string rol)
    {
        this.ConRolDeEscritura(rol);
        var corregidos = new List<Guid>();
        var cut = Panel(corregidos);

        var filas = cut.FindAll(".panel-doc-base-fila");
        filas.Select(Botones).Should().Equal(
            new List<string>[] { ["Gestionar"], ["Gestionar"], ["Gestionar"], [] },
            (a, b) => a.SequenceEqual(b),
            "Vencido, Sin confirmar y Por vencer urgente piden trabajo; Vigente no lleva botón");

        var total = cut.FindAll(".panel-doc-base-fila button").Count;
        for (var i = 0; i < total; i++)
        {
            // Cada clic vuelve a pintar el panel: se busca el botón de nuevo en cada vuelta.
            var boton = cut.FindAll(".panel-doc-base-fila button")[i];
            boton.GetAttribute("aria-label")!.Should().StartWith("Gestionar", "el nombre accesible contiene el texto visible (WCAG 2.5.3)")
                .And.Contain("documento");
            await boton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        }

        corregidos.Should().Equal(
            [Vencido, SinConfirmar, ProximoUrgente],
            "«Gestionar» abre el mismo editor que abría el verbo de antes, para el documento de su fila");
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public void Documentacion_base_no_ofrece_ninguna_accion_de_escritura_a_Consulta_ni_a_Cliente(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Panel([]);

        cut.FindAll(".panel-doc-base-fila").Should().HaveCount(4);
        cut.FindAll(".panel-doc-base-fila button").Should().BeEmpty();
        cut.Markup.Should().NotContain("Gestionar");
    }

    [Fact]
    public void Un_Proximo_que_no_es_urgente_no_gana_boton_con_el_cambio_de_rotulo()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var cut = Render<PanelDocumentacionBase>(p => p
            .Add(c => c.Datos, new DocumentacionBaseTrabajadorDto(
                [new(TipoDocumentoBase.EntregaEpi, EstadoIndicadorBase.ProximoAVencer, Proximo, null)]))
            .Add(c => c.OnCorregirDocumento, EventCallback.Factory.Create<Guid>(this, _ => { })));

        cut.FindAll(".panel-doc-base-fila button").Should().BeEmpty();
    }

    [Theory]
    [InlineData("es-ES", "Gestionar")]
    [InlineData("ca-ES", "Gestionar")]
    public void El_rotulo_es_siempre_Gestionar_en_los_dos_idiomas(string cultura, string esperado)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(cultura);
            EstadoDocumentoFicha360.TextoAccion.Should().Be(esperado);
            EstadoDocumentoFicha360.TextoAccionAccesible("Aptitud médica").Should().StartWith("Gestionar").And.Contain("Aptitud médica");
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido, true)]
    [InlineData(EstadoDocumento.EnTolerancia, true)]
    [InlineData(EstadoDocumento.Urgente, true)]
    [InlineData(EstadoDocumento.Faltante, true)]
    [InlineData(EstadoDocumento.SinConfirmar, true)]
    [InlineData(EstadoDocumento.Proximo, false)]
    [InlineData(EstadoDocumento.Vigente, false)]
    [InlineData(EstadoDocumento.SinCaducidad, false)]
    public void Solo_los_estados_que_piden_trabajo_tienen_accion(EstadoDocumento estado, bool pide)
    {
        (EstadoDocumentoFicha360.Accion(estado) is not null).Should().Be(pide);
    }
}
