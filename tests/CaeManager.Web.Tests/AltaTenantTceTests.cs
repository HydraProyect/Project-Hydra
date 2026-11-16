using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Comercial.Commands.ContratarLineaInbound;
using CaeManager.Application.Inbound.Comercial.Commands.CorregirLineaInbound;
using CaeManager.Application.Inbound.Comercial.Commands.DarDeBajaLineaInbound;
using CaeManager.Application.Plataforma.Queries.ObtenerIdentidadPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerTenantObjetivoSesion;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.Plataforma.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Tarjeta 31, «Alta del Tenant TCE» (Actor de Plataforma TALVEG): rótulos, pasos, shortcuts de B-04 y B-04b, la puerta de la
/// Sesión Privilegiada de Aprovisionamiento y el envío de los tres comandos de la línea Inbound. La autoridad real la prueban
/// los tests de Application de los comandos; aquí solo que la pantalla no ofrece acciones fuera de esa sesión, ni con
/// impersonación, y que manda lo que el Actor de Plataforma introduce.
/// </summary>
public class AltaTenantTceTests : BunitContext
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid ProductoId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    private readonly MediatorFalso _mediator = new();

    public AltaTenantTceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddSingleton<IMediator>(_mediator);
    }

    private static IdentidadPlataformaDto Identidad(CapacidadPrivilegio? capacidad, Guid? simulado = null) =>
        new(Guid.NewGuid(), simulado, capacidad is null ? TipoViaAcceso.Normal : TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid(), capacidad);

    private IRenderedComponent<AltaTenantTce> RenderConSesion()
    {
        _mediator.Identidad = Identidad(CapacidadPrivilegio.Aprovisionamiento);
        return Render<AltaTenantTce>();
    }

    private static void Pulsar(IRenderedComponent<AltaTenantTce> cut, string texto) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto).Click();

    private static void IrAlPasoProducto(IRenderedComponent<AltaTenantTce> cut)
    {
        Pulsar(cut, "Continuar");
        Pulsar(cut, "Continuar");
    }

    [Fact]
    public void Sin_Sesion_Privilegiada_de_Aprovisionamiento_no_ofrece_ninguna_accion()
    {
        _mediator.Identidad = Identidad(CapacidadPrivilegio.SoporteLectura);

        var cut = Render<AltaTenantTce>();

        cut.Markup.Should().Contain("Alta del Tenant TCE").And.Contain("Hace falta una Sesión Privilegiada de Aprovisionamiento");
        cut.Find("a[href='/delegaciones']").Should().NotBeNull();
        cut.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().NotContain(["Dar de alta", "Dar de baja", "Editar", "Continuar"]);
        _mediator.Enviados.OfType<ObtenerTenantObjetivoSesionQuery>().Should().BeEmpty("sin sesión no lee el Tenant objetivo");
    }

    [Fact]
    public void Sin_Tenant_activo_resuelto_no_ofrece_ninguna_accion_aunque_la_capacidad_sea_Aprovisionamiento()
    {
        _mediator.Identidad = Identidad(CapacidadPrivilegio.Aprovisionamiento);
        _mediator.Tenant = null;

        var cut = Render<AltaTenantTce>();

        cut.Markup.Should().Contain("No encontramos el Tenant activo de la Sesión Privilegiada")
            .And.NotContain("Hace falta una Sesión Privilegiada", "la sesión existe: no se le pide que abra otra");
        cut.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().NotContain(["Dar de alta", "Dar de baja", "Editar", "Continuar"]);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("2")]
    [InlineData("x")]
    public void Un_valor_de_tramo_que_no_es_un_nombre_del_enum_cuenta_como_sin_elegir(string valorDelDom)
    {
        var cut = RenderConSesion();
        IrAlPasoProducto(cut);
        cut.Find("#alta-pagador").Input("cus_Delta123");
        cut.Find("#alta-modalidad").Change(nameof(ModalidadSuscripcionAlta.Mensual));

        cut.Find("#alta-tramo").Change(valorDelDom);
        Pulsar(cut, "Continuar");

        cut.Find("[role='alert']").TextContent.Should().Contain("Elige el tramo y la modalidad");
    }

    [Fact]
    public void Con_el_alta_en_vuelo_el_resumen_no_se_edita_y_la_confirmacion_cuenta_lo_enviado()
    {
        var respuesta = new TaskCompletionSource<Result<Guid>>();
        _mediator.RespuestaContratarTarea = respuesta.Task;
        var cut = RenderConSesion();
        IrAlPasoProducto(cut);
        cut.Find("#alta-pagador").Input("cus_Delta123");
        cut.Find("#alta-tramo").Change(nameof(TramoTrabajadoresActivos.De11A40));
        cut.Find("#alta-modalidad").Change(nameof(ModalidadSuscripcionAlta.Mensual));
        Pulsar(cut, "Continuar");
        Pulsar(cut, "Continuar");

        Pulsar(cut, "Dar de alta");

        cut.FindAll(".alta-tce-seccion button").Should().HaveCount(4)
            .And.OnlyContain(b => b.HasAttribute("disabled"), "mientras se contrata no se vuelve a editar lo enviado");

        respuesta.SetResult(Result.Exito(ProductoId));
        cut.WaitForAssertion(() => cut.Find(".alta-tce-ok").TextContent.Should()
            .Contain("Pagador TALVEG: cus_Delta123").And.Contain("De 11 a 40 Trabajadores activos").And.Contain("Mensual"));
    }

    [Fact]
    public void Con_impersonacion_no_ofrece_ninguna_accion_aunque_la_capacidad_sea_Aprovisionamiento()
    {
        _mediator.Identidad = Identidad(CapacidadPrivilegio.Aprovisionamiento, simulado: Guid.NewGuid());

        var cut = Render<AltaTenantTce>();

        cut.Markup.Should().Contain("No disponible durante una impersonación");
        cut.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().NotContain(["Dar de alta", "Dar de baja", "Editar", "Continuar"]);
    }

    [Fact]
    public void Con_sesion_pinta_rotulos_pasos_y_shortcuts_de_B04_y_B04b()
    {
        var cut = RenderConSesion();

        cut.Markup.Should().Contain("Plataforma · Tenants").And.Contain("Alta hecha por el Actor de Plataforma TALVEG.");
        foreach (var paso in new[] { "Tenant del Cliente", "Operación", "Producto Contratado", "Administrador", "Resumen" })
            cut.Find("ol.indicador-pasos").TextContent.Should().Contain(paso);
        cut.Find("[data-campo='tenant']").TextContent.Should().Be("Logística Delta Sur S.A.");
        cut.Find("[data-shortcut='B-04']").Should().NotBeNull();
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain(["Cancelar", "Continuar"]).And.NotContain("Atrás");

        Pulsar(cut, "Continuar");
        cut.Find("[data-shortcut='B-04b']").Should().NotBeNull();
        cut.FindAll("input[name='alta-tce-operacion']").Should().OnlyContain(i => i.HasAttribute("disabled"));

        Pulsar(cut, "Continuar");
        cut.Markup.Should().Contain("Pagador TALVEG ≠ Tenant propietario").And.Contain("Desde que se da de alta");
        cut.FindAll("#alta-tramo option").Should().HaveCount(6, "cinco tramos y «Elige una opción», sin tramo por defecto");
        cut.FindAll("#alta-modalidad option").Select(o => o.TextContent).Should().Equal("Elige una opción", "Mensual", "Anual");
    }

    [Fact]
    public void No_pasa_del_Producto_Contratado_sin_un_Customer_de_Stripe_valido()
    {
        var cut = RenderConSesion();
        IrAlPasoProducto(cut);

        cut.Find("#alta-pagador").Input("cus con espacios");
        Pulsar(cut, "Continuar");

        cut.Find("[role='alert']").TextContent.Should().Contain("Indica el Customer de Stripe del Pagador TALVEG");
        cut.Find("#alta-pagador").Should().NotBeNull("sigue en el paso del Producto Contratado");
    }

    [Fact]
    public void Dar_de_alta_manda_ContratarLineaInbound_con_lo_introducido_y_confirma()
    {
        _mediator.RespuestaContratar = Result.Exito(ProductoId);
        var cut = RenderConSesion();
        IrAlPasoProducto(cut);
        cut.Find("#alta-pagador").Input("cus_Delta123");
        cut.Find("#alta-tramo").Change(nameof(TramoTrabajadoresActivos.De41A80));
        cut.Find("#alta-modalidad").Change(nameof(ModalidadSuscripcionAlta.Anual));
        Pulsar(cut, "Continuar");
        Pulsar(cut, "Continuar");

        cut.Markup.Should().Contain("De 41 a 80 Trabajadores activos").And.Contain("cus_Delta123");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain(["Atrás", "Dar de alta", "Editar"]).And.NotContain("Continuar");
        Pulsar(cut, "Dar de alta");

        _mediator.Enviados.OfType<ContratarLineaInboundCommand>().Should().ContainSingle()
            .Which.Should().Be(new ContratarLineaInboundCommand("cus_Delta123", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual));
        cut.Find(".alta-tce-ok").TextContent.Should().Contain("Logística Delta Sur S.A. dado de alta");
    }

    [Fact]
    public void Si_la_linea_ya_esta_contratada_muestra_el_error_del_comando_y_la_pista_de_editar()
    {
        _mediator.RespuestaContratar = Result.Fallo<Guid>(ErroresContratarLineaInbound.YaContratada);
        var cut = RenderConSesion();
        IrAlPasoProducto(cut);
        cut.Find("#alta-pagador").Input("cus_Delta123");
        cut.Find("#alta-tramo").Change(nameof(TramoTrabajadoresActivos.De1A10));
        cut.Find("#alta-modalidad").Change(nameof(ModalidadSuscripcionAlta.Mensual));
        Pulsar(cut, "Continuar");
        Pulsar(cut, "Continuar");
        Pulsar(cut, "Dar de alta");

        cut.Find(".alta-tce-cuerpo [role='alert']").TextContent.Should()
            .Contain(ErroresContratarLineaInbound.YaContratada.Mensaje).And.Contain("edita la línea Inbound vigente");
    }

    [Fact]
    public void Cancelar_vuelve_al_primer_paso_sin_mandar_nada()
    {
        var cut = RenderConSesion();
        IrAlPasoProducto(cut);

        Pulsar(cut, "Cancelar");

        cut.Markup.Should().Contain("Alta cancelada: no se ha creado ningún Producto Contratado.");
        cut.Find("[data-campo='tenant']").Should().NotBeNull();
        _mediator.Enviados.OfType<ContratarLineaInboundCommand>().Should().BeEmpty();
    }

    [Fact]
    public void Editar_y_Guardar_mandan_CorregirLineaInbound()
    {
        _mediator.RespuestaCorregir = Result.Exito(ProductoId);
        var cut = RenderConSesion();

        cut.Find(".alta-tce-linea").TextContent.Should().Contain("Línea Inbound vigente");
        cut.FindAll(".alta-tce-linea button").First(b => b.TextContent.Trim() == "Editar").Click();
        cut.Find("#corr-pagador").Input("cus_Nuevo9");
        cut.Find("#corr-modalidad").Change(nameof(ModalidadSuscripcionAlta.Anual));
        cut.FindAll(".alta-tce-linea button").First(b => b.TextContent.Trim() == "Guardar").Click();

        _mediator.Enviados.OfType<CorregirLineaInboundCommand>().Should().BeEmpty("sin tramo elegido no se manda ninguno por defecto");
        cut.Find(".alta-tce-linea [role='alert']").TextContent.Should().Contain("Elige el tramo y la modalidad");

        cut.Find("#corr-tramo").Change(nameof(TramoTrabajadoresActivos.De41A80));
        cut.FindAll(".alta-tce-linea button").First(b => b.TextContent.Trim() == "Guardar").Click();

        _mediator.Enviados.OfType<CorregirLineaInboundCommand>().Should().ContainSingle()
            .Which.Should().Be(new CorregirLineaInboundCommand("cus_Nuevo9", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual));
        cut.Find(".alta-tce-linea [role='status']").TextContent.Should().Contain("Línea Inbound corregida");
    }

    [Fact]
    public void Dar_de_baja_pide_confirmacion_y_manda_DarDeBajaLineaInbound()
    {
        _mediator.RespuestaBaja = Result.Fallo<Guid>(ErroresDarDeBajaLineaInbound.SinLineaVigente);
        var cut = RenderConSesion();

        cut.FindAll(".alta-tce-linea button").First(b => b.TextContent.Trim() == "Dar de baja").Click();
        _mediator.Enviados.OfType<DarDeBajaLineaInboundCommand>().Should().BeEmpty("primero pide confirmación");
        cut.Markup.Should().Contain("La línea Inbound de Logística Delta Sur S.A. deja de estar vigente desde ahora.");
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Dar de baja").Click();

        _mediator.Enviados.OfType<DarDeBajaLineaInboundCommand>().Should().ContainSingle();
        cut.Find(".alta-tce-linea [role='alert']").TextContent.Should().Contain(ErroresDarDeBajaLineaInbound.SinLineaVigente.Mensaje);
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Enviados { get; } = [];
        public IdentidadPlataformaDto? Identidad { get; set; }
        public ClienteAutorizadoDto? Tenant { get; set; } = new(TenantId, "Logística Delta Sur S.A.", EsOrigen: false, LogoVersion: null);
        public Result<Guid> RespuestaContratar { get; set; } = Result.Exito(Guid.Empty);
        public Task<Result<Guid>>? RespuestaContratarTarea { get; set; }
        public Result<Guid> RespuestaCorregir { get; set; } = Result.Exito(Guid.Empty);
        public Result<Guid> RespuestaBaja { get; set; } = Result.Exito(Guid.Empty);

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            object? respuesta = request switch
            {
                ObtenerIdentidadPlataformaQuery => Identidad,
                ObtenerTenantObjetivoSesionQuery => Tenant,
                ContratarLineaInboundCommand when RespuestaContratarTarea is { } tarea => tarea,
                ContratarLineaInboundCommand => RespuestaContratar,
                CorregirLineaInboundCommand => RespuestaCorregir,
                DarDeBajaLineaInboundCommand => RespuestaBaja,
                _ => throw new InvalidOperationException($"Petición inesperada: {request.GetType().Name}"),
            };
            return respuesta is Task<Result<Guid>> pendiente
                ? (Task<TResponse>)(object)pendiente
                : Task.FromResult((TResponse)respuesta!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
            => throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
