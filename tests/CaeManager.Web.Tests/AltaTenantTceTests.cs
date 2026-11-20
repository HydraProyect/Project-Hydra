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

    /// <summary>Opción A' (DECISION_LOG e496f33): el Actor de Plataforma TALVEG marca la declaración opcional «solo Inbound».</summary>
    private static void DeclararSoloInbound(IRenderedComponent<AltaTenantTce> cut) =>
        cut.Find("[data-campo='declara-solo-inbound'] input[type='checkbox']").Change(true);

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
    public void La_cabecera_es_la_compartida_con_su_antetitulo_de_plataforma()
    {
        var cut = RenderConSesion();

        cut.FindAll("header.alta-tce-cab").Should().BeEmpty("la cabecera propia pasó a CabeceraPagina");
        cut.Find("header.cabecera-pagina h1").TextContent.Should().Contain("Alta del Tenant TCE");
        cut.Find("header.cabecera-pagina .cabecera-pagina-kicker").TextContent.Should().Contain("Plataforma");
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

        DeclararSoloInbound(cut);
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
        DeclararSoloInbound(cut);
        Pulsar(cut, "Dar de alta");

        _mediator.Enviados.OfType<ContratarLineaInboundCommand>().Should().ContainSingle()
            .Which.Should().Be(new ContratarLineaInboundCommand("cus_Delta123", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual, DeclaraSoloInbound: true));
        cut.Find(".alta-tce-ok").TextContent.Should().Contain("Logística Delta Sur S.A. dado de alta");
    }

    /// <summary>
    /// Opción A' (DECISION_LOG e496f33): la declaración «solo Inbound» es opcional y nunca viene marcada. Sin marcarla, el
    /// alta se manda igual con la declaración en false, y el Tenant conserva Outbound.
    /// </summary>
    [Fact]
    public void Sin_marcar_la_declaracion_solo_Inbound_el_alta_se_manda_sin_ella()
    {
        _mediator.RespuestaContratar = Result.Exito(ProductoId);
        var cut = RenderConSesion();
        IrAlPasoProducto(cut);
        cut.Find("#alta-pagador").Input("cus_Delta123");
        cut.Find("#alta-tramo").Change(nameof(TramoTrabajadoresActivos.De41A80));
        cut.Find("#alta-modalidad").Change(nameof(ModalidadSuscripcionAlta.Anual));
        Pulsar(cut, "Continuar");
        Pulsar(cut, "Continuar");

        var casilla = cut.Find("[data-campo='declara-solo-inbound'] input[type='checkbox']");
        casilla.HasAttribute("checked").Should().BeFalse("la declaración nunca viene marcada");
        cut.Find("[data-campo='declara-solo-inbound']").TextContent.Should().Contain("Declaro que este Tenant no usa Outbound (solo Inbound)")
            .And.Contain("Opcional").And.Contain("Sin marcar, conserva Outbound");
        Pulsar(cut, "Dar de alta");

        _mediator.Enviados.OfType<ContratarLineaInboundCommand>().Should().ContainSingle()
            .Which.Should().Be(new ContratarLineaInboundCommand("cus_Delta123", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual, DeclaraSoloInbound: false));
        cut.FindAll(".alta-tce-cuerpo [role='alert']").Should().BeEmpty();
        cut.FindAll(".alta-tce-ok").Should().ContainSingle();
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
        DeclararSoloInbound(cut);
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
        ElegirDeclaracionCorreccion(cut, "conserva");
        cut.FindAll(".alta-tce-linea button").First(b => b.TextContent.Trim() == "Guardar").Click();

        _mediator.Enviados.OfType<CorregirLineaInboundCommand>().Should().ContainSingle()
            .Which.Should().Be(new CorregirLineaInboundCommand("cus_Nuevo9", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual, DeclaraSoloInbound: false));
        cut.Find(".alta-tce-linea [role='status']").TextContent.Should().Contain("Línea Inbound corregida");
    }

    private static void ElegirDeclaracionCorreccion(IRenderedComponent<AltaTenantTce> cut, string valor) =>
        cut.Find($"[data-campo='declara-solo-inbound-corr'] input[type='radio'][value='{valor}']").Change(valor);

    private static void GuardarCorreccion(IRenderedComponent<AltaTenantTce> cut) =>
        cut.FindAll(".alta-tce-linea button").First(b => b.TextContent.Trim() == "Guardar").Click();

    private static void RellenarLineaCorreccion(IRenderedComponent<AltaTenantTce> cut)
    {
        cut.Find("#corr-pagador").Input("cus_Nuevo9");
        cut.Find("#corr-tramo").Change(nameof(TramoTrabajadoresActivos.De41A80));
        cut.Find("#corr-modalidad").Change(nameof(ModalidadSuscripcionAlta.Anual));
    }

    /// <summary>
    /// Opción A' (DECISION_LOG e496f33): la corrección contrata una fila nueva y no copia la declaración «solo Inbound» de la
    /// sustituida, y la pantalla no lee la línea vigente. Por eso la declaración es una elección obligatoria y sin valor por
    /// defecto: Guardar no envía nada mientras no se elija, y lo elegido viaja tal cual en el comando.
    /// </summary>
    [Fact]
    public void La_correccion_exige_elegir_la_declaracion_solo_Inbound()
    {
        _mediator.RespuestaCorregir = Result.Exito(ProductoId);
        var cut = RenderConSesion();

        cut.FindAll(".alta-tce-linea button").First(b => b.TextContent.Trim() == "Editar").Click();
        cut.FindAll("[data-campo='declara-solo-inbound-corr'] input[type='radio']").Should().HaveCount(2);
        cut.FindAll("[data-campo='declara-solo-inbound-corr'] input[type='radio']")
            .Should().OnlyContain(r => !r.HasAttribute("checked"), "ninguna opción viene elegida");
        RellenarLineaCorreccion(cut);

        GuardarCorreccion(cut);

        _mediator.Enviados.OfType<CorregirLineaInboundCommand>().Should().BeEmpty("sin elegir la declaración no se envía el comando");
        cut.Find(".alta-tce-linea [role='alert']").TextContent.Should().Contain("solo Inbound");

        ElegirDeclaracionCorreccion(cut, "solo");
        GuardarCorreccion(cut);

        _mediator.Enviados.OfType<CorregirLineaInboundCommand>().Should().ContainSingle()
            .Which.DeclaraSoloInbound.Should().BeTrue();

        cut.FindAll(".alta-tce-linea button").First(b => b.TextContent.Trim() == "Editar").Click();
        cut.FindAll("[data-campo='declara-solo-inbound-corr'] input[type='radio']")
            .Should().OnlyContain(r => !r.HasAttribute("checked"), "cada edición empieza sin elegir");
        RellenarLineaCorreccion(cut);
        ElegirDeclaracionCorreccion(cut, "conserva");
        GuardarCorreccion(cut);

        _mediator.Enviados.OfType<CorregirLineaInboundCommand>().Should().HaveCount(2)
            .And.Subject.Last().DeclaraSoloInbound.Should().BeFalse();
    }

    private static void PulsarEnLinea(IRenderedComponent<AltaTenantTce> cut, string texto) =>
        cut.FindAll(".alta-tce-linea button").First(b => b.TextContent.Trim() == texto).Click();

    private static void DebeEmpezarSinElegir(IRenderedComponent<AltaTenantTce> cut, string porque) =>
        cut.FindAll("[data-campo='declara-solo-inbound-corr'] input[type='radio']")
            .Should().HaveCount(2).And.OnlyContain(r => !r.HasAttribute("checked"), porque);

    /// <summary>
    /// «Cada edición empieza sin elegir» no puede depender de que la edición anterior acabara bien: lo elegido y abandonado
    /// con Cancelar no se hereda en la edición siguiente (un «solo Inbound» olvidado se enviaría sin que nadie lo decida).
    /// </summary>
    [Fact]
    public void Cancelar_la_edicion_descarta_la_declaracion_elegida()
    {
        var cut = RenderConSesion();

        PulsarEnLinea(cut, "Editar");
        ElegirDeclaracionCorreccion(cut, "solo");
        cut.Find("[data-campo='declara-solo-inbound-corr'] input[value='solo']").HasAttribute("checked")
            .Should().BeTrue("la elección se refleja mientras se edita");
        PulsarEnLinea(cut, "Cancelar");
        PulsarEnLinea(cut, "Editar");

        DebeEmpezarSinElegir(cut, "tras Cancelar, la edición siguiente empieza sin elegir");
    }

    [Fact]
    public void Un_fallo_del_comando_no_deja_la_declaracion_elegida_para_la_edicion_siguiente()
    {
        _mediator.RespuestaCorregir = Result.Fallo<Guid>(ErroresCorregirLineaInbound.SinLineaVigente);
        var cut = RenderConSesion();

        PulsarEnLinea(cut, "Editar");
        RellenarLineaCorreccion(cut);
        ElegirDeclaracionCorreccion(cut, "solo");
        GuardarCorreccion(cut);

        _mediator.Enviados.OfType<CorregirLineaInboundCommand>().Should().ContainSingle();
        cut.Find(".alta-tce-linea [role='alert']").Should().NotBeNull("el comando falló y la edición sigue abierta");
        PulsarEnLinea(cut, "Cancelar");
        PulsarEnLinea(cut, "Editar");

        DebeEmpezarSinElegir(cut, "tras un fallo del comando y Cancelar, la edición siguiente empieza sin elegir");
    }

    /// <summary>
    /// El error de «sin elegir» se asocia al grupo de radios (aria-invalid y aria-describedby, que apuntan al párrafo del
    /// error) y solo a él: un error de otro campo de la línea no marca el grupo como inválido ni lo describe. El grupo se
    /// anuncia como radiogroup obligatorio.
    /// </summary>
    [Fact]
    public void El_error_de_declaracion_marca_el_grupo_de_radios_como_invalido_y_lo_describe()
    {
        var cut = RenderConSesion();
        PulsarEnLinea(cut, "Editar");
        var grupo = cut.Find("[data-campo='declara-solo-inbound-corr']");
        grupo.HasAttribute("aria-invalid").Should().BeFalse("antes de guardar no hay error");
        grupo.HasAttribute("aria-describedby").Should().BeFalse();

        PulsarEnLinea(cut, "Guardar");
        cut.Find(".alta-tce-linea [role='alert']").TextContent.Should().Contain("Indica el Customer de Stripe");
        grupo = cut.Find("[data-campo='declara-solo-inbound-corr']");
        grupo.HasAttribute("aria-invalid").Should().BeFalse("el error es de otros campos, no de la declaración");
        grupo.HasAttribute("aria-describedby").Should().BeFalse("el error de otro campo no describe el grupo de radios");

        RellenarLineaCorreccion(cut);
        GuardarCorreccion(cut);

        var error = cut.Find(".alta-tce-linea [role='alert']");
        error.TextContent.Should().Contain("solo Inbound");
        error.Id.Should().NotBeNullOrEmpty();
        grupo = cut.Find("[data-campo='declara-solo-inbound-corr']");
        grupo.GetAttribute("aria-invalid").Should().Be("true");
        grupo.GetAttribute("aria-required").Should().Be("true");
        grupo.GetAttribute("role").Should().Be("radiogroup");
        grupo.GetAttribute("aria-describedby").Should().Be(error.Id);
        grupo.QuerySelector("legend .campo-obligatorio")!.TextContent.Should().Be("*");
        grupo.QuerySelector("legend")!.TextContent.Should().NotContain("obligatoria");

        ElegirDeclaracionCorreccion(cut, "solo");
        grupo = cut.Find("[data-campo='declara-solo-inbound-corr']");
        grupo.HasAttribute("aria-invalid").Should().BeFalse("al elegir se retira el error y el grupo deja de marcarse inválido");
        grupo.HasAttribute("aria-describedby").Should().BeFalse("al elegir el grupo deja de apuntar al error");
    }

    /// <summary>
    /// El nombre accesible de cada radio de la declaración es solo su título: la ayuda va fuera del &lt;label&gt; y se
    /// enlaza como descripción con aria-describedby. Los títulos y las ayudas se fijan en castellano («Conserva Outbound» y
    /// «conserva Outbound», no «mantiene»).
    /// </summary>
    [Theory]
    [InlineData("solo", "Solo Inbound: la organización no usa Outbound", "Con esta declaración y sin línea Outbound, se le cierra Outbound.")]
    [InlineData("conserva", "Conserva Outbound", "No se declara «solo Inbound»: la organización conserva Outbound.")]
    public void El_nombre_accesible_del_radio_de_la_declaracion_es_solo_su_titulo_y_la_ayuda_lo_describe(
        string valor, string titulo, string ayuda)
    {
        var cut = RenderConSesion();
        PulsarEnLinea(cut, "Editar");

        DebeNombrarseSoloPorSuTitulo(cut,
            $"[data-campo='declara-solo-inbound-corr'] input[type='radio'][value='{valor}']", titulo, ayuda);
    }

    /// <summary>
    /// El mismo patrón en los dos radios (deshabilitados) de la operación del paso 2 y en la casilla de la declaración del
    /// alta: el nombre accesible es solo el título y el texto de ayuda lo describe.
    /// </summary>
    [Fact]
    public void Los_radios_de_la_operacion_y_la_casilla_de_la_declaracion_se_nombran_solo_por_su_titulo()
    {
        var cut = RenderConSesion();
        Pulsar(cut, "Continuar");

        var radios = cut.FindAll("input[name='alta-tce-operacion']");
        radios.Should().HaveCount(2);
        DebeNombrarseSoloPorSuTitulo(cut, $"#{radios[0].Id}", "Lo opera un Operador CAE externo Inbound",
            "Una organización externa gestiona el Tenant TCE con un encargo de administración pleno.");
        DebeNombrarseSoloPorSuTitulo(cut, $"#{radios[1].Id}", "Sin Operador CAE externo: lo opera el propio Tenant",
            "El Administrador del Tenant TCE hace la operación; no hay encargo.");

        Pulsar(cut, "Continuar");
        cut.Find("#alta-pagador").Input("cus_Delta123");
        cut.Find("#alta-tramo").Change(nameof(TramoTrabajadoresActivos.De41A80));
        cut.Find("#alta-modalidad").Change(nameof(ModalidadSuscripcionAlta.Anual));
        Pulsar(cut, "Continuar");
        Pulsar(cut, "Continuar");

        DebeNombrarseSoloPorSuTitulo(cut, "[data-campo='declara-solo-inbound'] input[type='checkbox']",
            "Declaro que este Tenant no usa Outbound (solo Inbound).",
            "Opcional. Márcala solo si la organización no usa Outbound: con la declaración y sin línea Outbound, se le cierra Outbound. Sin marcar, conserva Outbound.");
    }

    /// <summary>
    /// El control no va dentro de un &lt;label&gt;; un único label[for] con el título le da nombre, y aria-describedby
    /// apunta a la ayuda, que queda fuera de todo &lt;label&gt;.
    /// </summary>
    private static void DebeNombrarseSoloPorSuTitulo(
        IRenderedComponent<AltaTenantTce> cut, string selector, string titulo, string ayuda)
    {
        var control = cut.Find(selector);
        control.Id.Should().NotBeNullOrEmpty();
        control.Closest("label").Should().BeNull("el control no va dentro de un <label> que arrastre la ayuda a su nombre");

        var etiquetas = cut.FindAll($"label[for='{control.Id}']");
        etiquetas.Should().ContainSingle();
        etiquetas[0].TextContent.Trim().Should().Be(titulo, "el nombre accesible del control es solo su título");

        var idAyuda = control.GetAttribute("aria-describedby");
        idAyuda.Should().NotBeNullOrEmpty();
        var descripcion = cut.Find($"#{idAyuda}");
        descripcion.TextContent.Trim().Should().Be(ayuda);
        descripcion.Closest("label").Should().BeNull("la ayuda no forma parte del nombre accesible");
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

    [Fact]
    public void Con_el_alta_en_vuelo_la_linea_no_se_edita_ni_se_da_de_baja()
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

        DeclararSoloInbound(cut);
        Pulsar(cut, "Dar de alta");

        cut.FindAll(".alta-tce-linea button").Should().HaveCount(2)
            .And.OnlyContain(b => b.HasAttribute("disabled"), "un solo envío a la vez en toda la pantalla");
        respuesta.SetResult(Result.Exito(ProductoId));
        cut.WaitForAssertion(() => cut.FindAll(".alta-tce-linea button").Should().OnlyContain(b => !b.HasAttribute("disabled")));
    }

    [Fact]
    public void Tras_dar_de_baja_la_confirmacion_del_alta_deja_de_pintarse()
    {
        var cut = RenderConSesion();
        IrAlPasoProducto(cut);
        cut.Find("#alta-pagador").Input("cus_Delta123");
        cut.Find("#alta-tramo").Change(nameof(TramoTrabajadoresActivos.De11A40));
        cut.Find("#alta-modalidad").Change(nameof(ModalidadSuscripcionAlta.Mensual));
        Pulsar(cut, "Continuar");
        Pulsar(cut, "Continuar");
        DeclararSoloInbound(cut);
        Pulsar(cut, "Dar de alta");
        cut.FindAll(".alta-tce-ok").Should().ContainSingle();

        cut.FindAll(".alta-tce-linea button").First(b => b.TextContent.Trim() == "Dar de baja").Click();
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Dar de baja").Click();

        cut.FindAll(".alta-tce-ok").Should().BeEmpty("la línea ya no está vigente");
    }

    /// <summary>
    /// Opción A' (DECISION_LOG e496f33): la declaración «solo Inbound» vale para un alta concreta. Ni al cancelar el alta ni tras dar
    /// de baja la línea contratada puede quedar marcada para la siguiente: el Actor de Plataforma TALVEG la vuelve a hacer.
    /// </summary>
    [Theory]
    [InlineData("cancelar")]
    [InlineData("dar de baja")]
    public void La_declaracion_solo_Inbound_no_sobrevive_al_alta_en_la_que_se_hizo(string camino)
    {
        var cut = RenderConSesion();
        LlenarYIrAlResumen(cut);
        DeclararSoloInbound(cut);

        if (camino == "cancelar")
        {
            Pulsar(cut, "Cancelar");
        }
        else
        {
            Pulsar(cut, "Dar de alta");
            cut.FindAll(".alta-tce-ok").Should().ContainSingle();
            cut.FindAll(".alta-tce-linea button").First(b => b.TextContent.Trim() == "Dar de baja").Click();
            cut.FindAll("button").Last(b => b.TextContent.Trim() == "Dar de baja").Click();
            cut.FindAll(".alta-tce-ok").Should().BeEmpty();
        }

        LlenarYIrAlResumen(cut);
        cut.Find("[data-campo='declara-solo-inbound'] input[type='checkbox']").HasAttribute("checked")
            .Should().BeFalse($"tras {camino} la declaración se pide otra vez");
    }

    private static void LlenarYIrAlResumen(IRenderedComponent<AltaTenantTce> cut)
    {
        IrAlPasoProducto(cut);
        cut.Find("#alta-pagador").Input("cus_Delta123");
        cut.Find("#alta-tramo").Change(nameof(TramoTrabajadoresActivos.De11A40));
        cut.Find("#alta-modalidad").Change(nameof(ModalidadSuscripcionAlta.Mensual));
        Pulsar(cut, "Continuar");
        Pulsar(cut, "Continuar");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Dar de alta", "está en el Resumen");
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
