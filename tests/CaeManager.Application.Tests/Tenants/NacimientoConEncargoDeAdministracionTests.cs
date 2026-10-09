using CaeManager.Application.Tenants.Commands.CrearTenantPropietarioDeOperadorCaeExterno;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Un Tenant propietario aprovisionado para un Operador CAE externo puede nacer con el Encargo de
/// administración ya registrado (decisión D-8, 2026-10-08): lo registra la administración de
/// plataforma en el mismo guardado que la operación delegada a la que se liga. Sin cláusula no nace
/// ningún encargo: el alta de siempre no cambia.
/// </summary>
public class NacimientoConEncargoDeAdministracionTests
{
    private const string Clausula = "Cláusula 7.2 del contrato de servicios";
    private const string NombreNuevo = "Laboratorios Dexter";

    private readonly Guid _actor = Guid.NewGuid();
    private readonly Tenant _operador = new("Planet Express Prevención", PerfilVocabularioTenant.ClienteDirecto);
    private readonly TenantsQueryContextFalso _tenants = new();
    private readonly TenantRepositoryFalso _tenantRepositorio = new();
    private readonly EncargosFalsos _encargos = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly AsignacionesOperativasWriterFalso _writer = new();

    public NacimientoConEncargoDeAdministracionTests()
    {
        _operador.HabilitarComoOperadorCaeExterno();
        _tenants.ListaTenants.Add(_operador);
    }

    [Fact]
    public async Task Con_clausula_el_Tenant_propietario_nace_con_el_encargo_registrado_por_la_plataforma()
    {
        var resultado = await Manejador().Handle(Alta($"  {Clausula}  "), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        var encargo = _encargos.Agregados.Should().ContainSingle().Which;
        encargo.PropietarioTenantId.Should().Be(resultado.Valor, "el encargo es del Tenant propietario recién creado");
        encargo.OperadorTenantId.Should().Be(_operador.Id);
        encargo.Origen.Should().Be(OrigenEncargoAdministracion.AprovisionamientoDePlataforma);
        encargo.RegistradoPorUsuarioId.Should().Be(_actor, "queda el Actor real que lo aprovisionó");
        encargo.ClausulaContrato.Should().Be(Clausula, "la cláusula se guarda sin los espacios de los extremos");
        encargo.VersionTexto.Should().Be(EncargoAdministracion.VersionTextoVigente);
        encargo.VigenciaHasta.Should().BeNull();
        encargo.RetiradoEnUtc.Should().BeNull();
        encargo.AsignacionOperacionId.Should().NotBeEmpty("se liga a la operación delegada que se acaba de abrir");
        _writer.OperacionesAbiertas.Should().ContainSingle().Which.Should().Be((resultado.Valor, _operador.Id));
        _unitOfWork.VecesGuardado.Should().Be(1, "Tenant, operación y encargo se confirman en un único guardado");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sin_clausula_el_alta_no_registra_ningun_encargo(string? clausula)
    {
        var resultado = await Manejador().Handle(Alta(clausula), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        _encargos.Agregados.Should().BeEmpty("en blanco es «sin encargo», no una cláusula vacía");
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Una_clausula_demasiado_larga_no_crea_ni_el_Tenant_ni_el_encargo()
    {
        var resultado = await Manejador().Handle(
            Alta(new string('x', EncargoAdministracion.LongitudMaximaClausula + 1)), default);

        resultado.Error.Should().Be(ErroresEncargoAdministracion.ClausulaDemasiadoLarga);
        _encargos.Agregados.Should().BeEmpty();
        _tenantRepositorio.Agregados.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Quien_no_es_administracion_de_plataforma_no_hace_nacer_ningun_encargo()
    {
        var resultado = await Manejador(AutorizacionAdminPlataformaFalsa.SinNada()).Handle(Alta(Clausula), default);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("TenantPropietarioDeOperador.SinPermiso");
        _encargos.Agregados.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    private CrearTenantPropietarioDeOperadorCaeExternoCommand Alta(string? clausula) =>
        new(_operador.Id, NombreNuevo, clausula);

    private CrearTenantPropietarioDeOperadorCaeExternoCommandHandler Manejador(
        AutorizacionAdminPlataformaFalsa? autorizacion = null) =>
        new(_tenantRepositorio, _tenants, new DelegacionTenantRepositorioFalso(), new ParametrosFalsos(),
            autorizacion ?? AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(_actor),
            _writer, _unitOfWork, _encargos);

    private sealed class TenantRepositoryFalso : ITenantRepository
    {
        public List<Tenant> Agregados { get; } = [];

        public Task<bool> ExisteConNombreAsync(string nombre, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Tenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Agregados.SingleOrDefault(t => t.Id == id));

        public void Agregar(Tenant nuevo) => Agregados.Add(nuevo);
    }

    private sealed class ParametrosFalsos : IParametroSistemaRepository
    {
        public Task<ParametroSistema> ObtenerAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Agregar(ParametroSistema parametro)
        {
        }
    }

    /// <summary>Solo recoge lo que el alta añade: el alta no lee encargos ni guarda por esta vía.</summary>
    private sealed class EncargosFalsos : IEncargoAdministracionRepository
    {
        public List<EncargoAdministracion> Agregados { get; } = [];

        public void Agregar(EncargoAdministracion encargo) => Agregados.Add(encargo);

        public Task<EncargoAdministracion?> ObtenerPorIdAsync(
            Guid id, Guid propietarioTenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AsignacionOperacion?> ObtenerOperacionAsync(
            Guid asignacionOperacionId, Guid propietarioTenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AsignacionOperacion>> ListarOperacionesEncargablesAsync(
            Guid propietarioTenantId, DateTime ahora, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExisteSinRetirarAsync(Guid asignacionOperacionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> GuardarDetectandoCarreraAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
