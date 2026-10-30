using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Commands.RevocarConcesionPrivilegio;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.Application.Tests.Plataforma;

public class RevocarConcesionPrivilegioCommandTests
{
    [Fact]
    public void El_validador_exige_identificador() =>
        new RevocarConcesionPrivilegioCommandValidator().Validate(new RevocarConcesionPrivilegioCommand(Guid.Empty))
            .IsValid.Should().BeFalse();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_beneficiario_o_el_concedente_revoca_sin_Tenant_objetivo(bool beneficiario)
    {
        var contexto = new Contexto();
        var unidad = new Unidad();
        var handler = Handler(contexto, unidad, beneficiario ? contexto.Beneficiario : contexto.Concedente);

        var resultado = await handler.Handle(new(contexto.Concesion.Id), default);

        resultado.EsExitoso.Should().BeTrue();
        contexto.Concesion.Estado.Should().Be(EstadoConcesionPrivilegio.Revocada);
        unidad.Llamadas.Should().Be(1);
    }

    [Fact]
    public async Task Un_tercero_no_puede_revocar_aunque_la_fila_sea_visible()
    {
        var contexto = new Contexto();
        var unidad = new Unidad();
        var resultado = await Handler(contexto, unidad, Guid.NewGuid()).Handle(new(contexto.Concesion.Id), default);

        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.RevocacionNoPermitida");
        contexto.Concesion.Estado.Should().Be(EstadoConcesionPrivilegio.Vigente);
        unidad.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Sin_usuario_resuelto_no_se_revoca()
    {
        var contexto = new Contexto();
        var unidad = new Unidad();
        var handler = new RevocarConcesionPrivilegioCommandHandler(contexto, unidad, new CurrentUserServiceFalso());

        var resultado = await handler.Handle(new(contexto.Concesion.Id), default);

        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.RevocacionNoPermitida");
        contexto.Concesion.Estado.Should().Be(EstadoConcesionPrivilegio.Vigente);
        unidad.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Sin_segundo_factor_activo_no_se_revoca()
    {
        var contexto = new Contexto();
        var unidad = new Unidad();
        var handler = new RevocarConcesionPrivilegioCommandHandler(contexto, unidad,
            new CurrentUserServiceFalso(contexto.Beneficiario, tieneDobleFactorActivo: false));

        var resultado = await handler.Handle(new(contexto.Concesion.Id), default);

        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.SinDobleFactor");
        contexto.Concesion.Estado.Should().Be(EstadoConcesionPrivilegio.Vigente);
        unidad.Llamadas.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdminPlataforma_no_se_revoca_ni_como_titular_ni_como_concedente(bool beneficiario)
    {
        var ahora = DateTime.UtcNow;
        var titular = Guid.NewGuid();
        var concedente = Guid.NewGuid();
        var contexto = new Contexto(ConcesionPrivilegio.Global(titular, ahora.AddDays(-2), ahora.AddDays(1), concedente));
        var unidad = new Unidad();

        var resultado = await Handler(contexto, unidad, beneficiario ? titular : concedente)
            .Handle(new(contexto.Concesion.Id), default);

        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.AdminPlataformaNoRevocable");
        contexto.Concesion.Estado.Should().Be(EstadoConcesionPrivilegio.Vigente);
        unidad.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task La_identidad_raiz_no_revoca_la_concesion_fundacional()
    {
        var raiz = Guid.NewGuid();
        var contexto = new Contexto(ConcesionPrivilegio.RaizDeBootstrap(raiz, DateTime.UtcNow.AddDays(-2), vigenciaHasta: null));
        var unidad = new Unidad();

        var resultado = await Handler(contexto, unidad, raiz).Handle(new(contexto.Concesion.Id), default);

        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.AdminPlataformaNoRevocable");
        contexto.Concesion.Estado.Should().Be(EstadoConcesionPrivilegio.Vigente);
        unidad.Llamadas.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_concesion_ya_cerrada_no_se_revoca_de_nuevo(bool expirada)
    {
        var contexto = new Contexto();
        if (expirada) contexto.Concesion.MarcarExpirada(DateTime.UtcNow);
        else contexto.Concesion.Revocar(DateTime.UtcNow);
        var estado = contexto.Concesion.Estado;
        var unidad = new Unidad();

        var resultado = await Handler(contexto, unidad, contexto.Concedente).Handle(new(contexto.Concesion.Id), default);

        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.YaCerrada");
        contexto.Concesion.Estado.Should().Be(estado);
        unidad.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Una_concesion_cuya_fecha_ya_paso_se_revoca_como_define_la_entidad()
    {
        var contexto = new Contexto(vencida: true);
        var fin = contexto.Concesion.VigenciaHasta;
        var resultado = await Handler(contexto, new Unidad(), contexto.Beneficiario).Handle(new(contexto.Concesion.Id), default);
        resultado.EsExitoso.Should().BeTrue();
        contexto.Concesion.Estado.Should().Be(EstadoConcesionPrivilegio.Revocada);
        contexto.Concesion.VigenciaHasta.Should().Be(fin);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task Un_guardado_sin_fila_o_con_conflicto_no_confirma_la_revocacion(int filas, bool concurrencia)
    {
        var contexto = new Contexto();
        var resultado = await Handler(contexto, new Unidad(filas, concurrencia), contexto.Beneficiario)
            .Handle(new(contexto.Concesion.Id), default);
        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.RevocacionNoPermitida");
    }

    // ── La marca: el comando real por AutorizacionEscrituraBehavior ─────────

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura, "Autorizacion.SesionPrivilegiadaSoloLectura")]
    [InlineData(CapacidadPrivilegio.AdminPlataforma, "Autorizacion.SesionPrivilegiadaSoloLectura")]
    [InlineData(CapacidadPrivilegio.Impersonacion, "Autorizacion.SesionPrivilegiadaSoloLectura")]
    [InlineData(CapacidadPrivilegio.BreakGlass, "Autorizacion.BreakGlassSinCaminoDeEscritura")]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento, "Autorizacion.ComandoFueraDelAprovisionamiento")]
    [InlineData(CapacidadPrivilegio.RestablecimientoSegundoFactor, "Autorizacion.ComandoFueraDelRestablecimiento")]
    [InlineData(CapacidadPrivilegio.RevocacionOperadorCaeExterno, "Autorizacion.ComandoFueraDeLaRevocacion")]
    [InlineData(CapacidadPrivilegio.ConcesionAdministrador, "Autorizacion.ComandoFueraDeLaConcesionAdministrador")]
    public async Task Dentro_de_una_Sesion_Privilegiada_el_comando_se_rechaza_con_cualquier_capacidad(
        CapacidadPrivilegio capacidad, string codigo)
    {
        var sesion = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), capacidad, null);

        var (resultado, handlerLlamado) = await PorElBehaviorAsync("Administrador", sesion);

        resultado.Error.Codigo.Should().Be(codigo);
        handlerLlamado.Should().BeFalse();
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Sin_rol_de_escritura_el_comando_se_rechaza_porque_no_es_autoservicio(string? rol)
    {
        var (resultado, handlerLlamado) = await PorElBehaviorAsync(rol, sesion: null);

        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        handlerLlamado.Should().BeFalse();
    }

    private static async Task<(Result Resultado, bool HandlerLlamado)> PorElBehaviorAsync(
        string? rol, SesionPrivilegiadaActiva? sesion)
    {
        var behavior = new AutorizacionEscrituraBehavior<RevocarConcesionPrivilegioCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol),
            new SesionFija(sesion),
            new SinTenant());

        var handlerLlamado = false;
        var resultado = await behavior.Handle(new(Guid.NewGuid()), _ =>
        {
            handlerLlamado = true;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);
        return (resultado, handlerLlamado);
    }

    private static RevocarConcesionPrivilegioCommandHandler Handler(Contexto contexto, Unidad unidad, Guid usuario) =>
        new(contexto, unidad, new CurrentUserServiceFalso(usuario));

    private sealed class SesionFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private sealed class Unidad(int filas = 1, bool concurrencia = false) : IUnitOfWork
    {
        public int Llamadas { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Llamadas++;
            if (concurrencia) throw new DbUpdateConcurrencyException();
            return Task.FromResult(filas);
        }
    }

    private sealed class Contexto : IPlataformaQueryContext
    {
        public Guid Beneficiario { get; } = Guid.NewGuid();
        public Guid Concedente { get; } = Guid.NewGuid();
        public ConcesionPrivilegio Concesion { get; }

        public Contexto(bool vencida = false)
        {
            var ahora = DateTime.UtcNow;
            Concesion = ConcesionPrivilegio.SoporteLecturaGlobal(Beneficiario, ahora.AddDays(-2),
                vencida ? ahora.AddDays(-1) : ahora.AddDays(1), Concedente);
        }

        public Contexto(ConcesionPrivilegio concesion) => Concesion = concesion;

        public IQueryable<ConcesionPrivilegio> ConcesionesPrivilegio => Consulta<ConcesionPrivilegio>([Concesion]);
        public IQueryable<SesionPrivilegiada> SesionesPrivilegiadas => Consulta<SesionPrivilegiada>([]);
        public IQueryable<EstadoBootstrapPlataforma> EstadoBootstrapPlataforma => Consulta<EstadoBootstrapPlataforma>([]);
        public IQueryable<TenantAlcanzadoPorConcesion> TenantsAlcanzadosPorConcesion => Consulta<TenantAlcanzadoPorConcesion>([]);
        private static IQueryable<T> Consulta<T>(T[] elementos) => new TestAsyncQueryable<T>(elementos.AsQueryable());
    }
}
