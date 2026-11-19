using CaeManager.Application.Common;
using CaeManager.Application.Inbound.AccesoFisico.Credenciales;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Inbound.ContratosAcceso;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.AccesoFisico;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.Application.Tests.Inbound.AccesoFisico;

/// <summary>
/// Comportamiento de los tres comandos de la credencial de acceso físico (AF-02) en el Tenant TCE: quién es acreditable
/// (supuesto H5: evidencia en un contrato Activo y vinculado que el actor ve por su Asignación de Cartera), una sola Emitida
/// por Trabajador, regenerar sube la serie, revocar guarda el motivo cerrado y los choques de la base se responden sin
/// excepción. La denegación sin capacidad la cubre HandlersInboundDeniegaSinCapacidadTests y lo que la base garantiza
/// (RLS, índice único parcial, CHECK, privilegios), CredencialesAccesoBajoRuntimeTests.
/// </summary>
public class CredencialesAccesoTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private readonly Guid _cliente = Guid.NewGuid();
    private readonly Guid _tenantContratista = Guid.NewGuid();
    private readonly Guid _trabajador = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly ContratosAccesoQueryContextFalso _contratos = new();
    private readonly List<EvidenciaInbound> _evidencias = [];
    private readonly RepositorioFalso _repositorio = new();
    private readonly UnidadDeTrabajoFalsa _unidad = new();
    private readonly DescarteFalso _descarte = new();
    private readonly ContratoAcceso _contrato;

    public CredencialesAccesoTests()
    {
        _contrato = ContratoAcceso.Crear(_cliente, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), null, null);
        _contrato.VincularTenantContratista(_tenantContratista);
        _contratos.ListaContratos.Add(_contrato);
        _evidencias.Add(Evidencia(_contrato.Id, _trabajador, "Ana Pérez", "12345678Z"));
    }

    private EvidenciaInbound Evidencia(Guid contratoId, Guid trabajador, string nombre, string? documento) =>
        EvidenciaInbound.Registrar(
            new EvidenciaInbound.Procedencia(contratoId, _tenantContratista, Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(),
            new EvidenciaInbound.Sujeto(trabajador, nombre, documento), new DateOnly(2026, 3, 1), new DateOnly(2027, 3, 1),
            soloConclusionAptitud: false, conclusion: null,
            new EvidenciaInbound.ArchivoCopiado($"{Guid.NewGuid():N}/copia.pdf", new string('a', 64)), Guid.NewGuid(), Ahora.UtcDateTime);

    private static AlcanceDatosServiceFalso Alcance(params Guid[] clientesVisibles) =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: clientesVisibles);

    private EmitirCredencialCommandHandler Emitir(AlcanceDatosServiceFalso? alcance = null, Guid? usuario = null) =>
        new(_repositorio, _contratos, new EvidenciasFalsas(_evidencias), alcance ?? Alcance(_cliente), new UsuarioFalso(usuario ?? _usuario),
            _unidad, _descarte, new RelojFijo(Ahora), new AutorizacionInboundQuePermite());

    private RegenerarCredencialCommandHandler Regenerar(AlcanceDatosServiceFalso? alcance = null) =>
        new(_repositorio, _contratos, new EvidenciasFalsas(_evidencias), alcance ?? Alcance(_cliente), new UsuarioFalso(_usuario),
            _unidad, _descarte, new RelojFijo(Ahora.AddHours(1)), new AutorizacionInboundQuePermite());

    private RevocarCredencialCommandHandler Revocar(AlcanceDatosServiceFalso? alcance = null) =>
        new(_repositorio, _contratos, new EvidenciasFalsas(_evidencias), alcance ?? Alcance(_cliente), new UsuarioFalso(_usuario),
            _unidad, _descarte, new RelojFijo(Ahora.AddHours(2)), new AutorizacionInboundQuePermite());

    private EmitirCredencialCommand ComandoEmitir() => new(_tenantContratista, _trabajador);

    private async Task<CredencialAcceso> EmitidaAsync()
    {
        var resultado = await Emitir().Handle(ComandoEmitir(), CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue();
        return _repositorio.Credenciales.Single(c => c.Id == resultado.Valor);
    }

    [Fact]
    public async Task Emitir_crea_la_credencial_serie_uno_con_la_instantanea_de_la_evidencia_y_quien_la_emite()
    {
        var resultado = await Emitir().Handle(ComandoEmitir(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var credencial = _repositorio.Credenciales.Should().ContainSingle().Subject;
        credencial.Id.Should().Be(resultado.Valor);
        (credencial.TenantOrigenId, credencial.TrabajadorOrigenId).Should().Be((_tenantContratista, _trabajador));
        (credencial.SujetoNombre, credencial.SujetoDocumentoIdentidad).Should().Be(("Ana Pérez", "12345678Z"));
        (credencial.Serie, credencial.Estado).Should().Be((1, EstadoCredencialAcceso.Emitida));
        credencial.EmitidaPorUsuarioId.Should().Be(_usuario);
        credencial.EmitidaEnUtc.Should().Be(Ahora.UtcDateTime);
        _unidad.Guardados.Should().Be(1);
    }

    [Fact]
    public async Task Un_Trabajador_sin_evidencia_en_un_contrato_del_actor_no_se_encuentra()
    {
        var sinEvidencia = new EmitirCredencialCommand(_tenantContratista, Guid.NewGuid());
        var otroTenant = new EmitirCredencialCommand(Guid.NewGuid(), _trabajador);

        var a = await Emitir().Handle(sinEvidencia, CancellationToken.None);
        var b = await Emitir().Handle(otroTenant, CancellationToken.None);

        a.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        b.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        _repositorio.Credenciales.Should().BeEmpty();
        _unidad.Guardados.Should().Be(0);
    }

    [Fact]
    public async Task Un_actor_sin_cartera_sobre_el_Cliente_empresarial_del_contrato_no_ve_al_Trabajador()
    {
        var resultado = await Emitir(Alcance(Guid.NewGuid())).Handle(ComandoEmitir(), CancellationToken.None);

        resultado.Error.Should().Be(CredencialAccesoErrores.NoEncontrada,
            "no distingue «no existe» de «no lo ves»: no revela qué Trabajadores hay en carteras ajenas");
        _repositorio.Credenciales.Should().BeEmpty();
    }

    /// <summary>
    /// La evidencia que acredita al Trabajador está en un contrato de un Cliente empresarial fuera de la cartera del actor, aunque
    /// este vea otro contrato del mismo Tenant TEC: el cruce evidencia-contrato visible es la autoridad, y sin él el actor
    /// controlaría credenciales de carteras ajenas.
    /// </summary>
    [Fact]
    public async Task Una_evidencia_en_un_contrato_fuera_de_la_cartera_no_acredita_aunque_el_actor_vea_otro_contrato_del_mismo_Tenant_TEC()
    {
        var clienteAjeno = Guid.NewGuid();
        var contratoAjeno = ContratoAcceso.Crear(clienteAjeno, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), null, null);
        contratoAjeno.VincularTenantContratista(_tenantContratista);
        _contratos.ListaContratos.Add(contratoAjeno);
        var otroTrabajador = Guid.NewGuid();
        _evidencias.Add(Evidencia(contratoAjeno.Id, otroTrabajador, "Luis Gómez", "11111111H"));
        var credencial = await EmitidaAsync();

        var emitir = await Emitir().Handle(new EmitirCredencialCommand(_tenantContratista, otroTrabajador), CancellationToken.None);
        var propia = _repositorio.Credenciales.Single();
        var ajena = CredencialAcceso.Emitir(
            new CredencialAcceso.Sujeto(_tenantContratista, otroTrabajador, "Luis Gómez", "11111111H"), _usuario, Ahora.UtcDateTime);
        _repositorio.Agregar(ajena);
        var regenerar = await Regenerar().Handle(new RegenerarCredencialCommand(ajena.Id), CancellationToken.None);
        var revocar = await Revocar().Handle(
            new RevocarCredencialCommand(ajena.Id, MotivoRevocacionCredencial.Otro), CancellationToken.None);

        emitir.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        regenerar.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        revocar.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        (ajena.Serie, ajena.Estado).Should().Be((1, EstadoCredencialAcceso.Emitida));
        propia.Should().BeSameAs(credencial);
    }

    [Fact]
    public async Task Una_evidencia_rechazada_no_acredita_ni_da_la_instantanea()
    {
        // La rechazada es la más reciente: sin el filtro por Aceptada, el orden por fecha la elegiría y no el azar de un Guid.
        _evidencias[0].Rechazar(CaeManager.Domain.Documentos.CausaRechazoAcreditacion.DatosErroneos, "El DNI no coincide", _usuario, Ahora.UtcDateTime.AddHours(1));

        var sinOtra = await Emitir().Handle(ComandoEmitir(), CancellationToken.None);
        _evidencias.Add(Evidencia(_contrato.Id, _trabajador, "Ana Pérez Correcta", "99999999R"));
        var conOtra = await Emitir().Handle(ComandoEmitir(), CancellationToken.None);

        sinOtra.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        conOtra.EsExitoso.Should().BeTrue();
        _repositorio.Credenciales.Single().SujetoNombre.Should().Be("Ana Pérez Correcta");
    }

    [Fact]
    public async Task Solo_se_emite_para_un_contrato_Activo()
    {
        _contrato.Finalizar(new DateOnly(2026, 11, 1));

        var resultado = await Emitir().Handle(ComandoEmitir(), CancellationToken.None);

        resultado.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        _repositorio.Credenciales.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_usuario_actual_no_se_emite()
    {
        var resultado = await Emitir(usuario: Guid.Empty).Handle(ComandoEmitir(), CancellationToken.None);

        resultado.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        _repositorio.Credenciales.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_segunda_emision_con_otra_Emitida_responde_YaEmitida_sin_guardar()
    {
        await EmitidaAsync();
        _unidad.Guardados = 0;

        var resultado = await Emitir().Handle(ComandoEmitir(), CancellationToken.None);

        resultado.Error.Should().Be(CredencialAccesoErrores.YaEmitida);
        _repositorio.Credenciales.Should().ContainSingle();
        _unidad.Guardados.Should().Be(0);
    }

    [Fact]
    public async Task Tras_revocar_se_puede_emitir_otra_en_serie_uno()
    {
        var primera = await EmitidaAsync();
        (await Revocar().Handle(new RevocarCredencialCommand(primera.Id, MotivoRevocacionCredencial.PerdidaOExtravio), CancellationToken.None))
            .EsExitoso.Should().BeTrue();

        var segunda = await Emitir().Handle(ComandoEmitir(), CancellationToken.None);

        segunda.EsExitoso.Should().BeTrue();
        _repositorio.Credenciales.Should().HaveCount(2);
        _repositorio.Credenciales.Count(c => c.Estado == EstadoCredencialAcceso.Emitida).Should().Be(1,
            "el índice único parcial limita las Emitidas, no el histórico");
        _repositorio.Credenciales.Single(c => c.Id == segunda.Valor).Serie.Should().Be(1);
    }

    [Fact]
    public async Task Una_carrera_que_pierde_contra_el_indice_unico_parcial_responde_YaEmitida_y_descarta_lo_pendiente()
    {
        _unidad.ExcepcionAlGuardar = new DbUpdateException("duplicado", new ErrorDeBase("23505"));

        var resultado = await Emitir().Handle(ComandoEmitir(), CancellationToken.None);

        resultado.Error.Should().Be(CredencialAccesoErrores.YaEmitida);
        _descarte.Descartes.Should().Be(1);
    }

    [Fact]
    public async Task Un_error_de_base_que_no_es_de_unicidad_no_se_traga()
    {
        _unidad.ExcepcionAlGuardar = new DbUpdateException("otra cosa", new ErrorDeBase("23503"));

        var emitir = async () => await Emitir().Handle(ComandoEmitir(), CancellationToken.None);

        await emitir.Should().ThrowAsync<DbUpdateException>();
        _descarte.Descartes.Should().Be(0);
    }

    [Fact]
    public async Task Regenerar_sube_la_serie_y_trae_la_instantanea_al_dia()
    {
        var credencial = await EmitidaAsync();
        _evidencias.Clear();
        _evidencias.Add(Evidencia(_contrato.Id, _trabajador, "Ana María Pérez", "87654321X"));

        var resultado = await Regenerar().Handle(new RegenerarCredencialCommand(credencial.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(2);
        (credencial.Serie, credencial.Estado).Should().Be((2, EstadoCredencialAcceso.Emitida));
        (credencial.SujetoNombre, credencial.SujetoDocumentoIdentidad).Should().Be(("Ana María Pérez", "87654321X"));
        credencial.RegeneradaPorUsuarioId.Should().Be(_usuario);
        credencial.RegeneradaEnUtc.Should().Be(Ahora.AddHours(1).UtcDateTime);
    }

    [Fact]
    public async Task Regenerar_una_credencial_inexistente_o_fuera_de_la_cartera_responde_no_encontrada()
    {
        var credencial = await EmitidaAsync();

        var inexistente = await Regenerar().Handle(new RegenerarCredencialCommand(Guid.NewGuid()), CancellationToken.None);
        var ajena = await Regenerar(Alcance(Guid.NewGuid())).Handle(new RegenerarCredencialCommand(credencial.Id), CancellationToken.None);

        inexistente.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        ajena.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        credencial.Serie.Should().Be(1);
    }

    [Fact]
    public async Task Una_credencial_revocada_no_se_regenera()
    {
        var credencial = await EmitidaAsync();
        credencial.Revocar(MotivoRevocacionCredencial.UsoIndebido, _usuario, Ahora.UtcDateTime);

        var resultado = await Regenerar().Handle(new RegenerarCredencialCommand(credencial.Id), CancellationToken.None);

        resultado.Error.Should().Be(CredencialAccesoErrores.EstadoNoAdmite);
        credencial.Serie.Should().Be(1);
    }

    [Fact]
    public async Task Regenerar_exige_un_contrato_Activo_pero_revocar_no()
    {
        var credencial = await EmitidaAsync();
        _contrato.Finalizar(new DateOnly(2026, 11, 1));

        var regenerar = await Regenerar().Handle(new RegenerarCredencialCommand(credencial.Id), CancellationToken.None);
        var revocar = await Revocar().Handle(new RevocarCredencialCommand(credencial.Id, MotivoRevocacionCredencial.FinDeLaRelacion), CancellationToken.None);

        regenerar.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        revocar.EsExitoso.Should().BeTrue("el carné sigue en el bolsillo del Trabajador cuando el contrato acaba");
        credencial.Estado.Should().Be(EstadoCredencialAcceso.Revocada);
    }

    [Fact]
    public async Task Revocar_guarda_el_motivo_cerrado_quien_y_cuando()
    {
        var credencial = await EmitidaAsync();

        var resultado = await Revocar().Handle(
            new RevocarCredencialCommand(credencial.Id, MotivoRevocacionCredencial.UsoIndebido), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        credencial.Estado.Should().Be(EstadoCredencialAcceso.Revocada);
        credencial.MotivoRevocacion.Should().Be(MotivoRevocacionCredencial.UsoIndebido);
        credencial.RevocadaPorUsuarioId.Should().Be(_usuario);
        credencial.RevocadaEnUtc.Should().Be(Ahora.AddHours(2).UtcDateTime);
    }

    [Fact]
    public async Task Revocar_dos_veces_o_fuera_de_la_cartera_no_cambia_nada()
    {
        var credencial = await EmitidaAsync();
        var ajena = await Revocar(Alcance(Guid.NewGuid())).Handle(
            new RevocarCredencialCommand(credencial.Id, MotivoRevocacionCredencial.Otro), CancellationToken.None);
        credencial.Estado.Should().Be(EstadoCredencialAcceso.Emitida);
        await Revocar().Handle(new RevocarCredencialCommand(credencial.Id, MotivoRevocacionCredencial.PerdidaOExtravio), CancellationToken.None);

        var repetida = await Revocar().Handle(
            new RevocarCredencialCommand(credencial.Id, MotivoRevocacionCredencial.UsoIndebido), CancellationToken.None);

        ajena.Error.Should().Be(CredencialAccesoErrores.NoEncontrada);
        repetida.Error.Should().Be(CredencialAccesoErrores.EstadoNoAdmite);
        credencial.MotivoRevocacion.Should().Be(MotivoRevocacionCredencial.PerdidaOExtravio, "la segunda revocación no pisa el motivo");
    }

    [Fact]
    public async Task Un_choque_de_version_al_regenerar_o_revocar_responde_Conflicto_y_descarta_lo_pendiente()
    {
        var credencial = await EmitidaAsync();
        _unidad.ExcepcionAlGuardar = new DbUpdateConcurrencyException("versión");

        var regenerar = await Regenerar().Handle(new RegenerarCredencialCommand(credencial.Id), CancellationToken.None);
        var revocar = await Revocar().Handle(new RevocarCredencialCommand(credencial.Id, MotivoRevocacionCredencial.Otro), CancellationToken.None);

        regenerar.Error.Should().Be(CredencialAccesoErrores.Conflicto);
        revocar.Error.Should().Be(CredencialAccesoErrores.Conflicto);
        _descarte.Descartes.Should().Be(2);
    }

    [Fact]
    public async Task El_motivo_fuera_del_enum_cerrado_no_pasa_la_validacion()
    {
        var validador = new RevocarCredencialCommandValidator();

        validador.Validate(new RevocarCredencialCommand(Guid.NewGuid(), (MotivoRevocacionCredencial)99)).IsValid.Should().BeFalse();
        validador.Validate(new RevocarCredencialCommand(Guid.NewGuid(), MotivoRevocacionCredencial.Otro)).IsValid.Should().BeTrue();
        new EmitirCredencialCommandValidator().Validate(new EmitirCredencialCommand(Guid.Empty, Guid.NewGuid())).IsValid.Should().BeFalse();
        await Task.CompletedTask;
    }

    private sealed class ErrorDeBase(string sqlState) : System.Data.Common.DbException("error de base")
    {
        public override string? SqlState { get; } = sqlState;
    }

    private sealed class UnidadDeTrabajoFalsa : IUnitOfWork
    {
        public Exception? ExcepcionAlGuardar { get; set; }
        public int Guardados { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (ExcepcionAlGuardar is { } excepcion)
                throw excepcion;
            Guardados++;
            return Task.FromResult(1);
        }
    }

    private sealed class DescarteFalso : IDescarteCambiosPendientes
    {
        public int Descartes { get; private set; }

        public void DescartarCambiosPendientes() => Descartes++;
    }

    private sealed class RepositorioFalso : ICredencialAccesoRepository
    {
        public List<CredencialAcceso> Credenciales { get; } = [];

        public Task<CredencialAcceso?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Credenciales.FirstOrDefault(c => c.Id == id));

        public Task<CredencialAcceso?> ObtenerEmitidaDelTrabajadorAsync(
            Guid tenantOrigenId, Guid trabajadorOrigenId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Credenciales.FirstOrDefault(c =>
                c.TenantOrigenId == tenantOrigenId && c.TrabajadorOrigenId == trabajadorOrigenId
                && c.Estado == EstadoCredencialAcceso.Emitida));

        public void Agregar(CredencialAcceso credencial) => Credenciales.Add(credencial);
    }

    private sealed class EvidenciasFalsas(List<EvidenciaInbound> evidencias) : IEvidenciasInboundQueryContext
    {
        public IQueryable<EvidenciaInbound> EvidenciasInbound => new TestAsyncQueryable<EvidenciaInbound>(evidencias.AsQueryable());
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }

    private sealed class UsuarioFalso(Guid usuario) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuario == Guid.Empty ? null : usuario);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
