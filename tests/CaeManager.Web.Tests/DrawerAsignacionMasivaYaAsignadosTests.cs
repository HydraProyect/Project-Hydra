using Bunit;
using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// «+ Asignar trabajador» desde Centro 360: los Trabajadores que ya están asignados a ese Centro se ven marcados y no se
/// pueden seleccionar, y el aviso de documentos que faltan habla solo de los que se van a asignar ahora.
/// </summary>
public class DrawerAsignacionMasivaYaAsignadosTests : BunitContext
{
    public DrawerAsignacionMasivaYaAsignadosTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid YaAsignado = Guid.NewGuid();
    private static readonly Guid Nuevo = Guid.NewGuid();
    private static readonly Guid CentroId = Guid.NewGuid();
    private static readonly Guid OtroCentroId = Guid.NewGuid();

    private sealed class MediatorFalso : IMediator
    {
        private readonly List<CrearAsignacionesCommand> _altas = [];
        public IReadOnlyList<CrearAsignacionesCommand> AltasEnviadas => _altas;

        // Como la lectura real, no sabe de Asignaciones: devuelve lo que falta a cada pareja (Trabajador, Centro) pedida.
        private static IReadOnlyList<DocumentoFaltanteDto> Faltantes(ObtenerDocumentosFaltantesParaAsignacionQuery consulta) =>
            consulta.TrabajadorIds
                .SelectMany(t => consulta.CentroIds.Select(c => new DocumentoFaltanteDto(
                    t, t == YaAsignado ? "Ana Ya Asignada" : "Bea Nueva", c, c == CentroId ? "Planta Zaragoza" : "Planta Huesca", Guid.NewGuid(),
                    t == YaAsignado ? "Aptitud médica" : "Formación PRL específica")))
                .ToList();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerTrabajadoresParaSelectorQuery => new[]
                {
                    TrabajadorSelectorFalso.Crear(YaAsignado, "Ana Ya Asignada"),
                    TrabajadorSelectorFalso.Crear(Nuevo, "Bea Nueva"),
                },
                ObtenerCentrosParaSelectorQuery => new[]
                {
                    new CentroSelectorDto(CentroId, "Planta Zaragoza", "Refrielectric S.A.", "Montajes Ebro S.L."),
                    new CentroSelectorDto(OtroCentroId, "Planta Huesca", "Refrielectric S.A.", "Montajes Ebro S.L."),
                },
                ObtenerDocumentosFaltantesParaAsignacionQuery consulta => Faltantes(consulta),
                CrearAsignacionesCommand alta => Registrar(alta),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            }));

        private Result<ResultadoAsignacionLoteDto> Registrar(CrearAsignacionesCommand alta)
        {
            _altas.Add(alta);
            return Result.Exito(new ResultadoAsignacionLoteDto(alta.TrabajadorIds.Count, 0, 0, []));
        }

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

    private async Task<(IRenderedComponent<DrawerAsignacionMasiva> Cut, MediatorFalso Mediador)> AbrirAsync(bool conYaAsignados)
    {
        var mediador = new MediatorFalso();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();

        var cut = Render<DrawerAsignacionMasiva>(p => p.Add(d => d.OnGuardado, () => Task.CompletedTask));
        IReadOnlySet<Guid>? yaAsignados = conYaAsignados ? new HashSet<Guid> { YaAsignado } : null;
        await cut.InvokeAsync(() => cut.Instance.AbrirAsync(new[] { CentroId }, yaAsignados));
        return (cut, mediador);
    }

    private static IRenderedComponent<SelectorMultiple> Trabajadores(IRenderedComponent<DrawerAsignacionMasiva> cut) =>
        cut.FindComponents<SelectorMultiple>().Single(c => c.Instance.Etiqueta == "Trabajadores");

    [Fact]
    public async Task El_Trabajador_ya_asignado_al_Centro_se_ve_marcado_y_no_seleccionable()
    {
        var (cut, _) = await AbrirAsync(conYaAsignados: true);

        // La primera lista es la de Trabajadores; la segunda, la de Centros.
        var casillas = cut.FindAll(".lista-seleccion-multiple")[0].QuerySelectorAll("input[type=checkbox]").ToList();
        var ya = casillas.Single(c => c.HasAttribute("data-bloqueado"));
        ya.HasAttribute("disabled").Should().BeTrue();
        ya.HasAttribute("checked").Should().BeTrue();
        ya.ParentElement!.TextContent.Should().Contain("Ana Ya Asignada").And.Contain("ya asignado a este centro");

        var nueva = casillas.Single(c => !c.HasAttribute("data-bloqueado"));
        nueva.HasAttribute("disabled").Should().BeFalse();
        nueva.ParentElement!.TextContent.Should().Contain("Bea Nueva").And.NotContain("ya asignado");
    }

    [Fact]
    public async Task Un_Trabajador_ya_asignado_no_entra_en_la_seleccion_aunque_se_intente()
    {
        var (cut, _) = await AbrirAsync(conYaAsignados: true);
        var selector = Trabajadores(cut);

        await cut.InvokeAsync(() => selector.Instance.OnAlternar.InvokeAsync((YaAsignado, true)));

        Trabajadores(cut).Instance.Seleccionados.Should().BeEmpty();
    }

    [Fact]
    public async Task El_aviso_de_documentos_que_faltan_no_mezcla_a_los_ya_asignados_con_los_nuevos()
    {
        var (cut, _) = await AbrirAsync(conYaAsignados: true);

        // El Centro viene marcado desde la ficha: basta con marcar al nuevo (y el intento de marcar al ya asignado no cuenta).
        await cut.InvokeAsync(() => Trabajadores(cut).Instance.OnAlternar.InvokeAsync((YaAsignado, true)));
        await cut.InvokeAsync(() => Trabajadores(cut).Instance.OnAlternar.InvokeAsync((Nuevo, true)));

        var aviso = string.Join(' ', cut.Find(".alerta-preflight-asignacion").TextContent
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        aviso.Should().Contain("Al comprobarlo faltaban 1 documento(s) que se piden")
            .And.Contain("Bea Nueva — Formación PRL específica (Planta Zaragoza)")
            .And.NotContain("Ana Ya Asignada").And.NotContain("Aptitud médica");
    }

    [Fact]
    public async Task Al_guardar_solo_se_asigna_al_Trabajador_nuevo()
    {
        var (cut, mediador) = await AbrirAsync(conYaAsignados: true);
        await cut.InvokeAsync(() => Trabajadores(cut).Instance.OnAlternar.InvokeAsync((YaAsignado, true)));
        await cut.InvokeAsync(() => Trabajadores(cut).Instance.OnAlternar.InvokeAsync((Nuevo, true)));

        cut.Find(".drawer-pie button:last-child").Click();

        cut.WaitForAssertion(() => mediador.AltasEnviadas.Should().ContainSingle());
        var alta = mediador.AltasEnviadas.Single();
        alta.TrabajadorIds.Should().Equal(Nuevo);
        alta.CentroIds.Should().Equal(CentroId);
    }

    private static IRenderedComponent<SelectorMultiple> Centros(IRenderedComponent<DrawerAsignacionMasiva> cut) =>
        cut.FindComponents<SelectorMultiple>().Single(c => c.Instance.Etiqueta == "Centros");

    private static string AvisoDeFaltantes(IRenderedComponent<DrawerAsignacionMasiva> cut) =>
        string.Join(' ', cut.Find(".alerta-preflight-asignacion").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public async Task Con_otro_Centro_marcado_el_ya_asignado_se_puede_asignar_y_el_aviso_solo_cuenta_su_pareja_nueva()
    {
        var (cut, _) = await AbrirAsync(conYaAsignados: true);

        await cut.InvokeAsync(() => Centros(cut).Instance.OnAlternar.InvokeAsync((OtroCentroId, true)));
        cut.FindAll("input[data-bloqueado]").Should().BeEmpty("con dos Centros marcados ya no hay nada que bloquear");
        await cut.InvokeAsync(() => Trabajadores(cut).Instance.OnAlternar.InvokeAsync((YaAsignado, true)));

        // La lectura devuelve (Ana, Zaragoza) y (Ana, Huesca): la primera ya está asignada y no es un alta nueva.
        var aviso = AvisoDeFaltantes(cut);
        aviso.Should().Contain("1 documento(s) que se piden").And.Contain("Ana Ya Asignada — Aptitud médica (Planta Huesca)")
            .And.NotContain("Planta Zaragoza");
    }

    [Fact]
    public async Task Desmarcar_el_otro_Centro_vuelve_a_bloquear_al_ya_asignado_y_lo_saca_de_la_seleccion()
    {
        var (cut, _) = await AbrirAsync(conYaAsignados: true);
        await cut.InvokeAsync(() => Centros(cut).Instance.OnAlternar.InvokeAsync((OtroCentroId, true)));
        await cut.InvokeAsync(() => Trabajadores(cut).Instance.OnAlternar.InvokeAsync((YaAsignado, true)));

        await cut.InvokeAsync(() => Centros(cut).Instance.OnAlternar.InvokeAsync((OtroCentroId, false)));

        cut.FindAll("input[data-bloqueado]").Should().ContainSingle();
        Trabajadores(cut).Instance.Seleccionados.Should().BeEmpty("un bloqueado se ve marcado y deshabilitado: no puede seguir en la selección sin que nadie pueda quitarlo");
    }

    [Fact]
    public async Task Desmarcar_y_volver_a_marcar_el_Centro_anfitrion_no_deja_al_ya_asignado_en_la_seleccion()
    {
        var (cut, _) = await AbrirAsync(conYaAsignados: true);
        await cut.InvokeAsync(() => Centros(cut).Instance.OnAlternar.InvokeAsync((CentroId, false)));
        await cut.InvokeAsync(() => Trabajadores(cut).Instance.OnAlternar.InvokeAsync((YaAsignado, true)));

        await cut.InvokeAsync(() => Centros(cut).Instance.OnAlternar.InvokeAsync((CentroId, true)));

        cut.FindAll("input[data-bloqueado]").Should().ContainSingle();
        Trabajadores(cut).Instance.Seleccionados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_dato_de_asignados_no_se_bloquea_a_nadie()
    {
        var (cut, _) = await AbrirAsync(conYaAsignados: false);

        cut.FindAll("input[data-bloqueado]").Should().BeEmpty();
        cut.FindAll(".lista-seleccion-multiple input[type=checkbox]").Should().OnlyContain(c => !c.HasAttribute("disabled"));
    }
}
