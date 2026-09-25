using Bunit;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.IncorporacionCartera.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P0-9a (FS-03 a FS-06): con alcance cero —un Gestor CAE sin ninguna
/// Asignación de Cartera vigente en el Tenant, o el Administrador de un
/// Operador CAE externo que ve el Tenant delegado sin cartera— una lista o una
/// cola vacía no dice «al día» ni invita a crear. <see cref="VacioSegunAlcance"/>
/// decide entre el aviso y el vacío propio de la pantalla; aquí se prueba él
/// solo, y cada pantalla lo prueba montado en su propio fichero de vacíos.
/// </summary>
public class VacioSegunAlcanceTests : BunitContext
{
    /// <summary>Responde el alcance cero y los candidatos de incorporación a cartera; cualquier otra consulta lanza.</summary>
    internal sealed class MediatorAlcance : IMediator
    {
        public Func<Task<bool>> AlcanceCero { get; init; } = () => Task.FromResult(false);
        public IReadOnlyList<CandidatoIncorporacionCarteraDto> Candidatos { get; init; } = [];
        public int ConsultasAlcance { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerAlcanceCeroQuery:
                    ConsultasAlcance++;
                    return Convertir<TResponse>(AlcanceCero());
                case ObtenerCandidatosIncorporacionCarteraQuery:
                    return Task.FromResult((TResponse)(object)Result.Exito(Candidatos));
                default:
                    throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");
            }
        }

        // Una tarea ya completada se espera sin ceder: el render la ve en la misma pasada.
        private static async Task<TResponse> Convertir<TResponse>(Task<bool> alcance) => (TResponse)(object)await alcance;

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

    private MediatorAlcance Registrar(MediatorAlcance mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddLocalization();
        return mediador;
    }

    private static RenderFragment VacioPositivo => b =>
    {
        b.OpenElement(0, "p");
        b.AddAttribute(1, "class", "vacio-positivo");
        b.AddContent(2, "Todo al día");
        b.CloseElement();
    };

    [Fact]
    public void Con_alcance_cero_pinta_el_aviso_y_no_el_vacio_positivo()
    {
        Registrar(new MediatorAlcance { AlcanceCero = () => Task.FromResult(true) });

        var cut = Render<VacioSegunAlcance>(p => p.AddChildContent(VacioPositivo));

        cut.Find("[data-estado=sin-asignacion-cartera]").TextContent
            .Should().Contain("Sin Asignación de Cartera").And.Contain("Coordinador CAE");
        cut.Markup.Should().NotContain("Todo al día");
    }

    [Fact]
    public void Con_alcance_pinta_el_vacio_de_la_pantalla_sin_cambios()
    {
        Registrar(new MediatorAlcance { AlcanceCero = () => Task.FromResult(false) });

        var cut = Render<VacioSegunAlcance>(p => p.AddChildContent(VacioPositivo));

        cut.Find(".vacio-positivo").TextContent.Should().Be("Todo al día");
        cut.FindAll("[data-estado=sin-asignacion-cartera]").Should().BeEmpty();
    }

    [Fact]
    public void Mientras_no_sabe_el_alcance_no_pinta_el_vacio_positivo()
    {
        var pendiente = new TaskCompletionSource<bool>();
        Registrar(new MediatorAlcance { AlcanceCero = () => pendiente.Task });

        var cut = Render<VacioSegunAlcance>(p => p.AddChildContent(VacioPositivo));

        cut.Markup.Should().NotContain("Todo al día", "enseñarlo un instante sería el verde falso que se quiere evitar");
        cut.FindAll("[data-estado=sin-asignacion-cartera]").Should().BeEmpty();

        pendiente.SetResult(true);
        cut.WaitForAssertion(() => cut.Find("[data-estado=sin-asignacion-cartera]"));
    }

    [Fact]
    public void Si_la_consulta_falla_cae_al_vacio_de_la_pantalla()
    {
        Registrar(new MediatorAlcance { AlcanceCero = () => Task.FromException<bool>(new InvalidOperationException("caída")) });

        var cut = Render<VacioSegunAlcance>(p => p.AddChildContent(VacioPositivo));

        cut.Find(".vacio-positivo").TextContent.Should().Be("Todo al día");
    }

    [Fact]
    public void Avisa_a_la_pagina_para_que_esconda_su_nuevo_de_cabecera()
    {
        Registrar(new MediatorAlcance { AlcanceCero = () => Task.FromResult(true) });
        bool? recibido = null;

        Render<VacioSegunAlcance>(p => p
            .AddChildContent(VacioPositivo)
            .Add(c => c.AlcanceCeroChanged, (bool v) => recibido = v));

        recibido.Should().BeTrue();
    }

    [Fact]
    public void Con_Empresas_que_pedir_ofrece_anadir_a_mi_cartera()
    {
        Registrar(new MediatorAlcance
        {
            AlcanceCero = () => Task.FromResult(true),
            Candidatos = [new CandidatoIncorporacionCarteraDto(Guid.NewGuid(), "Refrielectric", null)],
        });

        var cut = Render<VacioSegunAlcance>(p => p.AddChildContent(VacioPositivo));

        cut.Find("[data-estado=sin-asignacion-cartera] button").TextContent.Should().Contain("Añadir a mi cartera");
    }

    [Fact]
    public void Sin_Empresas_que_pedir_no_ofrece_boton_y_remite_al_Coordinador_CAE()
    {
        Registrar(new MediatorAlcance { AlcanceCero = () => Task.FromResult(true) });

        var cut = Render<VacioSegunAlcance>(p => p.AddChildContent(VacioPositivo));

        cut.FindAll("[data-estado=sin-asignacion-cartera] button").Should().BeEmpty();
        cut.Find("[data-estado=sin-asignacion-cartera]").TextContent.Should().Contain("Coordinador CAE");
    }
}
