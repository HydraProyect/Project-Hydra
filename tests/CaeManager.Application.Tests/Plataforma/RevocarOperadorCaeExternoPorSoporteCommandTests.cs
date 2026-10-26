using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Commands.RevocarOperadorCaeExternoPorSoporte;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// La vía de Soporte TALVEG para revocar la Delegación de un Operador CAE externo a petición del
/// Tenant propietario (enmienda de ADR-004 § 12.2 del 2026-10-26). Aquí, las reglas de
/// Application: sesión, capacidad, simulación, Tenant objetivo y motivo. Que la base no dependa
/// de ellas (rol de solo lectura, función que lo repite todo, auditoría) lo prueba
/// <c>SoporteRevocaOperadorCaeExternoBajoRuntimeTests</c>.
/// </summary>
public class RevocarOperadorCaeExternoPorSoporteCommandTests
{
    private const string Motivo = "Petición del Administrador del Tenant propietario, ticket SOP-1234";

    private readonly Guid _tenantPropietario = Guid.NewGuid();
    private readonly Guid _delegacion = Guid.NewGuid();
    private readonly RevocacionFalsa _puerta = new();

    [Fact]
    public async Task Con_la_sesion_de_la_capacidad_en_su_Tenant_objetivo_revoca_por_la_puerta_de_la_base()
    {
        var sesion = Sesion(CapacidadPrivilegio.RevocacionOperadorCaeExterno);

        var resultado = await Handler(sesion, _tenantPropietario)
            .Handle(new RevocarOperadorCaeExternoPorSoporteCommand(_delegacion, "  " + Motivo + "  "), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _puerta.Llamadas.Should().ContainSingle()
            .Which.Should().Be((sesion.SesionId, _delegacion, Motivo), "la sesión que ampara el acto viaja a la base con el motivo");
    }

    [Fact]
    public async Task Sin_Sesion_Privilegiada_falla_aunque_el_rol_escriba()
    {
        // Es el caso del Administrador del Tenant propietario o de cualquier usuario de negocio:
        // AutorizacionEscrituraBehavior deja pasar su rol de escritura, y este handler no.
        var resultado = await Handler(null, _tenantPropietario)
            .Handle(new RevocarOperadorCaeExternoPorSoporteCommand(_delegacion, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("DelegacionTenant.SoloSoporteConSesionPrivilegiada");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento)]
    [InlineData(CapacidadPrivilegio.RestablecimientoSegundoFactor)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    public async Task Una_sesion_de_otra_capacidad_falla(CapacidadPrivilegio capacidad)
    {
        var resultado = await Handler(Sesion(capacidad), _tenantPropietario)
            .Handle(new RevocarOperadorCaeExternoPorSoporteCommand(_delegacion, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("DelegacionTenant.SesionSinCapacidad");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Simulando_a_otro_usuario_falla()
    {
        var sesion = Sesion(CapacidadPrivilegio.RevocacionOperadorCaeExterno) with { UsuarioSimuladoId = Guid.NewGuid() };

        var resultado = await Handler(sesion, _tenantPropietario)
            .Handle(new RevocarOperadorCaeExternoPorSoporteCommand(_delegacion, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("DelegacionTenant.ActorNoTitular");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_sesion_sobre_otro_Tenant_falla()
    {
        var resultado = await Handler(Sesion(CapacidadPrivilegio.RevocacionOperadorCaeExterno), Guid.NewGuid())
            .Handle(new RevocarOperadorCaeExternoPorSoporteCommand(_delegacion, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("DelegacionTenant.FueraDelTenantObjetivo");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Lo_que_niega_la_base_llega_tal_cual()
    {
        _puerta.Respuesta = Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación."));

        var resultado = await Handler(Sesion(CapacidadPrivilegio.RevocacionOperadorCaeExterno), _tenantPropietario)
            .Handle(new RevocarOperadorCaeExternoPorSoporteCommand(_delegacion, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Sin_motivo_no_valida(string? motivo)
    {
        new RevocarOperadorCaeExternoPorSoporteCommandValidator()
            .Validate(new RevocarOperadorCaeExternoPorSoporteCommand(_delegacion, motivo!))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Un_motivo_de_mas_de_500_caracteres_no_valida()
    {
        new RevocarOperadorCaeExternoPorSoporteCommandValidator()
            .Validate(new RevocarOperadorCaeExternoPorSoporteCommand(_delegacion, new string('x', 501)))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Sin_delegacion_no_valida()
    {
        new RevocarOperadorCaeExternoPorSoporteCommandValidator()
            .Validate(new RevocarOperadorCaeExternoPorSoporteCommand(Guid.Empty, Motivo))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Es_el_comando_marcado_de_la_capacidad_y_un_comando()
    {
        var comando = new RevocarOperadorCaeExternoPorSoporteCommand(_delegacion, Motivo);

        comando.Should().BeAssignableTo<IComandoDeRevocacionOperadorCaeExterno>();
        comando.Should().BeAssignableTo<ICommand>();
        comando.Should().NotBeAssignableTo<IComandoDeAprovisionamiento>();
        comando.Should().NotBeAssignableTo<IComandoDeRestablecimientoSegundoFactor>();
    }

    private SesionPrivilegiadaActiva Sesion(CapacidadPrivilegio capacidad) =>
        new(Guid.NewGuid(), Guid.NewGuid(), _tenantPropietario, capacidad, null);

    private RevocarOperadorCaeExternoPorSoporteCommandHandler Handler(SesionPrivilegiadaActiva? sesion, Guid? tenantActivo) =>
        new(new SesionFalsa(sesion), new TenantActualFalso(tenantActivo), _puerta);

    private sealed class RevocacionFalsa : IRevocacionOperadorCaeExternoPorSoporte
    {
        public List<(Guid Sesion, Guid Delegacion, string Motivo)> Llamadas { get; } = [];

        public Result Respuesta { get; set; } = Result.Exito();

        public Task<Result> RevocarAsync(
            Guid sesionPrivilegiadaId, Guid delegacionTenantId, string motivo, CancellationToken cancellationToken)
        {
            Llamadas.Add((sesionPrivilegiadaId, delegacionTenantId, motivo));
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
