using Bunit;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MiTrabajoPagina = CaeManager.Web.Features.Bandeja.Pages.MiTrabajo;

namespace CaeManager.Web.Tests;

/// <summary>
/// Mi trabajo con la cola que fabrica la fusión real de la Bandeja, con las consultas resolviendo
/// de forma asíncrona como en el circuito. Defecto medido en el E2E del Coordinador CAE
/// (<c>El_Tenant_beneficiario_elegido_sobrevive_al_menu_lateral_del_Coordinador_CAE</c>, rojo
/// dos veces en CI): dos filas hermanas con el mismo Id son dos <c>@key</c> iguales, el diff
/// de Blazor lanza «Attempting to return wrong pooled instance» y el circuito de la landing
/// muere en el primer segundo. Los tests síncronos de <c>MiTrabajoGen2Tests</c> no lo ven:
/// con consultas ya resueltas la página pinta una sola vez y nunca diferencia contra su
/// pintado anterior.
/// </summary>
public class MiTrabajoClavesUnicasTests : BunitContext
{
    public MiTrabajoClavesUnicasTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid TenantOrigen = Guid.Parse("0a0a0a0a-0000-0000-0000-000000000001");
    private static readonly Guid TenantBeneficiario = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");

    private sealed class MediadorAsincrono(MiTrabajoAgregadoDto datos) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            // Cede el hilo de verdad: es lo que hace una consulta a PostgreSQL.
            await Task.Delay(20, cancellationToken);
            return request switch
            {
                ObtenerMiTrabajoAgregadoQuery => (TResponse)(object)datos,
                ObtenerCandidatosIncorporacionCarteraQuery => EntregarCandidatos<TResponse>(),
                _ => throw new NotSupportedException($"Petición no prevista: {request.GetType().Name}.")
            };
        }

        /// <summary>Se completa cuando la segunda consulta de la página (los candidatos) ya respondió.</summary>
        public TaskCompletionSource CandidatosEntregados { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TResponse EntregarCandidatos<TResponse>()
        {
            var respuesta = (TResponse)(object)Result.Fallo<IReadOnlyList<CandidatoIncorporacionCarteraDto>>(ErroresSolicitudCartera.SinPermiso);
            CandidatosEntregados.TrySetResult();
            return respuesta;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => request switch
        {
            ObtenerMiTrabajoPorPartesQuery => (IAsyncEnumerable<TResponse>)PartesDeMiTrabajo.De(datos, async ct => await Task.Delay(20, ct), cancellationToken),
            _ => throw new NotSupportedException()
        };
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    private static AlertaDto Faltante(Guid trabajador, Guid tipo, Guid centro, string nombreCentro) => new(
        DocumentoId: null, TrabajadorId: trabajador, TrabajadorNombre: "Ana García",
        TipoDocumentoId: tipo, TipoDocumentoNombre: "Apto médico", FechaVencimiento: null,
        Estado: EstadoDocumento.Faltante, ArchivoUrl: null, CentroNombre: nombreCentro, CentroId: centro);

    /// <summary>La cola de un Tenant beneficiario tal como la entrega la fusión de la Bandeja.</summary>
    private static MiTrabajoAgregadoDto ColaDe(IReadOnlyList<AlertaDto> alertas)
    {
        var items = ObtenerBandejaGestorQueryHandler.Fusionar(alertas, [], [], [], [], [], [], new DateOnly(2026, 10, 3), 48, 24);
        var bloqueos = items.Count(ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo);
        return new MiTrabajoAgregadoDto(
        [
            new MiTrabajoTenantDto(TenantOrigen, "ArcoSPA", true, ObtenerBandejaAgrupadaQueryHandler.Agrupar([]), [], [],
                new ResumenMiTrabajoTenantDto(TenantOrigen, "ArcoSPA", true, 0, 0, 0, 0, 0), false),
            new MiTrabajoTenantDto(TenantBeneficiario, "Refrielectric", false, ObtenerBandejaAgrupadaQueryHandler.Agrupar(items), [], [],
                new ResumenMiTrabajoTenantDto(TenantBeneficiario, "Refrielectric", false, items.Count, bloqueos, items.Count - bloqueos, 0, 0), false),
        ]);
    }

    /// <summary>Pinta Mi trabajo con la cola dada y espera a que la página termine de inicializarse (consultas asíncronas, como el circuito).</summary>
    private async Task<IRenderedComponent<MiTrabajoPagina>> PintarAsentadoAsync(MiTrabajoAgregadoDto cola, int filasEsperadas)
    {
        var mediador = new MediadorAsincrono(cola);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        Services.AddSingleton<ToastService>();
        Services.AddLocalization();
        var estado = Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, CaeManager.Infrastructure.Identity.Roles.CoordinadorCae)], "prueba"))));

        var cut = Render<MiTrabajoPagina>(p => p.AddCascadingValue(estado));
        cut.WaitForAssertion(() => cut.FindAll(".mi-trabajo-fila").Count.Should().Be(filasEsperadas), TimeSpan.FromSeconds(5));

        // Asentar: la página termina su inicialización (segunda consulta y último repintado) antes de
        // repintar desde la prueba; dos pasadas por el despachador del renderizador vacían su cola.
        await mediador.CandidatosEntregados.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });
        return cut;
    }

    [Fact]
    public async Task Un_Trabajador_en_dos_Centros_sin_el_mismo_documento_pinta_sus_dos_filas_y_el_circuito_sigue_vivo()
    {
        var trabajador = Guid.NewGuid();
        var tipo = Guid.NewGuid();
        var cola = ColaDe(
        [
            Faltante(trabajador, tipo, Guid.NewGuid(), "Centro Norte"),
            Faltante(trabajador, tipo, Guid.NewGuid(), "Centro Sur"),
        ]);

        var cut = await PintarAsentadoAsync(cola, filasEsperadas: 2);

        // Un repintado más: es el diff contra el árbol anterior el que se rompía con claves repetidas.
        cut.Render();
        cut.FindAll(".mi-trabajo-fila").Should().HaveCount(2);
    }

    private static ItemBandejaDto FilaConId(string id, string titulo) => new(
        Id: id, Tipo: TipoItemBandeja.Faltante, Titulo: titulo, Subtitulo: "Ana García — Centro",
        Fecha: null, TrabajadorId: Guid.NewGuid(), CentroId: Guid.NewGuid(), DocumentoId: null,
        TipoDocumentoId: Guid.NewGuid(), RequisitoId: null);

    private static MiTrabajoTenantDto TenantConFilas(Guid tenantId, string nombre, params ItemBandejaDto[] items) => new(
        tenantId, nombre, false, ObtenerBandejaAgrupadaQueryHandler.Agrupar(items), [], [],
        new ResumenMiTrabajoTenantDto(tenantId, nombre, false, items.Length, items.Length, 0, 0, 0), false);

    /// <summary>
    /// Salvaguarda que no depende del modelo: aunque un productor emita el mismo Id de fila dos veces en un Tenant, y el mismo Id
    /// en dos Tenants (el Id no lleva Tenant y «Agrupar por severidad» los mezcla en un mismo grupo), la página pinta TODAS las
    /// filas con claves distintas y sobrevive al repintado, en las dos agrupaciones.
    /// </summary>
    [Fact]
    public async Task Ids_de_fila_repetidos_dentro_de_un_Tenant_y_entre_Tenants_no_producen_claves_repetidas_en_ninguna_agrupacion()
    {
        var segundoTenant = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");
        var cola = new MiTrabajoAgregadoDto(
        [
            TenantConFilas(TenantBeneficiario, "Refrielectric", FilaConId("alerta-igual", "Apto médico"), FilaConId("alerta-igual", "Apto médico")),
            TenantConFilas(segundoTenant, "Laboratorios Dexter", FilaConId("alerta-igual", "Apto médico")),
        ]);

        var cut = await PintarAsentadoAsync(cola, filasEsperadas: 3);

        ClavesDeRender.Duplicadas(this, cut).Should().BeEmpty("agrupado por Tenant propietario (pestaña «Empresa»)");
        ClavesDeRender.ClavesObservadas(this, cut).Should().BeGreaterThanOrEqualTo(3 + 2, "tres filas más los dos grupos de Tenant: el detector observa claves");
        cut.Render();

        cut.FindAll(".mi-trabajo-pestana").Single(b => b.TextContent == "Severidad").Click();
        cut.FindAll(".mi-trabajo-fila").Should().HaveCount(3);
        ClavesDeRender.Duplicadas(this, cut).Should().BeEmpty("agrupado por severidad: un solo grupo con filas de los dos Tenants");
        cut.Render();
        cut.FindAll(".mi-trabajo-fila").Should().HaveCount(3);
    }
}
