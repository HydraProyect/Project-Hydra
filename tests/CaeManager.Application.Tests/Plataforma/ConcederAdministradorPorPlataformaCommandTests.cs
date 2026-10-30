using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Commands.ConcederAdministradorPorPlataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// La vía de un Actor de Plataforma TALVEG para conceder Administrador en un Tenant ya existente
/// (decisión del 2026-10-30). Aquí, las reglas de Application: sesión, capacidad, simulación,
/// Tenant objetivo y motivo. Que la base no dependa de ellas lo prueba
/// <c>PlataformaConcedeAdministradorBajoRuntimeTests</c>.
/// </summary>
public class ConcederAdministradorPorPlataformaCommandTests
{
    private const string Motivo = "Petición del Tenant, ticket SOP-1234";

    private readonly Guid _tenantObjetivo = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly ConcesionFalsa _puerta = new();

    [Fact]
    public async Task Con_la_sesion_de_la_capacidad_en_su_Tenant_objetivo_concede_por_la_puerta_de_la_base()
    {
        var sesion = Sesion(CapacidadPrivilegio.ConcesionAdministrador);

        var resultado = await Handler(sesion, _tenantObjetivo)
            .Handle(new ConcederAdministradorPorPlataformaCommand(_usuario, "  " + Motivo + "  "), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _puerta.Llamadas.Should().ContainSingle()
            .Which.Should().Be((sesion.SesionId, _usuario, Motivo), "la sesión que ampara el acto viaja a la base con el motivo");
    }

    [Fact]
    public async Task Sin_Sesion_Privilegiada_falla_aunque_el_rol_escriba()
    {
        // Es el caso de un Administrador del Tenant o de cualquier usuario de negocio:
        // AutorizacionEscrituraBehavior deja pasar su rol de escritura, y este handler no.
        var resultado = await Handler(null, _tenantObjetivo)
            .Handle(new ConcederAdministradorPorPlataformaCommand(_usuario, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("Usuarios.SoloPlataformaConSesionPrivilegiada");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento)]
    [InlineData(CapacidadPrivilegio.RestablecimientoSegundoFactor)]
    [InlineData(CapacidadPrivilegio.RevocacionOperadorCaeExterno)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    public async Task Una_sesion_de_otra_capacidad_falla(CapacidadPrivilegio capacidad)
    {
        var resultado = await Handler(Sesion(capacidad), _tenantObjetivo)
            .Handle(new ConcederAdministradorPorPlataformaCommand(_usuario, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("Usuarios.SesionSinCapacidad");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Simulando_a_otro_usuario_falla()
    {
        var sesion = Sesion(CapacidadPrivilegio.ConcesionAdministrador) with { UsuarioSimuladoId = Guid.NewGuid() };

        var resultado = await Handler(sesion, _tenantObjetivo)
            .Handle(new ConcederAdministradorPorPlataformaCommand(_usuario, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("Usuarios.ActorNoTitular");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_sesion_sobre_otro_Tenant_falla()
    {
        var resultado = await Handler(Sesion(CapacidadPrivilegio.ConcesionAdministrador), Guid.NewGuid())
            .Handle(new ConcederAdministradorPorPlataformaCommand(_usuario, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("Usuarios.FueraDelTenantObjetivo");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Lo_que_niega_la_base_llega_tal_cual()
    {
        _puerta.Respuesta = Result.Fallo(Error.Crear("Usuarios.NoEncontrado", "No encontramos esa cuenta en esta organización."));

        var resultado = await Handler(Sesion(CapacidadPrivilegio.ConcesionAdministrador), _tenantObjetivo)
            .Handle(new ConcederAdministradorPorPlataformaCommand(_usuario, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("Usuarios.NoEncontrado");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Sin_motivo_no_valida(string? motivo)
    {
        new ConcederAdministradorPorPlataformaCommandValidator()
            .Validate(new ConcederAdministradorPorPlataformaCommand(_usuario, motivo!))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Un_motivo_de_mas_de_500_caracteres_no_valida()
    {
        new ConcederAdministradorPorPlataformaCommandValidator()
            .Validate(new ConcederAdministradorPorPlataformaCommand(_usuario, new string('x', 501)))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Sin_cuenta_no_valida()
    {
        new ConcederAdministradorPorPlataformaCommandValidator()
            .Validate(new ConcederAdministradorPorPlataformaCommand(Guid.Empty, Motivo))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Es_el_comando_marcado_de_la_capacidad_y_un_comando()
    {
        var comando = new ConcederAdministradorPorPlataformaCommand(_usuario, Motivo);

        comando.Should().BeAssignableTo<IComandoDeConcesionAdministrador>();
        comando.Should().BeAssignableTo<ICommand>();
        comando.Should().NotBeAssignableTo<IComandoDeAprovisionamiento>();
        comando.Should().NotBeAssignableTo<IComandoDeRestablecimientoSegundoFactor>();
        comando.Should().NotBeAssignableTo<IComandoDeRevocacionOperadorCaeExterno>();
    }

    private SesionPrivilegiadaActiva Sesion(CapacidadPrivilegio capacidad) =>
        new(Guid.NewGuid(), Guid.NewGuid(), _tenantObjetivo, capacidad, null);

    private ConcederAdministradorPorPlataformaCommandHandler Handler(SesionPrivilegiadaActiva? sesion, Guid? tenantActivo) =>
        new(new SesionFalsa(sesion), new TenantActualFalso(tenantActivo), _puerta);

    private sealed class ConcesionFalsa : IConcesionAdministradorPorPlataforma
    {
        public List<(Guid Sesion, Guid Usuario, string Motivo)> Llamadas { get; } = [];

        public Result Respuesta { get; set; } = Result.Exito();

        public Task<Result> ConcederAsync(
            Guid sesionPrivilegiadaId, Guid usuarioId, string motivo, CancellationToken cancellationToken)
        {
            Llamadas.Add((sesionPrivilegiadaId, usuarioId, motivo));
            return Task.FromResult(Respuesta);
        }
    }

    private sealed class SesionFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}
