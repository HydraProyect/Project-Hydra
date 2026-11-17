using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Guarda de pestaña obsoleta (D-6 b, R-2): un comando de un ámbito cuya selección ya no es la vigente no se
/// ejecuta, se rechaza con un error que pide recargar y queda auditado. Las consultas pasan: la vista no escribe.
/// </summary>
public class GuardaPestanaObsoletaBehaviorTests
{
    private readonly RegistroFalso _registro = new();

    [Fact]
    public async Task Un_comando_con_la_seleccion_vigente_se_ejecuta_sin_auditar_rechazo()
    {
        var ejecutado = false;

        var resultado = await Behavior<ComandoDePrueba, Result>(null).Handle(new ComandoDePrueba(), _ =>
        {
            ejecutado = true;
            return Task.FromResult(Result.Exito());
        }, default);

        resultado.EsExitoso.Should().BeTrue();
        ejecutado.Should().BeTrue();
        _registro.Rechazos.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SeleccionNoVigente.CambiadaEnOtraPestana, GuardaPestanaObsoletaBehavior<ComandoDePrueba, Result>.MensajeCambiada)]
    [InlineData(SeleccionNoVigente.RetiradaTrasPintar, GuardaPestanaObsoletaBehavior<ComandoDePrueba, Result>.MensajeRetirada)]
    public async Task Un_comando_de_una_pestana_obsoleta_se_rechaza_sin_ejecutarse_y_queda_auditado(
        SeleccionNoVigente motivo, string mensaje)
    {
        var ejecutado = false;

        var resultado = await Behavior<ComandoDePrueba, Result>(motivo).Handle(new ComandoDePrueba(), _ =>
        {
            ejecutado = true;
            return Task.FromResult(Result.Exito());
        }, default);

        ejecutado.Should().BeFalse("el comando no puede llegar al handler con una selección que ya no es la vigente");
        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be(GuardaPestanaObsoletaBehavior<ComandoDePrueba, Result>.CodigoError);
        resultado.Error.Mensaje.Should().Be(mensaje);
        _registro.Rechazos.Should().Equal((nameof(ComandoDePrueba), motivo));
    }

    [Fact]
    public async Task Un_comando_con_respuesta_tipada_recibe_el_fallo_tipado()
    {
        var resultado = await Behavior<ComandoTipado, Result<int>>(SeleccionNoVigente.CambiadaEnOtraPestana)
            .Handle(new ComandoTipado(), _ => Task.FromResult(Result.Exito(1)), default);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be(GuardaPestanaObsoletaBehavior<ComandoTipado, Result<int>>.CodigoError);
    }

    [Fact]
    public async Task Una_consulta_pasa_aunque_la_seleccion_este_obsoleta()
    {
        var resultado = await Behavior<ConsultaDePrueba, Result>(SeleccionNoVigente.CambiadaEnOtraPestana)
            .Handle(new ConsultaDePrueba(), _ => Task.FromResult(Result.Exito()), default);

        resultado.EsExitoso.Should().BeTrue();
        _registro.Rechazos.Should().BeEmpty();
    }

    [Fact]
    public void Va_en_el_pipeline_antes_de_autorizar_la_escritura_y_fuera_de_Web_no_rechaza_nada()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();

        var behaviors = servicios.Where(d => d.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(d => d.ImplementationType).ToList();
        var guarda = behaviors.IndexOf(typeof(GuardaPestanaObsoletaBehavior<,>));
        guarda.Should().BeGreaterThan(behaviors.IndexOf(typeof(ConcurrenciaBehavior<,>)));
        guarda.Should().Be(behaviors.IndexOf(typeof(AutorizacionEscrituraBehavior<,>)) - 1);

        servicios.Single(d => d.ServiceType == typeof(IGuardaSeleccionVigente)).ImplementationType
            .Should().Be(typeof(SinGuardaDeSeleccion));
        new SinGuardaDeSeleccion().Comprobar().Should().BeNull();
    }

    private GuardaPestanaObsoletaBehavior<TRequest, TResponse> Behavior<TRequest, TResponse>(SeleccionNoVigente? motivo)
        where TRequest : notnull =>
        new(new GuardaFija(motivo), _registro);

    public sealed record ComandoDePrueba : ICommand;

    private sealed record ComandoTipado : ICommand<int>;

    private sealed record ConsultaDePrueba : IRequest<Result>;

    private sealed class GuardaFija(SeleccionNoVigente? motivo) : IGuardaSeleccionVigente
    {
        public SeleccionNoVigente? Comprobar() => motivo;
    }

    private sealed class RegistroFalso : IRegistroCambioContexto
    {
        public List<(string Comando, SeleccionNoVigente Motivo)> Rechazos { get; } = [];

        public Task RegistrarCambioAsync(Guid tenantDestinoId, Guid tenantOrigenActorId, Guid? asignacionOperacionId,
            string origen, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RegistrarRechazoAsync(string comando, SeleccionNoVigente motivo, CancellationToken cancellationToken)
        {
            Rechazos.Add((comando, motivo));
            return Task.CompletedTask;
        }
    }
}
