using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MiTrabajoPagina = CaeManager.Web.Features.Bandeja.Pages.MiTrabajo;

namespace CaeManager.Web.Tests;

/// <summary>
/// Ficha 02: Mi trabajo pinta cada Empresa (Tenant propietario) en cuanto termina, con «Cargando N de M» y barra.
/// Mientras la carga no acaba, la vista es parcial: una cola vacía no puede decir «al día» ni «sin cartera» de
/// Empresas que aún no se han consultado.
/// </summary>
public class MiTrabajoCargaPorPartesTests : BunitContext
{
    public MiTrabajoCargaPorPartesTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid TenantOrigen = Guid.Parse("0a0a0a0a-0000-0000-0000-000000000001");
    private static readonly Guid TenantRefri = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid TenantDexter = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");

    private static ItemBandejaDto Vencido(string id, string titulo) => new(
        Id: id, Tipo: TipoItemBandeja.Vencido, Titulo: titulo, Subtitulo: $"Trabajador {id}", Fecha: new DateOnly(2026, 10, 1),
        TrabajadorId: Guid.NewGuid(), CentroId: Guid.NewGuid(), DocumentoId: null, TipoDocumentoId: Guid.NewGuid(),
        RequisitoId: null, ClienteId: Guid.NewGuid(), ClienteNombre: "Cliente de prueba");

    private static MiTrabajoTenantDto Tenant(Guid id, string nombre, params ItemBandejaDto[] items) => new(
        id, nombre, id == TenantOrigen, ObtenerBandejaAgrupadaQueryHandler.Agrupar(items), [], [],
        new ResumenMiTrabajoTenantDto(id, nombre, id == TenantOrigen, items.Length, items.Length, 0, 0, 0), AlcanceCero: false);

    private sealed class MediadorPorPasos : IMediator
    {
        private readonly TaskCompletionSource[] _pasos = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        private readonly MiTrabajoTenantDto[] _tenants;

        public MediadorPorPasos(params MiTrabajoTenantDto[] tenants) => _tenants = tenants;

        /// <summary>Deja pasar la parte número <paramref name="indice"/> (0 = primera Empresa tras la apertura).</summary>
        public void Liberar(int indice) => _pasos[indice].TrySetResult();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => request switch
        {
            ObtenerCandidatosIncorporacionCarteraQuery => Task.FromResult((TResponse)(object)
                Result.Fallo<IReadOnlyList<CandidatoIncorporacionCarteraDto>>(ErroresSolicitudCartera.SinPermiso)),
            _ => throw new NotSupportedException($"Petición no prevista: {request.GetType().Name}.")
        };

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => request switch
        {
            ObtenerMiTrabajoPorPartesQuery => (IAsyncEnumerable<TResponse>)Partes(cancellationToken),
            _ => throw new NotSupportedException()
        };

        private async IAsyncEnumerable<ParteMiTrabajoDto> Partes([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new ParteMiTrabajoDto(_tenants.Length, 0);
            for (var i = 0; i < _tenants.Length; i++)
            {
                await _pasos[i].Task.WaitAsync(ct);
                yield return new ParteMiTrabajoDto(_tenants.Length, i + 1, _tenants[i]);
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    private IRenderedComponent<MiTrabajoPagina> Renderizar(MediadorPorPasos mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        Services.AddSingleton<ToastService>();
        Services.AddLocalization();
        return Render<MiTrabajoPagina>();
    }

    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);

    [Fact]
    public void La_primera_Empresa_se_pinta_con_contador_y_barra_antes_de_que_termine_la_segunda()
    {
        var mediador = new MediadorPorPasos(
            Tenant(TenantRefri, "Refrielectric", Vencido("r1", "Reconocimiento médico")),
            Tenant(TenantDexter, "Laboratorios Dexter", Vencido("d1", "Formación PRL")));
        var cut = Renderizar(mediador);

        mediador.Liberar(0);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".mi-trabajo-fila-titulo").Select(e => e.TextContent).Should().Equal("Reconocimiento médico");
            cut.Find(".mi-trabajo-progreso").TextContent.Should().Contain("1 de 2");
            cut.Find(".mi-trabajo-progreso-barra").GetAttribute("aria-valuenow").Should().Be("1");
            cut.Find(".mi-trabajo-progreso-barra").GetAttribute("aria-valuemax").Should().Be("2");
            cut.Markup.Should().NotContain("Laboratorios Dexter", "la segunda Empresa aún no ha terminado");
        }, Espera);
    }

    [Fact]
    public void Al_terminar_la_ultima_Empresa_desaparece_el_progreso_y_estan_todas()
    {
        var mediador = new MediadorPorPasos(
            Tenant(TenantRefri, "Refrielectric", Vencido("r1", "Reconocimiento médico")),
            Tenant(TenantDexter, "Laboratorios Dexter", Vencido("d1", "Formación PRL")));
        var cut = Renderizar(mediador);

        mediador.Liberar(0);
        cut.WaitForAssertion(() => cut.FindAll(".mi-trabajo-progreso").Should().NotBeEmpty(), Espera);
        mediador.Liberar(1);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".mi-trabajo-progreso").Should().BeEmpty();
            cut.FindAll(".mi-trabajo-fila-titulo").Select(e => e.TextContent).Should().BeEquivalentTo("Reconocimiento médico", "Formación PRL");
        }, Espera);
    }

    [Fact]
    public void Con_la_carga_en_marcha_una_cola_vacia_no_se_dice_al_dia()
    {
        // La primera Empresa terminó sin nada pendiente; la segunda aún no. «Al día» sería afirmar algo de una cola sin consultar.
        var mediador = new MediadorPorPasos(
            Tenant(TenantRefri, "Refrielectric"),
            Tenant(TenantDexter, "Laboratorios Dexter", Vencido("d1", "Formación PRL")));
        var cut = Renderizar(mediador);

        mediador.Liberar(0);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".mi-trabajo-progreso").TextContent.Should().Contain("1 de 2");
            cut.FindAll(".estado-vacio").Should().BeEmpty("la cartera aún se está consultando");
            cut.FindAll("[aria-busy='true']").Should().NotBeEmpty();
        }, Espera);

        mediador.Liberar(1);
        cut.WaitForAssertion(() => cut.FindAll(".mi-trabajo-fila-titulo").Select(e => e.TextContent).Should().Equal("Formación PRL"), Espera);
    }

    [Fact]
    public void Sin_ninguna_Empresa_en_la_cartera_la_pantalla_acaba_en_su_estado_vacio_de_siempre()
    {
        // Control positivo del guardián anterior: terminada la carga, el vacío sí se dice.
        var mediador = new MediadorPorPasos();
        var cut = Renderizar(mediador);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".mi-trabajo-progreso").Should().BeEmpty();
            cut.FindAll(".estado-vacio").Should().NotBeEmpty();
        }, Espera);
    }
}
