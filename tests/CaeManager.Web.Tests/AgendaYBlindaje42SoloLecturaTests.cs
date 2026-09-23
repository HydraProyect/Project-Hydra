using Bunit;
using CaeManager.Application.Blindaje42.Queries.ObtenerHistorialCertificacionesTgss;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Domain.Blindaje42;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Blindaje42.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La agenda de contactos (<see cref="PestanaAgendaContactos"/>, compartida por los paneles
/// 360) y el drawer del blindaje 42.1 (<see cref="DrawerBlindaje42"/>) no tenían clase de
/// tests propia. Aquí solo se prueba lo que ve el rol Consulta: sus disparadores de
/// escritura acaban en ICommand que AutorizacionEscrituraBehavior le deniega siempre,
/// así que no se le ofrecen; lo que es lectura (los contactos, el historial) sí.
/// </summary>
public class AgendaYBlindaje42SoloLecturaTests : BunitContext
{
    public AgendaYBlindaje42SoloLecturaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<ToastService>();
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<ContactoAgendaDto> Contactos { get; } = [];
        public List<SolicitudCertificacionTgssDto> Historial { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(request switch
            {
                ObtenerAgendaContactosQuery => (object)Contactos,
                ObtenerHistorialCertificacionesTgssQuery => Historial,
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            }));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private MediatorFalso Registrar(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        return mediador;
    }

    [Fact]
    public void Consulta_ve_los_contactos_de_la_agenda_sin_que_se_le_ofrezca_editarlos_ni_quitarlos()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var mediador = Registrar(new MediatorFalso());
        mediador.Contactos.Add(new ContactoAgendaDto(
            Guid.NewGuid(), "Nuria Salas Ortiz", "nuria@example.test", null, "PRL", null,
            EsPredeterminado: true, RecibeProgramacionVisitas: false, RecibeFacturacion: false, [], [], []));

        var cut = Render<PestanaAgendaContactos>(p => p
            .Add(x => x.Tipo, TipoPropietarioAgenda.Subcontrata)
            .Add(x => x.PropietarioId, Guid.NewGuid()));

        cut.Markup.Should().Contain("Nuria Salas Ortiz", "la agenda es lectura: el contacto se ve");
        cut.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().NotContain(["Editar", "Quitar"]);

    }

    [Fact]
    public void Un_rol_que_escribe_si_ve_editar_y_quitar_en_cada_contacto_de_la_agenda()
    {
        // Control positivo: sin él, el test de Consulta pasaría igual si la agenda no
        // pintara nunca esos botones.
        this.ConRolDeEscritura(Roles.GestorCae);
        var mediador = Registrar(new MediatorFalso());
        mediador.Contactos.Add(new ContactoAgendaDto(
            Guid.NewGuid(), "Nuria Salas Ortiz", "nuria@example.test", null, "PRL", null,
            EsPredeterminado: true, RecibeProgramacionVisitas: false, RecibeFacturacion: false, [], [], []));

        var cut = Render<PestanaAgendaContactos>(p => p
            .Add(x => x.Tipo, TipoPropietarioAgenda.Subcontrata)
            .Add(x => x.PropietarioId, Guid.NewGuid()));

        cut.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().Contain(["Editar", "Quitar"]);
    }

    [Fact]
    public void Consulta_ve_el_historial_del_blindaje_42_sin_que_se_le_ofrezca_registrar_solicitud_ni_respuesta()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var mediador = Registrar(new MediatorFalso());
        // Sin resultado y la más reciente: es la que, a un rol que escribe, ofrece «Registrar respuesta».
        mediador.Historial.Add(new SolicitudCertificacionTgssDto(
            Guid.NewGuid(), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), null, null,
            EstadoBlindaje42.PendienteRespuesta, null, false, null));

        var cut = Render<DrawerBlindaje42>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.ClienteId, Guid.NewGuid())
            .Add(x => x.EmpresaId, Guid.NewGuid())
            .Add(x => x.EmpresaRazonSocial, "Pinturas Lauburu S.A."));

        cut.Markup.Should().Contain("Solicitada el 01/09/2026", "el historial es lectura: se ve");
        cut.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().NotContain(["Registrar solicitud", "Registrar respuesta"]);
    }

    [Fact]
    public void Un_rol_que_escribe_si_ve_en_el_blindaje_42_registrar_solicitud_y_respuesta()
    {
        // Control positivo: sin él, el test de Consulta pasaría igual si el drawer no
        // pintara nunca esos botones.
        this.ConRolDeEscritura(Roles.GestorCae);
        var mediador = Registrar(new MediatorFalso());
        mediador.Historial.Add(new SolicitudCertificacionTgssDto(
            Guid.NewGuid(), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), null, null,
            EstadoBlindaje42.PendienteRespuesta, null, false, null));

        var cut = Render<DrawerBlindaje42>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.ClienteId, Guid.NewGuid())
            .Add(x => x.EmpresaId, Guid.NewGuid()));

        cut.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().Contain(["Registrar solicitud", "Registrar respuesta"]);
    }
}
