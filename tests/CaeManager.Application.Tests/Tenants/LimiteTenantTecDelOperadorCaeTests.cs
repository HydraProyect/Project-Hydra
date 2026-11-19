using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tenants.Commands.CrearTenantPropietarioDeOperadorCaeExterno;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// D3 sobre el Operador CAE (EP-4: sin Producto Contratado P, el Tenant TEC no tiene «gestión de la CAE de terceros, ni
/// Operador CAE»; MEDIA de la ronda 3 del revisor-puente). Las tres vías que nombran a un Tenant como Operador CAE de otro
/// deciden también por el Tenant de origen de ese Operador CAE, con el mismo error que el resto de D3: con él cerrado no se
/// escribe nada; con línea P Vigente (la guarda responde abierto) la petición sigue. Qué es «cerrado» (marcador y línea) lo
/// prueba <c>OutboundCerradoTenantTecServiceTests</c>; aquí la guarda es un doble que responde por Tenant.
/// </summary>
public class LimiteTenantTecDelOperadorCaeTests
{
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly Tenant _propietario = new("Hostelería Los Pinos");
    private readonly Tenant _operador = CrearOperadorCaeExterno("Prevención Norte");
    private readonly TenantsQueryContextFalso _tenants = new();
    private readonly DelegacionTenantRepositorioFalso _vinculos = new();
    private readonly AsignacionesOperativasWriterFalso _writer = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    public LimiteTenantTecDelOperadorCaeTests() =>
        _tenants.ListaTenants.AddRange([_propietario, _operador]);

    private static Tenant CrearOperadorCaeExterno(string nombre)
    {
        var tenant = new Tenant(nombre, PerfilVocabularioTenant.Consultora);
        tenant.HabilitarComoOperadorCaeExterno();
        return tenant;
    }

    private static void DebeSerElErrorDeD3(Result resultado)
    {
        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo);
        resultado.Error.Mensaje.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Mensaje, "el mismo error en todos los puntos");
    }

    // ── CrearDelegacionTenant: el Operador CAE es TenantConsultoraId ─────────────────────────────

    private CrearDelegacionTenantCommandHandler HandlerDeDelegacion(IOutboundCerradoTenantTec outbound) =>
        new(_vinculos, _tenants, _writer, AutorizacionDelegacionFalsa.AdministradorDe(_propietario.Id),
            new CurrentUserServiceFalso(_usuario), _unitOfWork, new TransaccionDeComandoFalsa(), new AsignacionAutomaticaInerte(),
            new CaeManager.Application.Tests.Configuracion.TenantActualFijo(_propietario.Id), outbound);

    [Fact]
    public async Task Delegar_en_un_Operador_CAE_cerrado_por_D3_se_rechaza_sin_escribir()
    {
        var outbound = new OutboundCerradoTenantTecFijo(t => t == _operador.Id);

        var resultado = await HandlerDeDelegacion(outbound).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, _propietario.Id), CancellationToken.None);

        DebeSerElErrorDeD3(resultado);
        outbound.Preguntados.Should().Equal(_operador.Id);
        _vinculos.Delegaciones.Should().BeEmpty();
        _writer.OperacionesAbiertas.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Delegar_en_un_Operador_CAE_con_linea_Outbound_vigente_pasa_la_guarda()
    {
        // Pasa la guarda y llega a la comprobación siguiente: ya hay una delegación activa entre los dos.
        _vinculos.Agregar(new DelegacionTenant(_operador.Id, _propietario.Id));
        var outbound = OutboundCerradoTenantTecFijo.Abierto();

        var resultado = await HandlerDeDelegacion(outbound).Handle(
            new CrearDelegacionTenantCommand(_operador.Id, _propietario.Id), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("DelegacionTenant.YaActiva");
        outbound.Preguntados.Should().Equal(_operador.Id);
    }

    // ── CrearTenantPropietarioDeOperadorCaeExterno: el Operador CAE es TenantOperadorId ──────────

    private CrearTenantPropietarioDeOperadorCaeExternoCommandHandler HandlerDeAltaDePropietario(
        TenantsConNombre tenantsConNombre, IOutboundCerradoTenantTec outbound) =>
        new(tenantsConNombre, _tenants, _vinculos, new ParametrosFalsos(), AutorizacionAdminPlataformaFalsa.Global(),
            new CurrentUserServiceFalso(_usuario), _writer, _unitOfWork, encargos: null!, new TransaccionDeComandoFalsa(),
            new AsignacionAutomaticaInerte(), outbound);

    [Fact]
    public async Task Dar_de_alta_un_Tenant_propietario_para_un_Operador_CAE_cerrado_por_D3_se_rechaza_sin_escribir()
    {
        var tenantsConNombre = new TenantsConNombre(existe: false);
        var outbound = new OutboundCerradoTenantTecFijo(t => t == _operador.Id);

        var resultado = await HandlerDeAltaDePropietario(tenantsConNombre, outbound).Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(_operador.Id, "Construcciones Boreal", "Cláusula 3"),
            CancellationToken.None);

        DebeSerElErrorDeD3(resultado);
        outbound.Preguntados.Should().Equal(_operador.Id);
        tenantsConNombre.Agregados.Should().BeEmpty();
        _vinculos.Delegaciones.Should().BeEmpty();
        _writer.OperacionesAbiertas.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Dar_de_alta_un_Tenant_propietario_para_un_Operador_CAE_con_linea_Outbound_vigente_pasa_la_guarda()
    {
        // Pasa la guarda y llega a la comprobación siguiente: el nombre ya existe.
        var outbound = OutboundCerradoTenantTecFijo.Abierto();

        var resultado = await HandlerDeAltaDePropietario(new TenantsConNombre(existe: true), outbound).Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(_operador.Id, "Construcciones Boreal"), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("TenantPropietarioDeOperador.NombreDuplicado");
        outbound.Preguntados.Should().Equal(_operador.Id);
    }

    // ── CrearClienteDelegante: el Operador CAE es el Tenant de origen de quien ejecuta ───────────

    private CrearClienteDeleganteCommandHandler HandlerDeClienteDelegante(
        TenantsConNombre tenantsConNombre, AsignacionOperadorDelegadoRepositorioFalso asignaciones, IOutboundCerradoTenantTec outbound) =>
        new(tenantsConNombre, _vinculos, asignaciones, new ParametrosFalsos(), AutorizacionAdminPlataformaFalsa.Global(),
            new CurrentUserServiceFalso(_usuario, tenantOrigenId: _operador.Id), _writer, _unitOfWork,
            new TransaccionDeComandoFalsa(), outbound);

    [Fact]
    public async Task Dar_de_alta_un_cliente_delegante_desde_un_Operador_CAE_cerrado_por_D3_se_rechaza_sin_escribir()
    {
        var tenantsConNombre = new TenantsConNombre(existe: false);
        var asignaciones = new AsignacionOperadorDelegadoRepositorioFalso();
        var outbound = new OutboundCerradoTenantTecFijo(t => t == _operador.Id);

        var resultado = await HandlerDeClienteDelegante(tenantsConNombre, asignaciones, outbound).Handle(
            new CrearClienteDeleganteCommand("Construcciones Boreal"), CancellationToken.None);

        DebeSerElErrorDeD3(resultado);
        outbound.Preguntados.Should().Equal(_operador.Id);
        tenantsConNombre.Agregados.Should().BeEmpty();
        _vinculos.Delegaciones.Should().BeEmpty();
        asignaciones.Asignaciones.Should().BeEmpty();
        _writer.OperacionesAbiertas.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Dar_de_alta_un_cliente_delegante_desde_un_Operador_CAE_con_linea_Outbound_vigente_crea_la_delegacion()
    {
        var tenantsConNombre = new TenantsConNombre(existe: false);
        var outbound = OutboundCerradoTenantTecFijo.Abierto();

        var resultado = await HandlerDeClienteDelegante(tenantsConNombre, new AsignacionOperadorDelegadoRepositorioFalso(), outbound)
            .Handle(new CrearClienteDeleganteCommand("Construcciones Boreal"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        outbound.Preguntados.Should().Equal(_operador.Id);
        _vinculos.Delegaciones.Should().ContainSingle().Which.TenantConsultoraId.Should().Be(_operador.Id);
        _writer.OperacionesAbiertas.Should().ContainSingle().Which.Should().Be((resultado.Valor, _operador.Id));
    }

    // ── LimiteTenantTecDelOperadorCae: la regla que usan Solicitar, Proponer y las dos Aceptar de cartera ──

    [Fact]
    public async Task Con_el_Tenant_beneficiario_abierto_y_el_Operador_CAE_cerrado_esta_cerrado()
    {
        var outbound = new OutboundCerradoTenantTecFijo(t => t == _operador.Id);

        (await outbound.EstaCerradoConOperadorCaeAsync(_propietario.Id, _operador.Id)).Should().BeTrue();
        outbound.Preguntados.Should().Equal(_propietario.Id, _operador.Id);
    }

    [Fact]
    public async Task Con_los_dos_abiertos_esta_abierto_y_con_el_beneficiario_cerrado_no_pregunta_por_el_Operador_CAE()
    {
        var abierto = OutboundCerradoTenantTecFijo.Abierto();
        (await abierto.EstaCerradoConOperadorCaeAsync(_propietario.Id, _operador.Id)).Should().BeFalse();
        abierto.Preguntados.Should().Equal(_propietario.Id, _operador.Id);

        var beneficiarioCerrado = new OutboundCerradoTenantTecFijo(t => t == _propietario.Id);
        (await beneficiarioCerrado.EstaCerradoConOperadorCaeAsync(_propietario.Id, _operador.Id)).Should().BeTrue();
        beneficiarioCerrado.Preguntados.Should().Equal(_propietario.Id);
    }

    [Fact]
    public async Task Si_el_Operador_CAE_es_el_propio_Tenant_beneficiario_se_pregunta_una_vez()
    {
        var outbound = OutboundCerradoTenantTecFijo.Abierto();

        (await outbound.EstaCerradoConOperadorCaeAsync(_propietario.Id, _propietario.Id)).Should().BeFalse();
        outbound.Preguntados.Should().Equal(_propietario.Id);
    }

    private sealed class TenantsConNombre(bool existe) : ITenantRepository
    {
        public List<Tenant> Agregados { get; } = [];
        public Task<bool> ExisteConNombreAsync(string nombre, CancellationToken cancellationToken = default) => Task.FromResult(existe);
        public Task<Tenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Agregar(Tenant tenant) => Agregados.Add(tenant);
    }

    private sealed class ParametrosFalsos : IParametroSistemaRepository
    {
        public Task<ParametroSistema> ObtenerAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Agregar(ParametroSistema parametro) { }
    }
}
