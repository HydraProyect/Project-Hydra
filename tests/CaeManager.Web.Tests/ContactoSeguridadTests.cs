using Bunit;
using CaeManager.Application.ContactosSeguridad;
using CaeManager.Application.ContactosSeguridad.Commands.ProponerContactoSeguridad;
using CaeManager.Application.ContactosSeguridad.Commands.RechazarContactoSeguridad;
using CaeManager.Application.ContactosSeguridad.Commands.ValidarContactoSeguridad;
using CaeManager.Application.ContactosSeguridad.Queries.ObtenerContactoSeguridadDeMiOrganizacion;
using CaeManager.Application.ContactosSeguridad.Queries.ObtenerContactosSeguridadPropuestos;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.ContactoSeguridad.Components;
using CaeManager.Web.Features.ContactoSeguridad.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contacto de seguridad (aviso de violaciones de seguridad, incremento 3a): la sección de la organización,
/// donde el Administrador del Tenant beneficiario propone, y la cola de Plataforma, donde un Actor de
/// Plataforma TALVEG valida o rechaza con motivo. Quién puede hacer cada cosa lo imponen Application y la
/// RLS (ContactoSeguridadTenantTests); aquí se prueba lo que la pantalla ofrece y envía.
/// </summary>
public class ContactoSeguridadTests : BunitContext
{
    private readonly MediadorFalso _mediador = new();

    public ContactoSeguridadTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
    }

    private static ContactoSeguridadDto Contacto(string nombre, string? motivo = null) =>
        new(Guid.NewGuid(), nombre, $"{nombre.ToLowerInvariant()}@ejemplo.test", null, DateTime.UtcNow, DateTime.UtcNow, motivo);

    // ── Sección de la organización ────────────────────────────────────────────

    [Fact]
    public void Quien_no_es_Administrador_del_Tenant_no_ve_la_seccion()
    {
        _mediador.MiOrganizacion = null;

        var cut = Render<SeccionContactoSeguridad>();

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Sin_contacto_validado_lo_dice_y_ofrece_proponer()
    {
        _mediador.MiOrganizacion = new ContactoSeguridadDeMiOrganizacionDto(null, null, null);

        var cut = Render<SeccionContactoSeguridad>();

        cut.Find(".contacto-seguridad-sin-validar").TextContent.Should()
            .Be("Tu organización todavía no tiene un contacto de seguridad validado.");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Proponer contacto");
        cut.Markup.Should().NotContainEquivalentOf("tenant");
    }

    [Fact]
    public void Muestra_el_vigente_la_propuesta_en_revision_y_el_rechazo_con_su_motivo()
    {
        _mediador.MiOrganizacion = new ContactoSeguridadDeMiOrganizacionDto(
            Contacto("Marta"), Contacto("Jordi"), Contacto("Ana", "No pertenece a la organización"));

        var cut = Render<SeccionContactoSeguridad>();

        cut.Find(".contacto-seguridad-vigente").TextContent.Should().Contain("Marta").And.Contain("Validado");
        cut.Find(".contacto-seguridad-pendiente").TextContent.Should().Contain("Jordi").And.Contain("En revisión");
        cut.Find(".contacto-seguridad-rechazo").TextContent.Should().Contain("Motivo: No pertenece a la organización");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Proponer otro contacto");
    }

    [Fact]
    public void Proponer_abre_el_DrawerFormulario_y_envia_los_datos_recortados()
    {
        _mediador.MiOrganizacion = new ContactoSeguridadDeMiOrganizacionDto(null, null, null);
        var cut = Render<SeccionContactoSeguridad>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Proponer contacto").Click();
        var drawer = cut.FindComponent<ProponerContactoSeguridadDrawer>();
        drawer.FindComponent<DrawerFormulario>().Instance.Visible.Should().BeTrue();

        var campos = drawer.FindAll("input");
        campos[0].Input("  Marta Ruiz ");
        campos[0].Blur();
        campos[1].Input(" marta@ejemplo.test ");
        campos[1].Blur();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Enviar a revisión").Click();

        cut.WaitForAssertion(() => _mediador.Propuestas.Should().ContainSingle()
            .Which.Should().Be(new ProponerContactoSeguridadCommand("Marta Ruiz", "marta@ejemplo.test", null)));
    }

    [Fact]
    public void Un_correo_no_valido_no_llega_al_servidor_y_se_marca_en_el_campo()
    {
        _mediador.MiOrganizacion = new ContactoSeguridadDeMiOrganizacionDto(null, null, null);
        var cut = Render<SeccionContactoSeguridad>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Proponer contacto").Click();
        var campos = cut.FindComponent<ProponerContactoSeguridadDrawer>().FindAll("input");
        campos[0].Input("Marta Ruiz");
        campos[0].Blur();
        campos[1].Input("no-es-un-correo");
        campos[1].Blur();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Enviar a revisión").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("El correo no tiene un formato válido."));
        _mediador.Propuestas.Should().BeEmpty();
    }

    // ── Cola de Plataforma ────────────────────────────────────────────────────

    private ContactoSeguridadPropuestoDto Propuesto() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Norte Servicios", "Marta Ruiz", "marta@ejemplo.test", "600000000", DateTime.UtcNow);

    [Fact]
    public void Sin_propuestas_la_cola_muestra_el_estado_vacio()
    {
        var cut = Render<ContactosSeguridadPlataforma>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Sin propuestas en revisión"));
        cut.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public void Validar_envia_el_comando_de_esa_propuesta_y_recarga()
    {
        var propuesto = Propuesto();
        _mediador.Propuestos = [propuesto];
        var cut = Render<ContactosSeguridadPlataforma>();
        cut.WaitForAssertion(() => cut.Find("table").TextContent.Should().Contain("Norte Servicios").And.Contain("Marta Ruiz"));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Validar").Click();

        cut.WaitForAssertion(() => _mediador.Validados.Should().ContainSingle().Which.Should().Be(propuesto.Id));
        _mediador.ConsultasDeLaCola.Should().Be(2);
    }

    [Fact]
    public void Rechazar_exige_motivo_antes_de_enviar()
    {
        var propuesto = Propuesto();
        _mediador.Propuestos = [propuesto];
        var cut = Render<ContactosSeguridadPlataforma>();
        cut.WaitForAssertion(() => cut.FindAll("table").Should().ContainSingle());

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Rechazar").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Rechazar propuesta").Click();

        cut.Markup.Should().Contain("Indica el motivo del rechazo.");
        _mediador.Rechazados.Should().BeEmpty();

        cut.Find("textarea").Input("La persona no pertenece a la organización");

        cut.Find("textarea").Blur();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Rechazar propuesta").Click();

        cut.WaitForAssertion(() => _mediador.Rechazados.Should().ContainSingle()
            .Which.Should().Be(new RechazarContactoSeguridadCommand(propuesto.Id, "La persona no pertenece a la organización")));
    }

    [Fact]
    public void Un_fallo_al_validar_se_muestra_y_no_recarga()
    {
        _mediador.Propuestos = [Propuesto()];
        _mediador.ResultadoResolver = Result.Fallo(ErroresContactoSeguridad.YaResuelto);
        var cut = Render<ContactosSeguridadPlataforma>();
        cut.WaitForAssertion(() => cut.FindAll("table").Should().ContainSingle());

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Validar").Click();

        cut.WaitForAssertion(() => cut.Find(".contactos-seguridad-error").TextContent.Should().Be(ErroresContactoSeguridad.YaResuelto.Mensaje));
        _mediador.ConsultasDeLaCola.Should().Be(1);
    }

    private sealed class MediadorFalso : IMediator
    {
        public ContactoSeguridadDeMiOrganizacionDto? MiOrganizacion { get; set; }
        public IReadOnlyList<ContactoSeguridadPropuestoDto> Propuestos { get; set; } = [];
        public Result ResultadoResolver { get; set; } = Result.Exito();
        public List<ProponerContactoSeguridadCommand> Propuestas { get; } = [];
        public List<Guid> Validados { get; } = [];
        public List<RechazarContactoSeguridadCommand> Rechazados { get; } = [];
        public int ConsultasDeLaCola { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerContactoSeguridadDeMiOrganizacionQuery:
                    return Task.FromResult((TResponse)(object)MiOrganizacion!);
                case ProponerContactoSeguridadCommand proponer:
                    Propuestas.Add(proponer);
                    return Task.FromResult((TResponse)(object)Result.Exito());
                case ObtenerContactosSeguridadPropuestosQuery:
                    ConsultasDeLaCola++;
                    return Task.FromResult((TResponse)(object)Propuestos);
                case ValidarContactoSeguridadCommand validar:
                    Validados.Add(validar.ContactoId);
                    return Task.FromResult((TResponse)(object)ResultadoResolver);
                case RechazarContactoSeguridadCommand rechazar:
                    Rechazados.Add(rechazar);
                    return Task.FromResult((TResponse)(object)ResultadoResolver);
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
