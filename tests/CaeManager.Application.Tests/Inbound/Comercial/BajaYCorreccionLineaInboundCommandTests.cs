using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Comercial.Commands.CorregirLineaInbound;
using CaeManager.Application.Inbound.Comercial.Commands.DarDeBajaLineaInbound;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Inbound.Comercial;

/// <summary>
/// Opción B del 2026-11-15: las reglas de Application de <see cref="DarDeBajaLineaInboundCommand"/> y de
/// <see cref="CorregirLineaInboundCommand"/>. Solo una Sesión Privilegiada de Aprovisionamiento sobre el Tenant activo,
/// sin impersonación, da de baja o corrige; cualquier otra combinación se deniega sin tocar el repositorio. La corrección
/// finaliza antes de contratar, en la misma transacción y con el instante que devuelve la finalización (el now() de la base). Lo que añade la base (los privilegios por
/// columna, las políticas RESTRICTIVE, el índice único parcial y la transacción real) lo prueba
/// <c>BajaYCorreccionLineaInboundPorAprovisionamientoTests</c> en integración.
/// </summary>
public class BajaYCorreccionLineaInboundCommandTests
{
    /// <summary>El now() de la transacción de la base, que devuelve la finalización.</summary>
    private static readonly DateTime AhoraDeLaBase = new(2026, 11, 30, 23, 59, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly ProductosContratadosFalsos _productos = new();
    private readonly TransaccionDeComandoFalsa _transaccion = new();

    private static CorregirLineaInboundCommand Correccion(string pagador = "cus_PagadorCorregido", bool declara = false) =>
        new(pagador, TramoTrabajadoresActivos.De11A40, ModalidadSuscripcionAlta.Mensual, declara);

    /// <summary>
    /// Opción A' (DECISION_LOG e496f33): la corrección contrata una fila nueva y guarda en ella la declaración «solo Inbound»
    /// que llega en el comando; no la copia de la sustituida, que el rol elevado no puede leer.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task La_correccion_persiste_la_declaracion_solo_Inbound_tal_como_llega(bool declara)
    {
        var resultado = await new CorregirLineaInboundCommandHandler(
                new TenantActivo(_tenant), new SesionFija(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant)),
                _productos, _transaccion)
            .Handle(Correccion(declara: declara), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : string.Empty);
        _productos.Agregados.Should().ContainSingle().Which.DeclaraSoloInbound.Should().Be(declara);
        new CorregirLineaInboundCommandValidator().Validate(Correccion(declara: declara)).IsValid.Should().BeTrue();
    }

    public static TheoryData<string> Comandos() => new() { "baja", "correccion" };

    private Task<Result<Guid>> EnviarAsync(string comando, SesionPrivilegiadaActiva? sesion, Guid? tenantActivo) =>
        comando == "baja"
            ? new DarDeBajaLineaInboundCommandHandler(
                    new TenantActivo(tenantActivo), new SesionFija(sesion), _productos, _transaccion)
                .Handle(new DarDeBajaLineaInboundCommand(), CancellationToken.None)
            : new CorregirLineaInboundCommandHandler(
                    new TenantActivo(tenantActivo), new SesionFija(sesion), _productos, _transaccion)
                .Handle(Correccion(), CancellationToken.None);

    private static Error NoAutorizado(string comando) =>
        comando == "baja" ? ErroresDarDeBajaLineaInbound.NoAutorizado : ErroresCorregirLineaInbound.NoAutorizado;

    [Fact]
    public async Task La_baja_finaliza_la_linea_Inbound_vigente_en_el_instante_de_ejecutarla()
    {
        var resultado = await EnviarAsync("baja", Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant), _tenant);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : string.Empty);
        resultado.Valor.Should().Be(_productos.IdVigente);
        _productos.Llamadas.Should().Equal("Finalizar");
        _transaccion.Confirmadas.Should().Be(1, "la baja y su auditoría van en una transacción");
    }

    [Fact]
    public async Task La_correccion_finaliza_la_vigente_antes_de_contratar_la_corregida_con_el_mismo_instante()
    {
        var resultado = await EnviarAsync("correccion", Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant), _tenant);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : string.Empty);
        _productos.Llamadas.Should().Equal(
            ["Finalizar", "Agregar", "Guardar"],
            "el índice único parcial solo admite la nueva cuando la sustituida ya no es vigente");
        var corregido = _productos.Agregados.Should().ContainSingle().Subject;
        corregido.Id.Should().Be(resultado.Valor);
        corregido.Linea.Should().Be(LineaProductoContratado.Inbound);
        corregido.PagadorStripeCustomerId.Should().Be("cus_PagadorCorregido");
        corregido.Tramo.Should().Be(TramoTrabajadoresActivos.De11A40);
        corregido.Modalidad.Should().Be(ModalidadSuscripcionAlta.Mensual);
        corregido.Estado.Should().Be(EstadoProductoContratado.Vigente);
        corregido.VigenteDesdeUtc.Should().Be(AhoraDeLaBase,
            "sin hueco ni solape: la corregida empieza en el fin de la sustituida, el now() de la transacción de la base");
        corregido.VigenteDesdeUtc.Kind.Should().Be(DateTimeKind.Utc);
        _transaccion.Ejecutadas.Should().Be(1, "las dos escrituras en una sola transacción");
        _transaccion.Confirmadas.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Sin_Sesion_Privilegiada_no_toca_el_repositorio(string comando)
    {
        // Sin Sesión Privilegiada, AutorizacionEscrituraBehavior deja pasar a un Administrador del propio Tenant.
        var resultado = await EnviarAsync(comando, sesion: null, _tenant);

        resultado.Error.Should().Be(NoAutorizado(comando));
        _productos.Llamadas.Should().BeEmpty("la autoridad va antes que cualquier lectura");
        _transaccion.Ejecutadas.Should().Be(0);
    }

    public static TheoryData<string, CapacidadPrivilegio> ComandosYCapacidadesQueNoSonAprovisionamiento()
    {
        var datos = new TheoryData<string, CapacidadPrivilegio>();
        foreach (var comando in new[] { "baja", "correccion" })
            foreach (var capacidad in Enum.GetValues<CapacidadPrivilegio>().Where(c => c != CapacidadPrivilegio.Aprovisionamiento))
                datos.Add(comando, capacidad);
        return datos;
    }

    [Theory]
    [MemberData(nameof(ComandosYCapacidadesQueNoSonAprovisionamiento))]
    public async Task Una_Sesion_Privilegiada_de_otro_tipo_no_da_de_baja_ni_corrige(string comando, CapacidadPrivilegio capacidad)
    {
        var resultado = await EnviarAsync(comando, Sesion(capacidad, _tenant), _tenant);

        resultado.Error.Should().Be(NoAutorizado(comando));
        _productos.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public void Control_del_inventario_de_capacidades_ve_mas_de_una_por_comando()
    {
        ComandosYCapacidadesQueNoSonAprovisionamiento().Count.Should().BeGreaterThan(6,
            "control positivo: la teoría de arriba recorre todas las capacidades salvo Aprovisionamiento, en los dos comandos");
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Una_sesion_de_Aprovisionamiento_sobre_otro_Tenant_no_actua_en_el_activo(string comando)
    {
        var resultado = await EnviarAsync(comando, Sesion(CapacidadPrivilegio.Aprovisionamiento, Guid.NewGuid()), _tenant);

        resultado.Error.Should().Be(NoAutorizado(comando));
        _productos.Llamadas.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Bajo_impersonacion_la_sesion_de_Aprovisionamiento_no_actua(string comando)
    {
        var sesion = Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant) with { UsuarioSimuladoId = Guid.NewGuid() };

        var resultado = await EnviarAsync(comando, sesion, _tenant);

        resultado.Error.Should().Be(NoAutorizado(comando));
        _productos.Llamadas.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Sin_Tenant_activo_no_actua_aunque_haya_sesion_de_Aprovisionamiento(string comando)
    {
        var resultado = await EnviarAsync(comando, Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant), tenantActivo: null);

        resultado.Error.Should().Be(NoAutorizado(comando));
        _productos.Llamadas.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Sin_linea_Inbound_vigente_no_da_de_baja_ni_contrata_y_deshace(string comando)
    {
        _productos.Finalizacion = new FinalizacionInbound(ResultadoFinalizacionInbound.SinInboundVigente, null);

        var resultado = await EnviarAsync(comando, Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant), _tenant);

        resultado.Error.Should().Be(
            comando == "baja" ? ErroresDarDeBajaLineaInbound.SinLineaVigente : ErroresCorregirLineaInbound.SinLineaVigente);
        _productos.Agregados.Should().BeEmpty("la corrección no contrata si no había nada que sustituir");
        _transaccion.Deshechas.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Con_la_vigencia_aun_sin_empezar_no_da_de_baja_ni_contrata(string comando)
    {
        _productos.Finalizacion = new FinalizacionInbound(ResultadoFinalizacionInbound.VigenciaAunNoIniciada, null);

        var resultado = await EnviarAsync(comando, Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant), _tenant);

        resultado.Error.Should().Be(comando == "baja"
            ? ErroresDarDeBajaLineaInbound.VigenciaAunNoIniciada
            : ErroresCorregirLineaInbound.VigenciaAunNoIniciada);
        _productos.Agregados.Should().BeEmpty();
        _transaccion.Deshechas.Should().Be(1);
    }

    [Fact]
    public async Task Si_la_base_rechaza_la_corregida_por_duplicado_se_deshace_tambien_la_finalizacion()
    {
        _productos.RechazarPorInboundVigenteDuplicado = true;

        var resultado = await EnviarAsync("correccion", Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant), _tenant);

        resultado.Error.Should().Be(ErroresCorregirLineaInbound.YaContratada);
        _transaccion.Deshechas.Should().Be(1, "la finalización no se confirma sin la corregida");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" cus_X")]
    [InlineData("cus-X")]
    public async Task Un_Pagador_TALVEG_con_formato_no_valido_no_corrige_ni_finaliza(string pagador)
    {
        var resultado = await new CorregirLineaInboundCommandHandler(
                new TenantActivo(_tenant), new SesionFija(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant)),
                _productos, _transaccion)
            .Handle(Correccion(pagador), CancellationToken.None);

        resultado.Error.Should().Be(ErroresCorregirLineaInbound.PagadorNoValido);
        _productos.Llamadas.Should().BeEmpty("se valida antes de finalizar nada");
        new CorregirLineaInboundCommandValidator().Validate(Correccion(pagador)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Un_tramo_o_una_modalidad_fuera_de_la_lista_no_corrige_ni_finaliza()
    {
        var handler = new CorregirLineaInboundCommandHandler(
            new TenantActivo(_tenant), new SesionFija(Sesion(CapacidadPrivilegio.Aprovisionamiento, _tenant)),
            _productos, _transaccion);
        var tramo = new CorregirLineaInboundCommand("cus_X", (TramoTrabajadoresActivos)99, ModalidadSuscripcionAlta.Anual, false);
        var modalidad = new CorregirLineaInboundCommand("cus_X", TramoTrabajadoresActivos.De1A10, (ModalidadSuscripcionAlta)99, false);

        (await handler.Handle(tramo, CancellationToken.None)).Error.Should().Be(ErroresCorregirLineaInbound.ContratoNoValido);
        (await handler.Handle(modalidad, CancellationToken.None)).Error.Should().Be(ErroresCorregirLineaInbound.ContratoNoValido);
        _productos.Llamadas.Should().BeEmpty();
        new CorregirLineaInboundCommandValidator().Validate(tramo).IsValid.Should().BeFalse();
        new CorregirLineaInboundCommandValidator().Validate(modalidad).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Los_dos_son_comandos_de_aprovisionamiento_para_que_escriban_con_el_rol_elevado()
    {
        typeof(IComandoDeAprovisionamiento).IsAssignableFrom(typeof(DarDeBajaLineaInboundCommand)).Should().BeTrue();
        typeof(IComandoDeAprovisionamiento).IsAssignableFrom(typeof(CorregirLineaInboundCommand)).Should().BeTrue();
        new CorregirLineaInboundCommandValidator().Validate(Correccion()).IsValid.Should().BeTrue("control del validador");
    }

    private static SesionPrivilegiadaActiva Sesion(CapacidadPrivilegio capacidad, Guid tenantObjetivo) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tenantObjetivo, capacidad, null);

    private sealed class TenantActivo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class SesionFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        // El handler corre con la conexión elevada, que no lee el plano de Plataforma: revalidar ahí fallaría con 42501.
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("El handler no revalida: la conexión elevada no lee el plano de Plataforma (42501).");
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }

    /// <summary>Anota las llamadas en orden; la finalización devuelve lo que fije el test.</summary>
    private sealed class ProductosContratadosFalsos : IProductoContratadoRepository
    {
        public Guid IdVigente { get; } = Guid.NewGuid();
        public List<string> Llamadas { get; } = [];
        public List<ProductoContratado> Agregados { get; } = [];
        public bool RechazarPorInboundVigenteDuplicado { get; set; }
        public FinalizacionInbound? Finalizacion { get; set; }

        public void Agregar(ProductoContratado producto)
        {
            Llamadas.Add("Agregar");
            Agregados.Add(producto);
        }

        // La corrección toma el instante de la finalización, no lo pide aparte: así el fin de la sustituida y el inicio de la
        // corregida son el mismo.
        public Task<DateTime> InstanteDeLaTransaccionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("la baja y la corrección usan el fin que devuelve la finalización");

        public Task<bool> GuardarDetectandoInboundVigenteDuplicadoAsync(
            ProductoContratado intento, CancellationToken cancellationToken = default)
        {
            Agregados.Should().ContainSingle().Which.Should().BeSameAs(intento);
            Llamadas.Add("Guardar");
            return Task.FromResult(!RechazarPorInboundVigenteDuplicado);
        }

        public Task<FinalizacionInbound> FinalizarInboundVigenteAsync(CancellationToken cancellationToken = default)
        {
            Llamadas.Add("Finalizar");
            return Task.FromResult(Finalizacion
                                   ?? new FinalizacionInbound(ResultadoFinalizacionInbound.Finalizada, IdVigente, AhoraDeLaBase));
        }
    }
}
