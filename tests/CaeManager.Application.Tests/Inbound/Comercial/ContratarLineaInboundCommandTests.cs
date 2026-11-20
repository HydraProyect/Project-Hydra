using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Comercial.Commands.ContratarLineaInbound;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Inbound.Comercial;

/// <summary>
/// B-04a: las reglas de Application de <see cref="ContratarLineaInboundCommand"/> (decisiones D-A a D-E, 2026-11-15).
/// Solo una Sesión Privilegiada de Aprovisionamiento sobre el Tenant activo contrata; cualquier otra combinación se
/// deniega antes de leer nada. Lo que la base añade (el rol elevado, el WITH CHECK de la política y el índice único
/// parcial) lo prueba <c>ContratarLineaInboundPorAprovisionamientoTests</c> en integración.
/// </summary>
public class ContratarLineaInboundCommandTests
{
    /// <summary>El <c>now()</c> de la transacción de la base que devuelve el repositorio falso.</summary>
    private static readonly DateTime AhoraDeLaBase = new(2026, 11, 15, 10, 30, 0, DateTimeKind.Utc);

    private readonly Tenant _tenant = new("Tenant TCE de prueba");
    private readonly TenantsFalsos _tenants = new();
    private readonly TransaccionDeComandoFalsa _transaccionInterna = new();
    private readonly TransaccionQueAnota _transaccion;
    private readonly ProductosContratadosFalsos _productos;

    public ContratarLineaInboundCommandTests()
    {
        _tenants.Lista.Add(_tenant);
        _transaccion = new TransaccionQueAnota(_transaccionInterna);
        _productos = new ProductosContratadosFalsos(() => _transaccion.Dentro);
    }

    private static ContratarLineaInboundCommand Comando(string pagador = "cus_PagadorTce", bool declaraSoloInbound = true) =>
        new(pagador, TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual, declaraSoloInbound);

    /// <summary>
    /// Opción A' (DECISION_LOG e496f33): la declaración «solo Inbound» es opcional y el comando la guarda en la fila de la
    /// línea Inbound. Sin ella se contrata igual, la fila queda en <c>false</c> y el Tenant conserva Outbound.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_comando_persiste_la_declaracion_solo_Inbound_tal_como_llega(bool declara)
    {
        var resultado = await Handler(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id), _tenant.Id)
            .Handle(Comando(declaraSoloInbound: declara), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : string.Empty);
        var inbound = _productos.Agregados.Should().ContainSingle().Subject;
        inbound.DeclaraSoloInbound.Should().Be(declara);
        new ContratarLineaInboundCommandValidator().Validate(Comando(declaraSoloInbound: declara)).IsValid.Should().BeTrue(
            "la casilla es opcional");

        // Sin línea Outbound, solo la declaración decide si el Tenant pasa a «solo Inbound».
        AsignarTenant(inbound, _tenant.Id);
        (await new TenantSoloInboundService(new LineasFalsas([inbound]), new RelojFijo(AhoraDeLaBase.AddMinutes(1)))
            .EsSoloInboundAsync(_tenant.Id)).Should().Be(declara);
    }

    /// <summary>
    /// Declararla con la línea Outbound registrada no cierra Outbound: el comando no toca la línea Outbound (bajo el rol de
    /// aprovisionamiento no la ve), y <see cref="TenantSoloInboundService"/> sigue viendo su fila, así que el Tenant no pasa
    /// a «solo Inbound».
    /// </summary>
    [Fact]
    public async Task Con_la_declaracion_un_Tenant_con_linea_Outbound_contrata_Inbound_y_conserva_Outbound()
    {
        var outbound = ProductoContratado.Contratar(
            LineaProductoContratado.OutboundGestionPropia, "cus_PagadorOutbound", TramoTrabajadoresActivos.De1A10,
            ModalidadSuscripcionAlta.Anual, AhoraDeLaBase.AddDays(-60), ahoraUtc: AhoraDeLaBase.AddDays(-60));
        AsignarTenant(outbound, _tenant.Id);

        var resultado = await Handler(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id), _tenant.Id)
            .Handle(Comando(declaraSoloInbound: true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : string.Empty);
        var inbound = _productos.Agregados.Should().ContainSingle("el comando solo añade la línea Inbound").Subject;
        inbound.Linea.Should().Be(LineaProductoContratado.Inbound);
        // La base pone el Tenant del contexto firmado al guardar; el repositorio falso no.
        AsignarTenant(inbound, _tenant.Id);
        outbound.EstaEliminado.Should().BeFalse();
        outbound.Estado.Should().Be(EstadoProductoContratado.Vigente, "la línea Outbound no se toca");

        var lineas = new LineasFalsas([outbound, inbound]);
        var soloInbound = await new TenantSoloInboundService(lineas, new RelojFijo(AhoraDeLaBase.AddMinutes(1)))
            .EsSoloInboundAsync(_tenant.Id);
        soloInbound.Should().BeFalse("con la línea Outbound registrada el Tenant conserva Outbound");

        // Control del instrumento: la misma línea Inbound sin la Outbound sí es «solo Inbound».
        (await new TenantSoloInboundService(new LineasFalsas([inbound]), new RelojFijo(AhoraDeLaBase.AddMinutes(1)))
            .EsSoloInboundAsync(_tenant.Id)).Should().BeTrue();
    }

    private static void AsignarTenant(ProductoContratado producto, Guid tenantId) =>
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(producto, tenantId);

    private sealed class LineasFalsas(IReadOnlyList<ProductoContratado> filas) : ILineaOutboundQueryContext
    {
        public IQueryable<ProductoContratado> ProductosContratados => new TestAsyncQueryable<ProductoContratado>(filas.AsQueryable());
    }

    private sealed class RelojFijo(DateTime ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahora);
    }

    [Fact]
    public async Task Con_la_sesion_de_Aprovisionamiento_sobre_el_Tenant_activo_contrata_la_linea_Inbound_vigente_desde_ahora()
    {
        var resultado = await Handler(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id), _tenant.Id)
            .Handle(Comando(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : string.Empty);
        var producto = _productos.Agregados.Should().ContainSingle().Subject;
        producto.Id.Should().Be(resultado.Valor);
        producto.Linea.Should().Be(LineaProductoContratado.Inbound);
        producto.PagadorStripeCustomerId.Should().Be("cus_PagadorTce", "el Pagador TALVEG lo introduce a mano el Actor de Plataforma");
        producto.Tramo.Should().Be(TramoTrabajadoresActivos.De41A80);
        producto.Modalidad.Should().Be(ModalidadSuscripcionAlta.Anual);
        // Issue #2259: el inicio es el now() de la transacción de la base, no el reloj de la aplicación, porque la política
        // de INSERT no admite un inicio posterior a ese now().
        producto.VigenteDesdeUtc.Should().Be(AhoraDeLaBase);
        producto.VigenteDesdeUtc.Kind.Should().Be(DateTimeKind.Utc);
        producto.Estado.Should().Be(EstadoProductoContratado.Vigente);
        _productos.InstantesPedidos.Should().Be(1);
        _productos.Guardados.Should().Be(1);
        _transaccionInterna.Confirmadas.Should().Be(1);
    }

    [Fact]
    public async Task Sin_Sesion_Privilegiada_no_contrata_ni_lee_el_Tenant()
    {
        // El handler no mira el rol: sin Sesión Privilegiada, AutorizacionEscrituraBehavior deja pasar a un Administrador
        // del propio Tenant, y ese Administrador no puede concederse la línea Inbound.
        var resultado = await Handler(sesion: null, _tenant.Id).Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(ErroresContratarLineaInbound.NoAutorizado);
        _tenants.Lecturas.Should().Be(0, "la autoridad va antes que cualquier lectura");
        _productos.Agregados.Should().BeEmpty();
        _productos.Guardados.Should().Be(0);
        _transaccionInterna.Ejecutadas.Should().Be(0, "sin autoridad no se abre la transacción ni se pide el instante");
    }

    public static TheoryData<CapacidadPrivilegio> CapacidadesQueNoSonAprovisionamiento()
    {
        var datos = new TheoryData<CapacidadPrivilegio>();
        foreach (var capacidad in Enum.GetValues<CapacidadPrivilegio>().Where(c => c != CapacidadPrivilegio.Aprovisionamiento))
            datos.Add(capacidad);
        return datos;
    }

    [Theory]
    [MemberData(nameof(CapacidadesQueNoSonAprovisionamiento))]
    public async Task Una_Sesion_Privilegiada_de_otro_tipo_sobre_el_mismo_Tenant_no_contrata(CapacidadPrivilegio capacidad)
    {
        var resultado = await Handler(Sesion(capacidad, _tenant.Id), _tenant.Id).Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(ErroresContratarLineaInbound.NoAutorizado);
        _tenants.Lecturas.Should().Be(0);
        _productos.Agregados.Should().BeEmpty();
    }

    [Fact]
    public void Control_del_inventario_de_capacidades_ve_mas_de_una()
    {
        CapacidadesQueNoSonAprovisionamiento().Count.Should().BeGreaterThan(3,
            "control positivo: la teoría de arriba recorre todas las capacidades salvo Aprovisionamiento");
    }

    [Fact]
    public async Task Una_sesion_de_Aprovisionamiento_sobre_otro_Tenant_no_contrata_en_el_activo()
    {
        var otro = new Tenant("Otro Tenant de prueba");
        _tenants.Lista.Add(otro);

        var resultado = await Handler(Sesion(CapacidadPrivilegio.Aprovisionamiento, otro.Id), _tenant.Id)
            .Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(ErroresContratarLineaInbound.NoAutorizado);
        _tenants.Lecturas.Should().Be(0);
        _productos.Agregados.Should().BeEmpty();
    }

    [Fact]
    public async Task Bajo_impersonacion_la_sesion_de_Aprovisionamiento_no_contrata()
    {
        var sesion = Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id) with { UsuarioSimuladoId = Guid.NewGuid() };

        var resultado = await Handler(sesion, _tenant.Id).Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(ErroresContratarLineaInbound.NoAutorizado);
        _tenants.Lecturas.Should().Be(0);
        _productos.Agregados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_Tenant_activo_no_contrata_aunque_haya_sesion_de_Aprovisionamiento()
    {
        var resultado = await Handler(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id), tenantActivo: null)
            .Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(ErroresContratarLineaInbound.NoAutorizado);
        _productos.Agregados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sobre_un_Tenant_que_ya_no_existe_no_contrata()
    {
        var inexistente = Guid.NewGuid();

        var resultado = await Handler(Sesion(CapacidadPrivilegio.Aprovisionamiento, inexistente), inexistente)
            .Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(ErroresContratarLineaInbound.TenantNoEncontrado);
        _tenants.Lecturas.Should().Be(1);
        _productos.Agregados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sobre_la_organizacion_de_plataforma_no_contrata()
    {
        var plataforma = new Tenant("Organización de plataforma de prueba", esPlataforma: true);
        _tenants.Lista.Add(plataforma);

        var resultado = await Handler(Sesion(CapacidadPrivilegio.Aprovisionamiento, plataforma.Id), plataforma.Id)
            .Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(ErroresContratarLineaInbound.TenantNoValido);
        _productos.Agregados.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_Tenant_ya_tiene_la_linea_Inbound_vigente_no_contrata_otra()
    {
        _productos.RechazarPorInboundVigenteDuplicado = true;

        var resultado = await Handler(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id), _tenant.Id)
            .Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(ErroresContratarLineaInbound.YaContratada);
        _productos.Guardados.Should().Be(1, "la regla la impone el índice único parcial al guardar, no una lectura previa");
        _transaccionInterna.Deshechas.Should().Be(1, "el intento rechazado no confirma nada");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" cus_X")]
    [InlineData("cus-X")]
    [InlineData("cus_ñ")]
    public async Task Un_Pagador_TALVEG_con_formato_no_valido_no_contrata(string pagador)
    {
        var resultado = await Handler(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id), _tenant.Id)
            .Handle(Comando(pagador), CancellationToken.None);

        resultado.Error.Should().Be(ErroresContratarLineaInbound.PagadorNoValido);
        _productos.Agregados.Should().BeEmpty();
        new ContratarLineaInboundCommandValidator().Validate(Comando(pagador)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Un_Pagador_TALVEG_demasiado_largo_no_contrata()
    {
        var largo = "cus_" + new string('A', ProductoContratado.LongitudMaximaPagadorStripeCustomerId - 3);

        var resultado = await Handler(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id), _tenant.Id)
            .Handle(Comando(largo), CancellationToken.None);

        resultado.Error.Should().Be(ErroresContratarLineaInbound.PagadorNoValido);
        _productos.Agregados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_tramo_o_una_modalidad_fuera_de_la_lista_no_contrata()
    {
        var sesion = Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant.Id);
        var tramo = new ContratarLineaInboundCommand("cus_X", (TramoTrabajadoresActivos)99, ModalidadSuscripcionAlta.Anual, DeclaraSoloInbound: true);
        var modalidad = new ContratarLineaInboundCommand("cus_X", TramoTrabajadoresActivos.De1A10, (ModalidadSuscripcionAlta)99, DeclaraSoloInbound: true);

        (await Handler(sesion, _tenant.Id).Handle(tramo, CancellationToken.None)).Error
            .Should().Be(ErroresContratarLineaInbound.ContratoNoValido);
        (await Handler(sesion, _tenant.Id).Handle(modalidad, CancellationToken.None)).Error
            .Should().Be(ErroresContratarLineaInbound.ContratoNoValido);
        _productos.Agregados.Should().BeEmpty();
        new ContratarLineaInboundCommandValidator().Validate(tramo).IsValid.Should().BeFalse();
        new ContratarLineaInboundCommandValidator().Validate(modalidad).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Es_un_comando_de_aprovisionamiento_para_que_escriba_con_el_rol_elevado()
    {
        typeof(IComandoDeAprovisionamiento).IsAssignableFrom(typeof(ContratarLineaInboundCommand)).Should().BeTrue(
            "solo cae_app_aprovisionamiento tiene INSERT sobre ProductosContratados (D-C)");
        new ContratarLineaInboundCommandValidator().Validate(Comando()).IsValid.Should().BeTrue("control del validador");
    }

    private static SesionPrivilegiadaActiva Sesion(CapacidadPrivilegio capacidad, Guid tenantObjetivo) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tenantObjetivo, capacidad, null);

    private ContratarLineaInboundCommandHandler Handler(SesionPrivilegiadaActiva? sesion, Guid? tenantActivo) =>
        new(new TenantActivo(tenantActivo), new SesionFija(sesion), _tenants, _productos, _transaccion);

    private sealed class TenantActivo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class SesionFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        // Hallazgo 1 de Codex (ronda 1): el handler corre con la conexión elevada a cae_app_aprovisionamiento, que no
        // lee SesionesPrivilegiadas; revalidar ahí fallaría con 42501. El falso reproduce ese fallo.
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "El handler no revalida: la conexión elevada no tiene SELECT sobre SesionesPrivilegiadas (42501).");
    }

    /// <summary>Cuenta los accesos al catálogo de Tenants; el resto de colecciones no se lee.</summary>
    private sealed class TenantsFalsos : ITenantsQueryContext
    {
        public List<Tenant> Lista { get; } = [];
        public int Lecturas { get; private set; }

        public IQueryable<Tenant> Tenants
        {
            get
            {
                Lecturas++;
                return new TestAsyncQueryable<Tenant>(Lista.AsQueryable());
            }
        }

        public IQueryable<DelegacionTenant> DelegacionesTenant =>
            throw new NotSupportedException("el alta de la línea solo lee Tenants");
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado =>
            throw new NotSupportedException("el alta de la línea solo lee Tenants");
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte =>
            throw new NotSupportedException("el alta de la línea solo lee Tenants");
    }

    /// <summary>
    /// Anota si el instante y el guardado llegan dentro de la transacción del comando: el instante tiene que ser el
    /// <c>now()</c> de la misma transacción que inserta, el que comprueba la política.
    /// </summary>
    private sealed class TransaccionQueAnota(TransaccionDeComandoFalsa interna) : ITransaccionDeComando
    {
        public bool Dentro { get; private set; }

        public async Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
        {
            Dentro = true;
            try
            {
                return await interna.EjecutarAsync(operacion, cancellationToken);
            }
            finally
            {
                Dentro = false;
            }
        }
    }

    private sealed class ProductosContratadosFalsos(Func<bool> dentroDeLaTransaccion) : IProductoContratadoRepository
    {
        public List<ProductoContratado> Agregados { get; } = [];
        public int Guardados { get; private set; }
        public int InstantesPedidos { get; private set; }
        public bool RechazarPorInboundVigenteDuplicado { get; set; }

        public void Agregar(ProductoContratado producto) => Agregados.Add(producto);

        public Task<DateTime> InstanteDeLaTransaccionAsync(CancellationToken cancellationToken = default)
        {
            dentroDeLaTransaccion().Should().BeTrue("el instante es el now() de la transacción que inserta la fila");
            InstantesPedidos++;
            return Task.FromResult(AhoraDeLaBase);
        }

        public Task<bool> GuardarDetectandoInboundVigenteDuplicadoAsync(
            ProductoContratado intento, CancellationToken cancellationToken = default)
        {
            // El handler guarda exactamente la instancia que añadió: el repositorio la identifica por referencia.
            Agregados.Should().ContainSingle().Which.Should().BeSameAs(intento);
            dentroDeLaTransaccion().Should().BeTrue("la fila se guarda en la transacción de la que salió el instante");
            Guardados++;
            return Task.FromResult(!RechazarPorInboundVigenteDuplicado);
        }

        public Task<FinalizacionInbound> FinalizarInboundVigenteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("el alta de la línea Inbound no finaliza nada");
    }
}
