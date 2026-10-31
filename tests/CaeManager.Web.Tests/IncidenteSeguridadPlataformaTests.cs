using Bunit;
using CaeManager.Application.ContactosSeguridad;
using CaeManager.Application.Seguridad.Incidentes.Aviso;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.ConfirmarYEnviarPrimerAviso;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.DecidirIncidenteSeguridad;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.EnviarNotificacionCompleta;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Queries.ObtenerIncidenteSeguridad;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.IncidentesSeguridad.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Página del incidente de seguridad (aviso de violaciones, incremento 3b): la cuenta atrás de 24 y 36 horas
/// desde la confirmación, los Tenants beneficiarios afectados marcados cuando no tienen contacto de seguridad
/// validado, y el primario único del paso vigente (confirmar y enviar el primer aviso en un clic; después, la
/// notificación completa). Quién puede decidir lo imponen Application y la base (AvisoIncidenteSeguridadTests y
/// AvisoIncidenteSeguridadBajoRuntimeTests); aquí se prueba lo que la pantalla ofrece y envía.
/// </summary>
public class IncidenteSeguridadPlataformaTests : BunitContext
{
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid TenantC = Guid.NewGuid();

    private readonly MediadorFalso _mediador = new();

    public IncidenteSeguridadPlataformaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
    }

    // ── Plazos ────────────────────────────────────────────────────────────────

    [Fact]
    public void Sin_confirmar_no_hay_cuenta_atras()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Sospechado, confirmadoEnUtc: null);

        var cut = Pagina();

        cut.WaitForAssertion(() => cut.Find("[data-testid=estado-incidente]").TextContent.Should().NotBeNullOrWhiteSpace());
        cut.FindAll("[data-testid=plazos]").Should().BeEmpty();
    }

    [Fact]
    public void Confirmado_hace_dos_horas_quedan_casi_22_horas_para_el_primer_aviso_y_casi_34_para_la_completa()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Confirmado, DateTime.UtcNow.AddHours(-2).AddMinutes(-1));

        var cut = Pagina();

        cut.WaitForAssertion(() => cut.FindAll(".incidente-plazo strong").Should().HaveCount(2));
        var plazos = cut.FindAll(".incidente-plazo strong").Select(p => p.TextContent.Trim()).ToList();
        plazos[0].Should().MatchRegex(@"^Quedan 21 h 5\d min$");
        plazos[1].Should().MatchRegex(@"^Quedan 33 h 5\d min$");
    }

    [Fact]
    public void Pasadas_24_horas_sin_enviar_el_primer_aviso_esta_vencido_y_la_completa_sigue_contando()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Confirmado, DateTime.UtcNow.AddHours(-25));

        var cut = Pagina();

        cut.WaitForAssertion(() => cut.FindAll(".incidente-plazo strong").Should().HaveCount(2));
        var plazos = cut.FindAll(".incidente-plazo strong").Select(p => p.TextContent.Trim()).ToList();
        plazos[0].Should().Be("Vencido");
        plazos[1].Should().MatchRegex(@"^Quedan 10 h \d+ min$");
    }

    [Fact]
    public void Enviado_el_primer_aviso_su_plazo_queda_cumplido()
    {
        var confirmado = DateTime.UtcNow.AddHours(-30);
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Confirmado, confirmado,
            enviosPrimerAviso: EstadoEnvioAvisoIncidenteSeguridad.Enviado);

        var cut = Pagina();

        cut.WaitForAssertion(() => cut.FindAll(".incidente-plazo strong").Should().HaveCount(2));
        var plazos = cut.FindAll(".incidente-plazo strong").Select(p => p.TextContent.Trim()).ToList();
        plazos[0].Should().Be("Enviado");
        plazos[1].Should().MatchRegex(@"^Quedan 5 h \d+ min$");
    }

    [Fact]
    public void Con_el_primer_aviso_enviado_solo_a_parte_de_los_destinatarios_su_plazo_sigue_contando()
    {
        var vista = Vista(EstadoIncidenteSeguridad.Confirmado, DateTime.UtcNow.AddHours(-2),
            enviosPrimerAviso: EstadoEnvioAvisoIncidenteSeguridad.Enviado);
        _mediador.Vista = vista with { Incidente = vista.Incidente with { Envios = [vista.Incidente.Envios[0]] } };

        var cut = Pagina();

        cut.WaitForAssertion(() => cut.FindAll(".incidente-plazo strong").Should().HaveCount(2));
        cut.FindAll(".incidente-plazo strong")[0].TextContent.Trim().Should().MatchRegex(@"^Quedan 2\d h \d+ min$",
            "un Tenant afectado avisado no da por cumplido el plazo de los demás");
    }

    [Fact]
    public void Un_envio_con_Tenants_sin_destinatario_no_se_presenta_como_exito_sin_mas()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Sospechado, confirmadoEnUtc: null);
        _mediador.Resumen = new ResumenEnvioAviso(2, 0, 0, 0, [TenantC]);
        var cut = Pagina();
        cut.WaitForAssertion(() => cut.Find("[data-testid=enviar-paso]"));

        cut.Find("[data-testid=enviar-paso]").Click();

        cut.WaitForAssertion(() => cut.Find(".incidente-error").TextContent.Should()
            .Be("Aviso enviado a 2 destinatarios. 1 organizaciones afectadas no tienen destinatario: no se les ha podido avisar."));
    }

    // ── Tenants afectados ─────────────────────────────────────────────────────

    [Fact]
    public void Los_Tenants_sin_contacto_validado_y_sin_destinatario_se_marcan()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Sospechado, confirmadoEnUtc: null);

        var cut = Pagina();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tenant-afectado]").Should().HaveCount(3));
        var tenants = cut.FindAll("[data-testid=tenant-afectado]");
        tenants[0].TextContent.Should().Contain("Org A").And.Contain("seguridad@a.test").And.NotContain("Sin contacto");
        tenants[1].TextContent.Should().Contain("Org B")
            .And.Contain("Sin contacto de seguridad validado: se avisa a sus Administradores")
            .And.Contain("admin@b.test");
        tenants[2].QuerySelector(".incidente-marca-grave")!.TextContent.Should().Be("Sin destinatario: no se le puede avisar");
    }

    // ── Paso vigente y un solo primario ───────────────────────────────────────

    [Fact]
    public void Confirmar_y_enviar_el_primer_aviso_es_un_solo_clic_con_el_borrador()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Sospechado, confirmadoEnUtc: null);
        var cut = Pagina();
        cut.WaitForAssertion(() => cut.Find("[data-testid=enviar-paso]").TextContent.Trim().Should().Be("Confirmar y enviar el primer aviso"));

        cut.FindAll("button.boton-primario").Should().ContainSingle("la superficie tiene un solo primario");
        cut.Find("[data-testid=enviar-paso]").Click();

        cut.WaitForAssertion(() => _mediador.PrimerosAvisos.Should().ContainSingle()
            .Which.Should().Be(new ConfirmarYEnviarPrimerAvisoCommand(_mediador.Vista!.Incidente.Id, BorradorPrimer)));
        _mediador.Decisiones.Should().BeEmpty("confirmar no es un paso aparte: lo hace el mismo clic que envía");
        _mediador.Consultas.Should().Be(2, "tras enviar, la página recarga el incidente");
    }

    [Fact]
    public void Con_el_primer_aviso_enviado_el_primario_pasa_a_la_notificacion_completa_y_espera_sus_apartados()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Confirmado, DateTime.UtcNow.AddHours(-3),
            enviosPrimerAviso: EstadoEnvioAvisoIncidenteSeguridad.Enviado);
        var cut = Pagina();
        cut.WaitForAssertion(() => cut.Find("[data-testid=paso-notificacion-completa]"));

        var boton = cut.Find("[data-testid=enviar-paso]");
        boton.TextContent.Trim().Should().Be("Enviar la notificación completa");
        boton.ClassList.Should().Contain("boton-primario");
        boton.HasAttribute("disabled").Should().BeTrue("faltan las consecuencias y los datos afectados");
        cut.FindAll("button.boton-primario").Should().ContainSingle();
        cut.FindAll("[data-testid=paso-primer-aviso]").Should().BeEmpty();
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Descartar (falsa alarma)");
        cut.Find("[data-testid=apartados-pendientes]");

        cut.FindAll("textarea")[1].Input("Unos 40 trabajadores: nombre y DNI.");
        cut.FindAll("textarea")[1].Blur();
        cut.FindAll("textarea")[2].Input("Posible suplantación de identidad.");
        cut.FindAll("textarea")[2].Blur();
        cut.WaitForAssertion(() => cut.Find("[data-testid=enviar-paso]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid=enviar-paso]").Click();

        cut.WaitForAssertion(() => _mediador.Completas.Should().ContainSingle());
        _mediador.Completas[0].Contenido.Consecuencias.Should().Be("Posible suplantación de identidad.");
        _mediador.PrimerosAvisos.Should().BeEmpty();
    }

    [Fact]
    public void Con_un_primer_aviso_fallido_el_primario_pasa_a_la_completa_y_el_reintento_queda_como_secundario()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Confirmado, DateTime.UtcNow.AddHours(-1),
            enviosPrimerAviso: EstadoEnvioAvisoIncidenteSeguridad.Fallido);
        var cut = Pagina();

        cut.WaitForAssertion(() => cut.Find("[data-testid=paso-notificacion-completa]"));
        cut.Find("[data-testid=enviar-paso]").TextContent.Trim().Should().Be("Enviar la notificación completa");
        cut.FindAll("button.boton-primario").Should().ContainSingle("el reintento no es un segundo primario");
        cut.Find("[data-testid=primer-aviso-pendiente]");
        var reintento = cut.Find("[data-testid=reintentar-primer-aviso]");
        reintento.TextContent.Trim().Should().Be("Reintentar el primer aviso");
        reintento.ClassList.Should().NotContain("boton-primario");

        reintento.Click();

        cut.WaitForAssertion(() => _mediador.PrimerosAvisos.Should().ContainSingle());
        _mediador.Completas.Should().BeEmpty();
    }

    [Fact]
    public void Confirmado_sin_ningun_intento_del_primer_aviso_el_primario_sigue_en_el_primer_aviso()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Confirmado, DateTime.UtcNow.AddHours(-1));
        var cut = Pagina();

        cut.WaitForAssertion(() => cut.Find("[data-testid=enviar-paso]").TextContent.Trim().Should().Be("Reintentar los envíos pendientes"));
        cut.FindAll("[data-testid=paso-notificacion-completa]").Should().BeEmpty();
        cut.FindAll("button.boton-primario").Should().ContainSingle();
    }

    [Fact]
    public void Recargar_tras_una_accion_conserva_el_borrador_escrito()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Sospechado, confirmadoEnUtc: null);
        var cut = Pagina();
        cut.WaitForAssertion(() => cut.FindAll("textarea").Should().NotBeEmpty());

        cut.FindAll("textarea")[0].Input("Naturaleza reescrita por el Actor de Plataforma.");
        cut.FindAll("textarea")[0].Blur();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Necesito más tiempo para evaluarlo").Click();

        // El texto del textarea lo lleva el navegador (CampoTextarea no lo refleja en cada pulsación), así que la
        // prueba es lo que sale en el comando tras la recarga: el borrador de la página, no el de la consulta.
        cut.WaitForAssertion(() => _mediador.Consultas.Should().Be(2, "evaluar recarga el incidente"));

        cut.Find("[data-testid=enviar-paso]").Click();
        cut.WaitForAssertion(() => _mediador.PrimerosAvisos.Should().ContainSingle());
        _mediador.PrimerosAvisos[0].Contenido.Naturaleza.Should().Be("Naturaleza reescrita por el Actor de Plataforma.");
    }

    [Fact]
    public void Sin_segundo_factor_en_la_sesion_se_explica_y_no_se_puede_enviar()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Sospechado, confirmadoEnUtc: null)
            with
        { MotivoParaNoDecidir = ErroresIncidenteSeguridad.TotpNoVerificado };
        var cut = Pagina();

        cut.WaitForAssertion(() => cut.Find("[data-testid=motivo-no-decidir]").TextContent.Should()
            .Be(ErroresIncidenteSeguridad.TotpNoVerificado.Mensaje));
        cut.Find("[data-testid=enviar-paso]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Un_envio_incompleto_se_explica_en_la_pagina()
    {
        _mediador.Vista = Vista(EstadoIncidenteSeguridad.Sospechado, confirmadoEnUtc: null);
        _mediador.Resumen = new ResumenEnvioAviso(1, 0, 1, 0, []);
        var cut = Pagina();
        cut.WaitForAssertion(() => cut.Find("[data-testid=enviar-paso]"));

        cut.Find("[data-testid=enviar-paso]").Click();

        cut.WaitForAssertion(() => cut.Find(".incidente-error").TextContent.Should()
            .Be("Envío incompleto: 1 enviados, 1 fallidos y 0 en curso. Puedes reintentarlo."));
    }

    // ── Datos ─────────────────────────────────────────────────────────────────

    private static readonly ContenidoAvisoIncidente BorradorPrimer =
        new("Acceso no autorizado sospechado.", "En evaluación.", "En evaluación.", "Sesiones cerradas.", "Responsable de seguridad de TALVEG.");

    private static readonly ContenidoAvisoIncidente BorradorCompleta =
        new("Acceso no autorizado sospechado.", "", "", "Sesiones cerradas.", "Responsable de seguridad de TALVEG.");

    private IRenderedComponent<IncidenteSeguridadPlataforma> Pagina() =>
        Render<IncidenteSeguridadPlataforma>(p => p.Add(x => x.IncidenteId, _mediador.Vista!.Incidente.Id));

    private static VistaIncidenteSeguridad Vista(
        EstadoIncidenteSeguridad estado, DateTime? confirmadoEnUtc, EstadoEnvioAvisoIncidenteSeguridad? enviosPrimerAviso = null)
    {
        var tenants = new List<TenantAfectadoAviso>
        {
            new(TenantA, "Org A", false, [new DestinatarioAvisoSeguridad("Marta Ruiz", "seguridad@a.test", null)]),
            new(TenantB, "Org B", true, [new DestinatarioAvisoSeguridad("Admin B", "admin@b.test", null)]),
            new(TenantC, "Org C", true, []),
        };
        var envios = enviosPrimerAviso is { } estadoEnvio
            ? tenants.SelectMany(t => t.Destinatarios.Select(d => new EnvioAvisoDto(Guid.NewGuid(),
                PlantillaAvisoIncidenteSeguridad.PrimerAviso, t.TenantId, d.Nombre, d.Correo, t.SinContactoValidado, estadoEnvio, 1,
                Actor, confirmadoEnUtc ?? DateTime.UtcNow, DateTime.UtcNow,
                estadoEnvio == EstadoEnvioAvisoIncidenteSeguridad.Enviado ? DateTime.UtcNow : null, null))).ToList()
            : [];
        var incidente = new IncidenteSeguridadDto(Guid.NewGuid(), SenalIncidenteSeguridad.InicioSesionNoReconocido, estado,
            DateTime.UtcNow.AddHours(-40), DateTime.UtcNow.AddHours(-39), 1, [TenantA, TenantB, TenantC], "{}", confirmadoEnUtc,
            [], envios);
        return new VistaIncidenteSeguridad(incidente, "INC-2026-ABCDEF", "La persona titular marcó un inicio de sesión.",
            tenants, BorradorPrimer, BorradorCompleta,
            confirmadoEnUtc + PlazosAvisoIncidenteSeguridad.PrimerAviso,
            confirmadoEnUtc + PlazosAvisoIncidenteSeguridad.NotificacionCompleta,
            Actor, null, false);
    }

    private sealed class MediadorFalso : IMediator
    {
        public VistaIncidenteSeguridad? Vista { get; set; }
        public ResumenEnvioAviso Resumen { get; set; } = new(2, 0, 0, 0, []);
        public List<ConfirmarYEnviarPrimerAvisoCommand> PrimerosAvisos { get; } = [];
        public List<EnviarNotificacionCompletaCommand> Completas { get; } = [];
        public List<DecidirIncidenteSeguridadCommand> Decisiones { get; } = [];
        public int Consultas { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerIncidenteSeguridadQuery:
                    Consultas++;
                    return Task.FromResult((TResponse)(object)Result.Exito(Vista!));
                case ConfirmarYEnviarPrimerAvisoCommand primer:
                    PrimerosAvisos.Add(primer);
                    return Task.FromResult((TResponse)(object)Result.Exito(Resumen));
                case EnviarNotificacionCompletaCommand completa:
                    Completas.Add(completa);
                    return Task.FromResult((TResponse)(object)Result.Exito(Resumen));
                case DecidirIncidenteSeguridadCommand decidir:
                    Decisiones.Add(decidir);
                    return Task.FromResult((TResponse)(object)Result.Exito());
                default:
                    throw new NotSupportedException($"El doble no cubre {request.GetType().Name}.");
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
