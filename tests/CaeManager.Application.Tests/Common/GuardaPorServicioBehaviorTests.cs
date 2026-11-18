using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// La guarda por servicio (B-02b; DN-21, punto 3) falla cerrada en los dos sentidos: un comando Inbound solo pasa en el
/// Tenant de origen o con papel Inbound, y con papel Inbound solo pasan comandos Inbound o de autoservicio. Las lecturas
/// no las mira.
/// </summary>
public class GuardaPorServicioBehaviorTests
{
    private sealed record ComandoInbound : ICommand, IComandoInbound;
    private sealed record ComandoInboundConValor : ICommand<Guid>, IComandoInbound;
    private sealed record ComandoSinServicio : ICommand;
    private sealed record ComandoDeAutoservicio : ICommand, IComandoDeAutoservicio;
    private sealed record Consulta : IRequest<Result>;

    /// <summary>Un usuario cualquiera; <paramref name="papel"/> nulo deja la implementación por defecto de la interfaz.</summary>
    private sealed class UsuarioConPapel(PapelActivo? papel) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
        public Task<PapelActivo> ObtenerPapelActivoAsync() =>
            papel is { } valor ? Task.FromResult(valor) : ((ICurrentUserService)new SinPapelConocido()).ObtenerPapelActivoAsync();
    }

    /// <summary>No sobrescribe el papel: responde la implementación por defecto de <see cref="ICurrentUserService"/>.</summary>
    private sealed class SinPapelConocido : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }

    /// <summary>
    /// El criterio real (<see cref="CriterioDeOutbound"/>) sobre un Tenant activo cuyo Producto Contratado es o no solo
    /// Inbound. Cuenta las lecturas para probar que con otro papel no se consulta.
    /// </summary>
    private static CriterioDeOutbound Criterio(bool soloInbound) => new(new ServiceCollection()
        .AddSingleton<ITenantActual>(new TenantFijo(Guid.NewGuid()))
        .AddSingleton<ITenantSoloInbound>(new SoloInboundFijo(soloInbound))
        .BuildServiceProvider());

    private sealed class TenantFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId { get; } = tenantId;
    }

    private sealed class SoloInboundFijo(bool soloInbound) : ITenantSoloInbound
    {
        public Task<bool> EsSoloInboundAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(soloInbound);
    }

    private static async Task<(TResponse Respuesta, bool LlegoAlHandler)> Ejecutar<TRequest, TResponse>(
        TRequest request, PapelActivo papel, TResponse exito, bool soloInbound = false) where TRequest : notnull
    {
        var llego = false;
        var respuesta = await new GuardaPorServicioBehavior<TRequest, TResponse>(new UsuarioConPapel(papel), Criterio(soloInbound)).Handle(
            request, _ => { llego = true; return Task.FromResult(exito); }, default);
        return (respuesta, llego);
    }

    [Theory]
    [InlineData(PapelActivo.TenantDeOrigen, true)]
    [InlineData(PapelActivo.Inbound, true)]
    [InlineData(PapelActivo.Outbound, false)]
    [InlineData(PapelActivo.SinPapel, false)]
    [InlineData(PapelActivo.OperacionQueYaNoAutoriza, false)]
    public async Task Un_comando_Inbound_solo_pasa_en_el_Tenant_de_origen_o_con_papel_Inbound(PapelActivo papel, bool pasa)
    {
        var (respuesta, llego) = await Ejecutar(new ComandoInbound(), papel, Result.Exito());

        llego.Should().Be(pasa);
        respuesta.EsExitoso.Should().Be(pasa);
        if (!pasa)
            respuesta.Error!.Codigo.Should().Be(papel == PapelActivo.OperacionQueYaNoAutoriza
                ? "Autorizacion.OperacionQueYaNoAutoriza"
                : "Autorizacion.ComandoInboundFueraDePapel");
    }

    [Fact]
    public async Task El_fallo_de_un_comando_con_valor_es_un_Result_de_su_tipo()
    {
        var (respuesta, llego) = await Ejecutar(new ComandoInboundConValor(), PapelActivo.Outbound, Result.Exito(Guid.NewGuid()));

        llego.Should().BeFalse();
        respuesta.EsExitoso.Should().BeFalse();
    }

    [Theory]
    [InlineData(PapelActivo.TenantDeOrigen, true)]
    [InlineData(PapelActivo.Outbound, true)]
    [InlineData(PapelActivo.SinPapel, true)]
    [InlineData(PapelActivo.Inbound, false)]
    [InlineData(PapelActivo.OperacionQueYaNoAutoriza, false)]
    public async Task Con_papel_Inbound_un_comando_que_no_es_de_Inbound_falla_cerrado(PapelActivo papel, bool pasa)
    {
        var (respuesta, llego) = await Ejecutar(new ComandoSinServicio(), papel, Result.Exito());

        llego.Should().Be(pasa);
        if (!pasa)
            respuesta.Error!.Codigo.Should().Be(papel == PapelActivo.OperacionQueYaNoAutoriza
                ? "Autorizacion.OperacionQueYaNoAutoriza"
                : "Autorizacion.ComandoFueraDelPapelInbound");
    }

    /// <summary>
    /// Las páginas de Outbound y el alcance de datos usan <see cref="CriterioDePapel"/>; la guarda decide lo mismo para un
    /// comando que no es Inbound ni de autoservicio, con todos los valores del papel, también los que se añadan.
    /// </summary>
    [Fact]
    public async Task La_guarda_y_el_criterio_de_las_paginas_de_Outbound_coinciden_para_todo_papel()
    {
        foreach (var papel in Enum.GetValues<PapelActivo>())
        {
            var (_, llego) = await Ejecutar(new ComandoSinServicio(), papel, Result.Exito());

            llego.Should().Be(CriterioDePapel.AdmiteLoQueNoEsInbound(papel), $"con papel {papel}");
        }
    }

    [Theory]
    [InlineData(PapelActivo.TenantDeOrigen, true)]
    [InlineData(PapelActivo.Outbound, true)]
    [InlineData(PapelActivo.SinPapel, true)]
    [InlineData(PapelActivo.Inbound, false)]
    [InlineData(PapelActivo.OperacionQueYaNoAutoriza, false)]
    public void El_criterio_de_papel_admite_lo_que_no_es_Inbound(PapelActivo papel, bool admite)
    {
        CriterioDePapel.AdmiteLoQueNoEsInbound(papel).Should().Be(admite);
    }

    [Theory]
    [InlineData(PapelActivo.Inbound)]
    [InlineData(PapelActivo.OperacionQueYaNoAutoriza)]
    public async Task Un_comando_de_autoservicio_pasa_con_papel_Inbound_y_con_una_operacion_que_ya_no_autoriza(PapelActivo papel)
    {
        var (_, llego) = await Ejecutar(new ComandoDeAutoservicio(), papel, Result.Exito());

        llego.Should().BeTrue();
    }

    [Theory]
    [InlineData(PapelActivo.Inbound)]
    [InlineData(PapelActivo.Outbound)]
    [InlineData(PapelActivo.SinPapel)]
    public async Task Las_lecturas_no_pasan_por_la_guarda(PapelActivo papel)
    {
        var (_, llego) = await Ejecutar(new Consulta(), papel, Result.Exito());

        llego.Should().BeTrue();
    }

    [Fact]
    public async Task Quien_no_sabe_el_papel_falla_cerrado()
    {
        // La implementación por defecto de ICurrentUserService: un doble que no sobrescribe el papel.
        var llego = false;

        await new GuardaPorServicioBehavior<ComandoInbound, Result>(new SinPapelConocido(), Criterio(soloInbound: true)).Handle(
            new ComandoInbound(), _ => { llego = true; return Task.FromResult(Result.Exito()); }, default);

        llego.Should().BeFalse();
    }
    /// <summary>
    /// «Inbound sin Outbound» (decisión del 2026-11-18): con el papel de Tenant de origen en un Tenant solo Inbound, un
    /// comando del módulo Outbound no pasa; con cualquier otro papel, o en un Tenant que no es solo Inbound (el Outbound
    /// histórico sin líneas, o el que tiene las dos), decide lo de siempre.
    /// </summary>
    [Theory]
    [InlineData(PapelActivo.TenantDeOrigen, true, false)]
    [InlineData(PapelActivo.TenantDeOrigen, false, true)]
    [InlineData(PapelActivo.Outbound, true, true)]
    [InlineData(PapelActivo.SinPapel, true, true)]
    [InlineData(PapelActivo.Inbound, true, false)]
    public async Task Un_comando_de_Outbound_se_cierra_al_Tenant_de_origen_solo_Inbound(PapelActivo papel, bool soloInbound, bool pasa)
    {
        var (respuesta, llego) = await Ejecutar(
            new CaeManager.Application.Subcontratas.PruebasDeGuarda.ComandoDeOutbound(), papel, Result.Exito(), soloInbound);

        llego.Should().Be(pasa);
        if (!pasa && papel == PapelActivo.TenantDeOrigen)
            respuesta.Error!.Codigo.Should().Be("Autorizacion.ComandoDeOutboundSinLineaOutbound");
    }

    [Fact]
    public async Task Con_un_Tenant_solo_Inbound_pasan_los_comandos_del_Nucleo_de_Inbound_y_de_autoservicio()
    {
        (await Ejecutar(new ComandoSinServicio(), PapelActivo.TenantDeOrigen, Result.Exito(), soloInbound: true)).LlegoAlHandler
            .Should().BeTrue("un comando del Núcleo no es de Outbound: el Tenant solo Inbound sigue administrando");
        (await Ejecutar(new ComandoInbound(), PapelActivo.TenantDeOrigen, Result.Exito(), soloInbound: true)).LlegoAlHandler
            .Should().BeTrue();
        (await Ejecutar(new CaeManager.Application.Subcontratas.PruebasDeGuarda.ComandoDeOutboundDeAutoservicio(),
            PapelActivo.TenantDeOrigen, Result.Exito(), soloInbound: true)).LlegoAlHandler.Should().BeTrue();
    }

    [Fact]
    public void Los_comandos_de_prueba_estan_donde_el_clasificador_los_espera()
    {
        // Control del instrumento: si el clasificador no viera el comando de prueba como de Outbound, el caso de arriba
        // pasaría por la rama equivocada.
        PeticionDeOutbound.Es(typeof(CaeManager.Application.Subcontratas.PruebasDeGuarda.ComandoDeOutbound)).Should().BeTrue();
        PeticionDeOutbound.Es(typeof(ComandoSinServicio)).Should().BeFalse();
    }
}
