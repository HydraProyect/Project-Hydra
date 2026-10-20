using Bunit;
using CaeManager.Application.Clientes.Commands.GuardarAccesoPlataformaCae;
using CaeManager.Application.Clientes.Commands.GuardarPlataformaCaeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerAccesosPlataformaCaeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerPlataformaCaeCliente;
using CaeManager.Application.Clientes.Queries.RevelarContrasenaAccesoPlataformaCae;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Clientes.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Caja «Plataforma CAE» de Cliente 360 (maqueta del 17-10, decisión del 2026-10-18). Por rol: el Gestor CAE ve la
/// plataforma, el código de cliente y los accesos de cada Empresa; Consulta solo la plataforma y «Abrir portal» (el
/// servidor no le entrega los accesos); al Usuario de Cliente no se le pinta. La contraseña nunca llega al abrir la
/// ficha: «Mostrar» y «Copiar» la piden con un clic, y la mostrada se vuelve a ocultar sola a los 30 s.
/// </summary>
public class CajaPlataformaCaeClienteTests : BunitContext
{
    private static readonly Guid ClienteEmpresarialId = Guid.NewGuid();
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private readonly RelojManual _reloj = new();

    public CajaPlataformaCaeClienteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class MediatorFalso : IMediator
    {
        public PlataformaCaeClienteDto? Plataforma { get; set; } =
            new(true, "Nalanda", "https://portal.example/acceso", "CLI-01");

        /// <summary>null = lo que el servidor devuelve a Consulta (AutorizacionSecretosDeTenantBehavior).</summary>
        public IReadOnlyList<AccesoPlataformaCaeDto>? Accesos { get; set; } =
        [
            new(EmpresaId, "Empresa Propia S.L.", "b12345678", true, new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), "Marta Gestora"),
        ];

        public bool AccesosPidenSegundoFactor { get; set; }
        public bool AccesosFallan { get; set; }

        /// <summary>Otra persona ganó la carrera del índice único: el guardado devuelve el conflicto del Command.</summary>
        public bool GuardadoEnConflicto { get; set; }

        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object? respuesta = request switch
            {
                GuardarPlataformaCaeClienteCommand => GuardadoEnConflicto
                    ? Result.Fallo(Error.Crear(GuardarPlataformaCaeClienteCommand.CodigoConflicto, "Otra persona acaba de registrar la plataforma."))
                    : Result.Exito(),
                GuardarAccesoPlataformaCaeCommand => GuardadoEnConflicto
                    ? Result.Fallo(Error.Crear(GuardarAccesoPlataformaCaeCommand.CodigoConflicto, "Otra persona acaba de dar de alta este acceso."))
                    : Result.Exito(),
                ObtenerPlataformaCaeClienteQuery => Plataforma,
                ObtenerAccesosPlataformaCaeClienteQuery when AccesosPidenSegundoFactor =>
                    throw new SegundoFactorRequeridoParaCredencialesException(),
                ObtenerAccesosPlataformaCaeClienteQuery when AccesosFallan =>
                    throw new InvalidOperationException("Fallo simulado de los accesos."),
                ObtenerAccesosPlataformaCaeClienteQuery => Accesos,
                RevelarContrasenaAccesoPlataformaCaeQuery => new ContrasenaAccesoPlataformaCaeDto("s3cr3ta-del-portal"),
                _ => throw new NotSupportedException($"Consulta no prevista: {request.GetType().Name}."),
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private int _avisosDeCarga;

    private IRenderedComponent<CajaPlataformaCaeCliente> Montar(
        MediatorFalso mediador, string rol = Roles.GestorCae, bool interactivo = true, bool cargar = true)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<TimeProvider>(_reloj);
        Services.AddLocalization();
        this.ConRolDeEscritura(rol);
        SetRendererInfo(new RendererInfo(interactivo ? "Server" : "Static", isInteractive: interactivo));
        return Render<CajaPlataformaCaeCliente>(p => p
            .Add(c => c.ClienteEmpresarialId, ClienteEmpresarialId)
            .Add(c => c.Cargar, cargar)
            .Add(c => c.AlCargar, () => _avisosDeCarga++));
    }

    private static void Guardar(IRenderedComponent<CajaPlataformaCaeCliente> cut) =>
        cut.FindAll("button").Last(b => b.TextContent.Contains("Guardar", StringComparison.Ordinal)).Click();

    [Fact]
    public void Mientras_la_pagina_no_habilita_la_carga_no_consulta_nada()
    {
        var mediador = new MediatorFalso();

        var cut = Montar(mediador, cargar: false);

        mediador.Enviadas.Should().BeEmpty(
            "la página aún lee su cadena sobre el mismo DbContext del circuito: dos consultas a la vez fallan");
        cut.Markup.Should().NotContain("Nalanda");
        _avisosDeCarga.Should().Be(0);

        cut.Render(p => p.Add(c => c.Cargar, true));

        cut.WaitForAssertion(() => cut.Find("[data-plataforma-nombre]").TextContent.Should().Be("Nalanda"));
        mediador.Enviadas.Select(e => e.GetType()).Should().Equal(
            typeof(ObtenerPlataformaCaeClienteQuery), typeof(ObtenerAccesosPlataformaCaeClienteQuery));
        _avisosDeCarga.Should().Be(1, "la caja avisa una sola vez, al terminar su lectura");
    }

    [Fact]
    public void Volver_a_habilitar_la_carga_del_mismo_Cliente_avisa_sin_volver_a_consultar()
    {
        var mediador = new MediatorFalso();
        var cut = Montar(mediador);
        var consultas = mediador.Enviadas.Count;

        cut.Render(p => p.Add(c => c.Cargar, false));
        cut.Render(p => p.Add(c => c.Cargar, true));

        mediador.Enviadas.Should().HaveCount(consultas);
        _avisosDeCarga.Should().Be(2, "la página espera el aviso aunque no haya nada nuevo que leer");
    }

    [Fact]
    public void Tras_un_conflicto_al_guardar_la_plataforma_se_vuelve_a_leer_la_caja()
    {
        var mediador = new MediatorFalso { GuardadoEnConflicto = true };
        var cut = Montar(mediador);
        var lecturas = mediador.Enviadas.OfType<ObtenerPlataformaCaeClienteQuery>().Count();
        mediador.Plataforma = new PlataformaCaeClienteDto(true, "Dokify", "https://dokify.example", "CLI-02");

        cut.Find("[data-editar-plataforma]").Click();
        cut.Find("input").Input("Nalanda Pro");
        Guardar(cut);

        cut.WaitForAssertion(() =>
        {
            mediador.Enviadas.OfType<ObtenerPlataformaCaeClienteQuery>().Should().HaveCount(lecturas + 1);
            cut.Find("[data-plataforma-nombre]").TextContent.Should().Be("Dokify", "al reabrir, el modal parte de lo que guardó la otra persona");
            cut.Markup.Should().Contain("Otra persona acaba de registrar la plataforma.");
        });
    }

    [Fact]
    public void Tras_un_conflicto_al_guardar_un_acceso_se_vuelven_a_leer_los_accesos()
    {
        var mediador = new MediatorFalso { GuardadoEnConflicto = true };
        var cut = Montar(mediador);
        var lecturas = mediador.Enviadas.OfType<ObtenerAccesosPlataformaCaeClienteQuery>().Count();
        mediador.Accesos =
        [
            new(EmpresaId, "Empresa Propia S.L.", "otro-usuario", true, new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc), "Otra Gestora"),
        ];

        cut.Find($"[data-acceso-empresa='{EmpresaId}'] [data-editar-acceso]").Click();
        cut.Find("input").Input("nuevo-usuario");
        Guardar(cut);

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerAccesosPlataformaCaeClienteQuery>().Should().HaveCount(lecturas + 1));
        cut.Find($"[data-acceso-empresa='{EmpresaId}']").TextContent.Should().Contain("otro-usuario");
    }

    [Fact]
    public void En_el_prerender_estatico_no_carga_nada_ni_deja_el_usuario_en_el_HTML()
    {
        var mediador = new MediatorFalso();

        var cut = Montar(mediador, interactivo: false);

        mediador.Enviadas.Should().BeEmpty("cargar en el prerender auditaría dos veces la lectura de los usuarios");
        cut.Markup.Should().NotContain("b12345678");
    }

    [Fact]
    public void Si_fallan_los_accesos_se_conservan_la_plataforma_y_abrir_portal()
    {
        var mediador = new MediatorFalso { AccesosFallan = true };

        var cut = Montar(mediador);

        cut.Find("[data-plataforma-nombre]").TextContent.Should().Be("Nalanda");
        cut.FindAll("[data-abrir-portal]").Should().ContainSingle();
        cut.FindAll("[data-accesos-error]").Should().ContainSingle();
        cut.FindAll("[data-acceso-empresa]").Should().BeEmpty();
    }

    [Fact]
    public void El_Gestor_CAE_ve_plataforma_portal_codigo_y_accesos_sin_que_viaje_la_contrasena()
    {
        var mediador = new MediatorFalso();

        var cut = Montar(mediador);

        cut.Find("[data-plataforma-nombre]").TextContent.Should().Be("Nalanda");
        var portal = cut.Find("[data-abrir-portal]");
        portal.GetAttribute("href").Should().Be("https://portal.example/acceso");
        portal.GetAttribute("rel").Should().Contain("noopener");
        cut.Find("[data-codigo-cliente]").TextContent.Should().Contain("CLI-01");
        var acceso = cut.Find($"[data-acceso-empresa='{EmpresaId}']");
        acceso.TextContent.Should().Contain("Empresa Propia S.L.").And.Contain("b12345678").And.Contain("Marta Gestora");
        cut.Find("[data-contrasena-valor]").TextContent.Should().Be("••••••••••");
        cut.Markup.Should().NotContain("s3cr3ta-del-portal");
        mediador.Enviadas.OfType<RevelarContrasenaAccesoPlataformaCaeQuery>().Should().BeEmpty(
            "abrir la ficha no descifra ninguna contraseña ni deja rastro en la auditoría");
    }

    [Fact]
    public void Consulta_ve_solo_la_plataforma_y_abrir_portal()
    {
        var mediador = new MediatorFalso { Accesos = null };

        var cut = Montar(mediador, Roles.Consulta);

        cut.Find("[data-plataforma-nombre]").TextContent.Should().Be("Nalanda");
        cut.FindAll("[data-abrir-portal]").Should().ContainSingle();
        cut.FindAll("[data-codigo-cliente]").Should().BeEmpty();
        cut.FindAll("[data-acceso-empresa]").Should().BeEmpty();
        cut.FindAll("[data-editar-plataforma]").Should().BeEmpty();
        cut.Markup.Should().NotContain("CLI-01");
    }

    [Fact]
    public void Al_Usuario_de_Cliente_no_se_le_pinta_la_caja()
    {
        var mediador = new MediatorFalso { Plataforma = null, Accesos = null };

        var cut = Montar(mediador, Roles.Cliente);

        cut.FindAll(".tarjeta-titulo").Should().BeEmpty();
        cut.Markup.Should().NotContain("Plataforma CAE");
        mediador.Enviadas.OfType<ObtenerAccesosPlataformaCaeClienteQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Sin_plataforma_registrada_el_Gestor_CAE_ve_el_vacio_y_registrar()
    {
        var mediador = new MediatorFalso { Plataforma = new PlataformaCaeClienteDto(false, null, null, null) };

        var cut = Montar(mediador);

        cut.FindAll("[data-plataforma-sin-registrar]").Should().ContainSingle();
        cut.Find("[data-editar-plataforma]").TextContent.Should().Contain("Registrar");
        mediador.Enviadas.OfType<ObtenerAccesosPlataformaCaeClienteQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Sin_segundo_factor_los_accesos_invitan_a_activarlo()
    {
        var mediador = new MediatorFalso { AccesosPidenSegundoFactor = true };

        var cut = Montar(mediador);

        cut.FindAll("[data-accesos-requieren-2fa]").Should().ContainSingle();
        cut.FindAll("[data-acceso-empresa]").Should().BeEmpty();
        cut.Find("[data-plataforma-nombre]").TextContent.Should().Be("Nalanda");
    }

    [Fact]
    public void Mostrar_pide_la_contrasena_una_vez_y_se_oculta_sola_a_los_30_segundos()
    {
        var mediador = new MediatorFalso();
        var cut = Montar(mediador);

        cut.Find("[data-mostrar-contrasena]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-contrasena-valor]").TextContent.Should().Be("s3cr3ta-del-portal"));
        mediador.Enviadas.OfType<RevelarContrasenaAccesoPlataformaCaeQuery>().Should().ContainSingle()
            .Which.Should().Be(new RevelarContrasenaAccesoPlataformaCaeQuery(ClienteEmpresarialId, EmpresaId));

        _reloj.Avanzar(CajaPlataformaCaeCliente.TiempoContrasenaVisible - TimeSpan.FromSeconds(1));
        cut.Find("[data-contrasena-valor]").TextContent.Should().Be("s3cr3ta-del-portal");

        _reloj.Avanzar(TimeSpan.FromSeconds(1));
        cut.WaitForAssertion(() => cut.Find("[data-contrasena-valor]").TextContent.Should().Be("••••••••••"));
        cut.Markup.Should().NotContain("s3cr3ta-del-portal");
    }

    [Fact]
    public void Ocultar_antes_de_tiempo_no_vuelve_a_pedir_la_contrasena()
    {
        var mediador = new MediatorFalso();
        var cut = Montar(mediador);

        cut.Find("[data-mostrar-contrasena]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-contrasena-valor]").TextContent.Should().Be("s3cr3ta-del-portal"));
        cut.Find("[data-mostrar-contrasena]").Click();

        cut.Find("[data-contrasena-valor]").TextContent.Should().Be("••••••••••");
        mediador.Enviadas.OfType<RevelarContrasenaAccesoPlataformaCaeQuery>().Should().ContainSingle();
        _reloj.TemporizadoresVivos.Should().Be(0, "al ocultar se suelta el temporizador");
    }

    [Fact]
    public void Copiar_la_contrasena_usa_la_misma_peticion_auditada()
    {
        var mediador = new MediatorFalso();
        var cut = Montar(mediador);

        cut.Find("[data-contrasena] .boton-copiar").Click();

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<RevelarContrasenaAccesoPlataformaCaeQuery>().Should().ContainSingle());
        cut.Find("[data-contrasena-valor]").TextContent.Should().Be("••••••••••", "copiar no la deja a la vista");
    }
}
