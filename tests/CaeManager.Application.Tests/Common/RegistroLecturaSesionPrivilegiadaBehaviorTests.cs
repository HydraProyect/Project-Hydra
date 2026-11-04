using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Issue #2001: un request marcado <see cref="ILecturaRegistradaEnSesionPrivilegiada"/> bajo una
/// Sesión Privilegiada registra la lectura antes de que corra el handler, y si no queda registrada
/// el handler no corre (fallo cerrado). La función y el rol se prueban bajo runtime en
/// <c>RegistroLecturaSesionPrivilegiadaBajoRuntimeTests</c>.
/// </summary>
public class RegistroLecturaSesionPrivilegiadaBehaviorTests
{
    private sealed record LecturaMarcada : IRequest<string>, ILecturaRegistradaEnSesionPrivilegiada;
    private sealed record LecturaSinMarcador : IRequest<string>;

    private static readonly SesionPrivilegiadaActiva Sesion =
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CapacidadPrivilegio.Aprovisionamiento, null);

    [Fact]
    public async Task Un_request_sin_marcador_no_registra_ni_consulta_la_sesion()
    {
        var sesion = new SesionContada(Sesion);
        var registro = new RegistroFalso(resultado: true);
        var behavior = new RegistroLecturaSesionPrivilegiadaBehavior<LecturaSinMarcador, string>(sesion, registro);

        var resultado = await behavior.Handle(new LecturaSinMarcador(), _ => Task.FromResult("ok"), CancellationToken.None);

        resultado.Should().Be("ok");
        sesion.Consultas.Should().Be(0);
        registro.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_request_marcado_sin_Sesion_Privilegiada_no_registra_y_sigue()
    {
        var registro = new RegistroFalso(resultado: false);
        var behavior = new RegistroLecturaSesionPrivilegiadaBehavior<LecturaMarcada, string>(new SesionContada(null), registro);

        var resultado = await behavior.Handle(new LecturaMarcada(), _ => Task.FromResult("ok"), CancellationToken.None);

        resultado.Should().Be("ok", "fuera de una Sesión Privilegiada no hay nada que registrar");
        registro.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Bajo_Sesion_Privilegiada_registra_la_sesion_y_la_operacion_antes_de_leer()
    {
        var registro = new RegistroFalso(resultado: true);
        var behavior = new RegistroLecturaSesionPrivilegiadaBehavior<LecturaMarcada, string>(new SesionContada(Sesion), registro);
        var llamadasAlLeer = -1;

        var resultado = await behavior.Handle(new LecturaMarcada(), _ =>
        {
            llamadasAlLeer = registro.Llamadas.Count;
            return Task.FromResult("datos");
        }, CancellationToken.None);

        resultado.Should().Be("datos");
        llamadasAlLeer.Should().Be(1, "la fila se escribe antes de que el handler lea");
        registro.Llamadas.Should().ContainSingle().Which.Should().Be((Sesion.SesionId, nameof(LecturaMarcada)));
    }

    [Fact]
    public async Task Si_el_registro_no_se_escribe_el_handler_no_corre_y_lanza()
    {
        var behavior = new RegistroLecturaSesionPrivilegiadaBehavior<LecturaMarcada, string>(
            new SesionContada(Sesion), new RegistroFalso(resultado: false));
        var leyo = false;

        var ejecutar = () => behavior.Handle(new LecturaMarcada(), _ =>
        {
            leyo = true;
            return Task.FromResult("datos");
        }, CancellationToken.None);

        (await ejecutar.Should().ThrowAsync<LecturaPrivilegiadaNoRegistradaException>())
            .Which.Operacion.Should().Be(nameof(LecturaMarcada));
        leyo.Should().BeFalse("lo que no deja rastro no se sirve");
    }

    [Fact]
    public async Task El_valor_por_defecto_del_registro_dice_no_registrado()
    {
        (await new RegistroLecturaSesionPrivilegiadaNoDisponible().RegistrarAsync(Guid.NewGuid(), "x"))
            .Should().BeFalse("un host sin Infrastructure real no puede servir una lectura privilegiada sin rastro");
    }

    [Fact]
    public void El_behavior_va_justo_antes_de_la_elevacion_y_despues_de_la_validacion()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();

        var orden = servicios
            .Where(d => d.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(d => d.ImplementationType)
            .ToList();

        var registro = orden.IndexOf(typeof(RegistroLecturaSesionPrivilegiadaBehavior<,>));
        registro.Should().BeGreaterThan(orden.IndexOf(typeof(ValidationBehavior<,>)));
        registro.Should().BeGreaterThan(orden.IndexOf(typeof(AutorizacionEscrituraBehavior<,>)));
        orden.IndexOf(typeof(ElevacionEscrituraAprovisionamientoBehavior<,>)).Should().Be(registro + 1,
            "la función solo la ejecuta cae_app_soporte: dentro del ámbito de elevación la conexión ya no lo es");
    }

    private sealed class SesionContada(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public int Consultas { get; private set; }

        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default)
        {
            Consultas++;
            return Task.FromResult(sesion);
        }

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default)
        {
            Consultas++;
            return Task.FromResult(sesion);
        }
    }

    private sealed class RegistroFalso(bool resultado) : IRegistroLecturaSesionPrivilegiada
    {
        public List<(Guid SesionId, string Operacion)> Llamadas { get; } = [];

        public Task<bool> RegistrarAsync(Guid sesionPrivilegiadaId, string operacion, CancellationToken cancellationToken = default)
        {
            Llamadas.Add((sesionPrivilegiadaId, operacion));
            return Task.FromResult(resultado);
        }
    }
}
