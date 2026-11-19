using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Commands.MarcarParticipanteTec;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// D3: <see cref="MarcarTenantComoParticipanteTecCommand"/>, el único escritor de <c>Tenants.EsParticipanteTec</c>. Solo
/// una Sesión Privilegiada de Aprovisionamiento sobre el Tenant activo, sin impersonación, lo pone; cualquier otra
/// combinación se deniega sin leer ni guardar. Que la base solo deje escribir la columna a
/// <c>cae_app_aprovisionamiento</c> lo prueba <c>LimiteTenantTecBajoRuntimeTests</c> en integración.
/// </summary>
public class MarcarTenantComoParticipanteTecCommandTests
{
    private readonly Tenant _tenant = new("Tenant TEC de prueba");
    private readonly UnitOfWorkFalso _unidad = new();

    private static SesionPrivilegiadaActiva Sesion(CapacidadPrivilegio capacidad, Guid tenantObjetivo) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tenantObjetivo, capacidad, null);

    private Task<Result> Ejecutar(SesionPrivilegiadaActiva? sesion, Guid? tenantActivo, TenantsFalsos? tenants = null) =>
        new MarcarTenantComoParticipanteTecCommandHandler(
                new TenantActivo(tenantActivo), new SesionFija(sesion), tenants ?? new TenantsFalsos(_tenant), _unidad)
            .Handle(new MarcarTenantComoParticipanteTecCommand(), CancellationToken.None);

    [Fact]
    public async Task Con_la_sesion_de_Aprovisionamiento_sobre_el_Tenant_activo_lo_marca()
    {
        var resultado = await Ejecutar(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id), _tenant.Id);

        resultado.EsExitoso.Should().BeTrue();
        _tenant.EsParticipanteTec.Should().BeTrue();
        _unidad.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Marcar_un_Tenant_ya_marcado_no_escribe()
    {
        _tenant.MarcarComoParticipanteTec();

        var resultado = await Ejecutar(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id), _tenant.Id);

        resultado.EsExitoso.Should().BeTrue();
        _unidad.VecesGuardado.Should().Be(0);
    }

    public static TheoryData<CapacidadPrivilegio> OtrasCapacidades()
    {
        var datos = new TheoryData<CapacidadPrivilegio>();
        foreach (var capacidad in Enum.GetValues<CapacidadPrivilegio>().Where(c => c != CapacidadPrivilegio.Aprovisionamiento))
            datos.Add(capacidad);
        return datos;
    }

    [Theory]
    [MemberData(nameof(OtrasCapacidades))]
    public async Task Otra_capacidad_no_marca(CapacidadPrivilegio capacidad)
    {
        await DebeDenegar(Sesion(capacidad, _tenant.Id), _tenant.Id);
    }

    [Fact]
    public async Task Sin_Sesion_Privilegiada_no_marca_aunque_el_rol_pueda_escribir()
    {
        await DebeDenegar(null, _tenant.Id);
    }

    [Fact]
    public async Task La_sesion_sobre_otro_Tenant_no_marca_el_activo()
    {
        await DebeDenegar(Sesion(CapacidadPrivilegio.Aprovisionamiento, Guid.NewGuid()), _tenant.Id);
    }

    [Fact]
    public async Task Bajo_impersonacion_no_marca()
    {
        await DebeDenegar(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id) with { UsuarioSimuladoId = Guid.NewGuid() },
            _tenant.Id);
    }

    [Fact]
    public async Task Sin_Tenant_activo_no_marca()
    {
        await DebeDenegar(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id), null);
    }

    [Fact]
    public async Task Un_Tenant_que_ya_no_esta_no_se_marca()
    {
        var inexistente = Guid.NewGuid();
        var tenants = new TenantsFalsos(_tenant);

        var resultado = await Ejecutar(Sesion(CapacidadPrivilegio.Aprovisionamiento, inexistente), inexistente, tenants);

        resultado.Error!.Codigo.Should().Be(ErroresMarcarParticipanteTec.NoAutorizado.Codigo);
        _unidad.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Si_la_fila_cargada_no_es_la_del_Tenant_de_la_elevacion_no_se_marca()
    {
        // Tenants no tiene RLS y el GRANT por columna no limita la fila: si la lectura devolviera otro Tenant que el de
        // la Sesión Privilegiada, el handler no lo marca. Una coordenada de contexto no es autoridad.
        var otro = new Tenant("Otro Tenant de prueba");
        var tenants = new TenantsFalsos(otro, devolverSiempre: true);

        var resultado = await Ejecutar(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id), _tenant.Id, tenants);

        resultado.Error!.Codigo.Should().Be(ErroresMarcarParticipanteTec.NoAutorizado.Codigo);
        otro.EsParticipanteTec.Should().BeFalse();
        _unidad.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void Es_un_comando_de_aprovisionamiento()
    {
        typeof(IComandoDeAprovisionamiento).IsAssignableFrom(typeof(MarcarTenantComoParticipanteTecCommand)).Should().BeTrue(
            "solo cae_app_aprovisionamiento puede actualizar la columna EsParticipanteTec");
    }

    private async Task DebeDenegar(SesionPrivilegiadaActiva? sesion, Guid? tenantActivo)
    {
        var tenants = new TenantsFalsos(_tenant);
        var resultado = await Ejecutar(sesion, tenantActivo, tenants);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be(ErroresMarcarParticipanteTec.NoAutorizado.Codigo);
        tenants.Lecturas.Should().Be(0, "la autoridad va antes que cualquier lectura");
        _tenant.EsParticipanteTec.Should().BeFalse();
        _unidad.VecesGuardado.Should().Be(0);
    }

    private sealed class TenantActivo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class SesionFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        // El handler corre bajo cae_app_aprovisionamiento, que no lee SesionesPrivilegiadas: no revalida.
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("El handler no revalida: la conexión elevada no lee SesionesPrivilegiadas.");
    }

    private sealed class TenantsFalsos(Tenant tenant, bool devolverSiempre = false) : ITenantRepository
    {
        public int Lecturas { get; private set; }

        public Task<bool> ExisteConNombreAsync(string nombre, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Tenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Lecturas++;
            return Task.FromResult(devolverSiempre || id == tenant.Id ? tenant : null);
        }

        public void Agregar(Tenant nuevo) => throw new NotSupportedException();
    }
}
