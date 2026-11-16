using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento.AceptacionContractual;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Cumplimiento;

/// <summary>
/// #2009, decisión D-6: el Administrador del Tenant propietario acepta fuera del alta las versiones vigentes de los
/// documentos obligatorios, y mientras tenga alguna pendiente la consulta se la devuelve (el gate lo bloquea). Nadie
/// más queda bloqueado ni puede aceptar. Catálogo de prueba: el real está vacío a propósito.
/// </summary>
public class AceptacionContractualFueraDelAltaTests
{
    private static readonly DateTime Ahora = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime VigenciaV1 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime VigenciaV2 = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
    private const string Ip = "203.0.113.9";

    private static readonly DocumentoContractualPublicado CondicionesV1 =
        new("Condiciones", "Condiciones de prueba", "1.0", VigenciaV1, ObligatorioEnAlta: true, "Texto de condiciones 1.0");
    private static readonly DocumentoContractualPublicado CondicionesV2 =
        new("Condiciones", "Condiciones de prueba", "2.0", VigenciaV2, ObligatorioEnAlta: true, "Texto de condiciones 2.0");
    private static readonly DocumentoContractualPublicado EncargoV1 =
        new("Encargo", "Encargo de prueba", "1.0", VigenciaV1, ObligatorioEnAlta: true, "Texto del encargo 1.0");
    private static readonly DocumentoContractualPublicado OpcionalV1 =
        new("Opcional", "Opcional de prueba", "1.0", VigenciaV1, ObligatorioEnAlta: false, "Texto opcional");
    private static readonly DocumentoContractualPublicado FuturoV3 =
        new("Condiciones", "Condiciones de prueba", "3.0", Ahora.AddDays(5), ObligatorioEnAlta: true, "Texto de condiciones 3.0");

    private sealed class Escenario
    {
        public Tenant Tenant { get; } = new("Tenant propietario de prueba");
        public Guid Administrador { get; } = Guid.NewGuid();
        public string? Rol { get; set; } = "Administrador";
        public Guid? TenantOrigen { get; set; }
        public Guid? ActorReal { get; set; }
        public Guid? UsuarioSimulado { get; set; }
        public SesionPrivilegiadaActiva? Sesion { get; set; }
        public bool CuentaPropiaActiva { get; set; } = true;
        public List<DocumentoContractualPublicado> Catalogo { get; } = [CondicionesV1, EncargoV1, OpcionalV1];
        public RepositorioFalso Repositorio { get; } = new();
        public EnvioFalso Envio { get; } = new();
        public UnidadFalsa Unidad { get; }
        public List<string> Bitacora { get; } = [];
        public TenantsQueryContextFalso Tenants { get; } = new();

        public Escenario()
        {
            Unidad = new UnidadFalsa(Repositorio, Bitacora);
            Envio.Bitacora = Bitacora;
            Tenants.ListaTenants.Add(Tenant);
        }

        private UsuarioActualFalso Usuario() => new(Rol, Administrador, TenantOrigen ?? Tenant.Id);
        private ActorFalso Actor() => new(new ActorAuditoria(ActorReal ?? Administrador, UsuarioSimulado, TipoViaAcceso.Normal, null));
        private DirectorioFalso Directorio() => new(Tenant.Id, CuentaPropiaActiva ? [Administrador] : []);

        public ObtenerAceptacionesContractualesPendientesQueryHandler Consulta() => new(
            Usuario(), new TenantFijo(Tenant.Id), new SesionFalsa(Sesion), Actor(), Directorio(), Tenants,
            new CatalogoFalso(Catalogo), Repositorio, new RelojFijo(Ahora));

        public AceptarDocumentosContractualesCommandHandler Aceptar() => new(
            Usuario(), new TenantFijo(Tenant.Id), new SesionFalsa(Sesion), Actor(), Directorio(), Tenants,
            new CatalogoFalso(Catalogo), Repositorio, Unidad, Envio, new RelojFijo(Ahora),
            NullLogger<AceptarDocumentosContractualesCommandHandler>.Instance);

        public async Task<IReadOnlyList<DocumentoContractualPendienteDto>> PendientesAsync() =>
            await Consulta().Handle(new ObtenerAceptacionesContractualesPendientesQuery(), CancellationToken.None);

        public Task<Result> AceptarAsync(bool facultades = true, string? ip = Ip, params DocumentoContractualPublicado[] documentos) =>
            Aceptar().Handle(new AceptarDocumentosContractualesCommand(
                documentos.Select(d => new DocumentoContractualAceptado(d.Documento, d.Version, d.HashSha256)).ToList(), facultades, ip),
                CancellationToken.None);
    }

    [Fact]
    public async Task El_Administrador_con_documentos_pendientes_los_tiene_que_aceptar()
    {
        var e = new Escenario();

        var pendientes = await e.PendientesAsync();

        pendientes.Should().Equal(
            new DocumentoContractualPendienteDto("Condiciones", "Condiciones de prueba", "1.0", CondicionesV1.HashSha256),
            new DocumentoContractualPendienteDto("Encargo", "Encargo de prueba", "1.0", EncargoV1.HashSha256));
    }

    [Fact]
    public async Task Con_el_catalogo_vacio_no_se_bloquea_a_nadie_ni_se_puede_aceptar()
    {
        var e = new Escenario();
        e.Catalogo.Clear();

        (await e.PendientesAsync()).Should().BeEmpty("hoy el catálogo real no publica nada y no debe bloquear a nadie");
        (await e.AceptarAsync(documentos: [CondicionesV1])).Error.Should().Be(ErroresAceptacionContractual.VersionNoPublicada);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    public static TheoryData<string> CuentasQueNoAceptan => new()
    {
        "otro-rol", "gestor-cae-externo", "administrador-operador-externo", "impersonacion", "actor-real-distinto",
        "sesion-privilegiada", "cuenta-no-propia", "tenant-plataforma",
    };

    private static void Configurar(Escenario e, string cuenta)
    {
        switch (cuenta)
        {
            case "otro-rol": e.Rol = "CoordinadorCae"; break;
            case "gestor-cae-externo": e.Rol = "GestorCae"; e.TenantOrigen = Guid.NewGuid(); break;
            case "administrador-operador-externo": e.TenantOrigen = Guid.NewGuid(); break;
            case "impersonacion": e.UsuarioSimulado = e.Administrador; e.ActorReal = Guid.NewGuid(); break;
            case "actor-real-distinto": e.ActorReal = Guid.NewGuid(); break;
            case "sesion-privilegiada":
                e.Rol = null;
                e.Sesion = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), e.Tenant.Id, CapacidadPrivilegio.SoporteLectura, null);
                break;
            case "cuenta-no-propia": e.CuentaPropiaActiva = false; break;
            case "tenant-plataforma": e.Tenant.MarcarComoPlataforma(); break;
            default: throw new ArgumentOutOfRangeException(nameof(cuenta));
        }
    }

    [Theory]
    [MemberData(nameof(CuentasQueNoAceptan))]
    public async Task Ninguna_otra_cuenta_queda_bloqueada_ni_puede_aceptar(string cuenta)
    {
        var e = new Escenario();
        Configurar(e, cuenta);

        (await e.PendientesAsync()).Should().BeEmpty($"«{cuenta}» no es quien acepta: no se le bloquea");
        var resultado = await e.AceptarAsync(documentos: [CondicionesV1, EncargoV1]);

        resultado.Error.Should().Be(ErroresAceptacionContractual.SoloAdministradorDelTenantPropietario);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
        e.Envio.Destinatarios.Should().BeEmpty();
    }

    [Fact]
    public async Task La_impersonacion_con_Sesion_Privilegiada_tampoco_bloquea_ni_acepta()
    {
        var e = new Escenario();
        e.Sesion = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), e.Tenant.Id, CapacidadPrivilegio.Impersonacion, e.Administrador);

        (await e.PendientesAsync()).Should().BeEmpty();
        (await e.AceptarAsync(documentos: [CondicionesV1, EncargoV1])).EsFallido.Should().BeTrue();
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Aceptar_registra_una_fila_por_documento_fuera_del_alta_y_desbloquea()
    {
        var e = new Escenario();

        var resultado = await e.AceptarAsync(documentos: [CondicionesV1, EncargoV1]);

        resultado.EsExitoso.Should().BeTrue();
        e.Repositorio.Aceptaciones.Should().HaveCount(2).And.OnlyContain(a =>
            a.UsuarioId == e.Administrador && a.SolicitudAltaId == null && a.DeclaraFacultades && a.Ip == Ip
            && a.AceptadaEnUtc == Ahora && a.RegistradaEnUtc == Ahora && a.ImportePermanencia == null);
        e.Repositorio.Aceptaciones.Select(a => (a.Documento, a.Version, a.HashSha256)).Should().BeEquivalentTo(
            [("Condiciones", "1.0", CondicionesV1.HashSha256), ("Encargo", "1.0", EncargoV1.HashSha256)]);
        (await e.PendientesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task El_acuse_sale_despues_del_guardado_de_la_aceptacion()
    {
        var e = new Escenario();

        await e.AceptarAsync(documentos: [CondicionesV1, EncargoV1]);

        e.Bitacora.Should().Equal("guardado: 2 aceptaciones", "acuse");
        e.Envio.Destinatarios.Should().Equal(e.Administrador);
    }

    [Fact]
    public async Task Si_el_guardado_falla_no_sale_ningun_acuse()
    {
        var e = new Escenario();
        e.Unidad.Falla = true;

        var aceptar = () => e.AceptarAsync(documentos: [CondicionesV1, EncargoV1]);

        await aceptar.Should().ThrowAsync<InvalidOperationException>();
        e.Envio.Destinatarios.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_acuse_que_falla_no_deshace_lo_aceptado()
    {
        var e = new Escenario();
        e.Envio.Lanza = true;

        var resultado = await e.AceptarAsync(documentos: [CondicionesV1, EncargoV1]);

        resultado.EsExitoso.Should().BeTrue("la aceptación ya está guardada; el acuse se puede reenviar");
        e.Repositorio.Aceptaciones.Should().HaveCount(2);
    }

    [Fact]
    public async Task Una_aceptacion_parcial_se_rechaza()
    {
        var e = new Escenario();

        (await e.AceptarAsync(documentos: [CondicionesV1])).Error.Should().Be(ErroresAceptacionContractual.FaltaDocumentoObligatorio);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_declarar_facultades_se_rechaza()
    {
        var e = new Escenario();

        (await e.AceptarAsync(facultades: false, documentos: [CondicionesV1, EncargoV1]))
            .Error.Should().Be(ErroresAceptacionContractual.SinDeclaracionFacultades);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_IP_de_origen_se_rechaza()
    {
        var e = new Escenario();

        (await e.AceptarAsync(ip: null, documentos: [CondicionesV1, EncargoV1]))
            .Error.Should().Be(AceptarDocumentosContractualesCommandHandler.SinIpDeOrigen);
        (await e.AceptarAsync(ip: "no-es-una-ip", documentos: [CondicionesV1, EncargoV1]))
            .Error.Should().Be(ErroresAceptacionContractual.DatoNoValido);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_version_o_un_hash_que_no_son_los_vigentes_se_rechazan()
    {
        var e = new Escenario();
        e.Catalogo.Add(CondicionesV2);
        e.Catalogo.Add(FuturoV3);

        (await e.AceptarAsync(documentos: [CondicionesV1, EncargoV1])).Error.Should().Be(
            ErroresAceptacionContractual.VersionNoPublicada, "la 1.0 ya no rige: rige la 2.0");
        (await e.AceptarAsync(documentos: [FuturoV3, EncargoV1])).Error.Should().Be(
            ErroresAceptacionContractual.VersionNoPublicada, "la 3.0 aún no rige");
        (await e.AceptarAsync(documentos: [OpcionalV1, CondicionesV2, EncargoV1])).Error.Should().Be(
            ErroresAceptacionContractual.VersionNoPublicada, "solo se aceptan aquí los obligatorios");

        var hashAjeno = await e.Aceptar().Handle(new AceptarDocumentosContractualesCommand(
            [new("Condiciones", "2.0", new string('a', 64)), new("Encargo", "1.0", EncargoV1.HashSha256)], true, Ip), CancellationToken.None);
        hashAjeno.Error.Should().Be(ErroresAceptacionContractual.VersionNoPublicada);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_documento_repetido_se_rechaza()
    {
        var e = new Escenario();

        (await e.AceptarAsync(documentos: [CondicionesV1, CondicionesV1, EncargoV1]))
            .Error.Should().Be(ErroresAceptacionContractual.DocumentoRepetido);
    }

    [Fact]
    public async Task Repetir_la_aceptacion_no_duplica_filas_ni_acuses()
    {
        var e = new Escenario();
        await e.AceptarAsync(documentos: [CondicionesV1, EncargoV1]);

        var otraVez = await e.AceptarAsync(documentos: [CondicionesV1, EncargoV1]);

        otraVez.EsExitoso.Should().BeTrue();
        e.Repositorio.Aceptaciones.Should().HaveCount(2, "idempotente por (Tenant, cuenta, documento, versión, hash)");
        e.Envio.Destinatarios.Should().HaveCount(1);
    }

    [Fact]
    public async Task Una_version_nueva_vuelve_a_bloquear_solo_por_ese_documento()
    {
        var e = new Escenario();
        await e.AceptarAsync(documentos: [CondicionesV1, EncargoV1]);
        e.Catalogo.Add(CondicionesV2);

        (await e.PendientesAsync()).Select(p => (p.Documento, p.Version)).Should().Equal([("Condiciones", "2.0")]);

        (await e.AceptarAsync(documentos: [CondicionesV2])).EsExitoso.Should().BeTrue();
        (await e.PendientesAsync()).Should().BeEmpty();
        e.Repositorio.Aceptaciones.Should().HaveCount(3);
    }

    [Fact]
    public async Task Corregir_el_texto_de_una_version_sin_cambiar_su_numero_obliga_a_aceptarla_otra_vez()
    {
        var e = new Escenario();
        await e.AceptarAsync(documentos: [CondicionesV1, EncargoV1]);
        var corregida = CondicionesV1 with { Texto = "Texto de condiciones 1.0, corregido" };
        e.Catalogo.Remove(CondicionesV1);
        e.Catalogo.Add(corregida);
        corregida.HashSha256.Should().NotBe(CondicionesV1.HashSha256);
        FluentActions.Invoking(() => ResolucionDocumentosContractuales.ExigirCoherente(e.Catalogo))
            .Should().NotThrow("el catálogo publica una sola vez la versión 1.0, ahora con el texto corregido");

        (await e.PendientesAsync()).Select(p => (p.Documento, p.Version, p.HashSha256))
            .Should().Equal([("Condiciones", "1.0", corregida.HashSha256)]);

        (await e.AceptarAsync(documentos: [corregida])).EsExitoso.Should().BeTrue();
        e.Repositorio.Aceptaciones.Should().HaveCount(3, "la idempotencia incluye el hash: el texto corregido es otra aceptación");
        e.Repositorio.Aceptaciones.Should().ContainSingle(a => a.Documento == "Condiciones" && a.HashSha256 == corregida.HashSha256);
        (await e.PendientesAsync()).Should().BeEmpty();

        (await e.AceptarAsync(documentos: [corregida])).EsExitoso.Should().BeTrue();
        e.Repositorio.Aceptaciones.Should().HaveCount(3, "repetir la aceptación del texto corregido ya no registra nada");
    }

    [Fact]
    public async Task Lo_aceptado_en_el_alta_cuenta_y_no_vuelve_a_bloquear()
    {
        var e = new Escenario();
        e.Repositorio.Aceptaciones.Add(new AceptacionContractualTenant(Guid.NewGuid(), Guid.NewGuid(),
            new AceptacionDeclarada("Condiciones", "1.0", CondicionesV1.HashSha256, true, Ip, VigenciaV1.AddDays(1)), VigenciaV1.AddDays(1)));

        (await e.PendientesAsync()).Select(p => p.Documento).Should().Equal(["Encargo"],
            "lo aceptado obliga a la organización, sea quien sea quien lo aceptó y por donde");
    }

    [Fact]
    public async Task El_texto_de_una_version_que_ya_rige_se_puede_leer_y_el_de_una_programada_no()
    {
        var catalogo = new CatalogoFalso([CondicionesV1, CondicionesV2, FuturoV3]);
        var handler = new ObtenerTextoDocumentoContractualQueryHandler(catalogo, new RelojFijo(Ahora));

        var texto = await handler.Handle(new ObtenerTextoDocumentoContractualQuery("Condiciones", "1.0"), CancellationToken.None);
        var ausente = await handler.Handle(new ObtenerTextoDocumentoContractualQuery("Condiciones", "9.9"), CancellationToken.None);
        var programada = await handler.Handle(new ObtenerTextoDocumentoContractualQuery("Condiciones", "3.0"), CancellationToken.None);

        texto.Should().Be(new TextoDocumentoContractualDto("Condiciones", "Condiciones de prueba", "1.0", VigenciaV1, "Texto de condiciones 1.0"));
        ausente.Should().BeNull();
        programada.Should().BeNull("una versión programada no se enseña antes de la fecha en que rige");
    }

    private sealed class RepositorioFalso : IAceptacionContractualTenantRepository
    {
        public List<AceptacionContractualTenant> Aceptaciones { get; } = [];
        public List<AceptacionContractualTenant> SinGuardar { get; } = [];

        public void Agregar(AceptacionContractualTenant aceptacion) => SinGuardar.Add(aceptacion);
        public void AgregarAcuse(AcuseAceptacionContractualTenant acuse) => throw new NotSupportedException();
        public bool HayTransaccionAbierta => false;
        public bool HayCambiosPendientes => SinGuardar.Count > 0;

        public Task<bool> ExistenDeSolicitudAltaAsync(Guid solicitudAltaId, Guid usuarioId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AceptacionContractualTenant>> ListarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AceptacionContractualTenant>>(Aceptaciones.OrderByDescending(a => a.AceptadaEnUtc).ToList());

        public Task<IReadOnlyDictionary<Guid, DateTime>> UltimoAcusePorAceptacionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Confirma lo añadido al repositorio al guardar y lo anota en la bitácora; con <see cref="Falla"/>, lanza sin confirmar.</summary>
    private sealed class UnidadFalsa(RepositorioFalso repositorio, List<string> bitacora) : IUnitOfWork
    {
        public bool Falla { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (Falla)
                throw new InvalidOperationException("Fallo simulado de la base de datos.");
            bitacora.Add($"guardado: {repositorio.SinGuardar.Count} aceptaciones");
            repositorio.Aceptaciones.AddRange(repositorio.SinGuardar);
            var guardadas = repositorio.SinGuardar.Count;
            repositorio.SinGuardar.Clear();
            return Task.FromResult(guardadas);
        }
    }

    private sealed class EnvioFalso : IEnvioAcuseAceptacionContractual
    {
        public List<Guid> Destinatarios { get; } = [];
        public List<string>? Bitacora { get; set; }
        public bool Lanza { get; set; }

        public Task<Result> EnviarAsync(Guid usuarioDestinatarioId, CancellationToken cancellationToken = default)
        {
            Bitacora?.Add("acuse");
            if (Lanza)
                throw new InvalidOperationException("Fallo simulado al preparar el acuse.");
            Destinatarios.Add(usuarioDestinatarioId);
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class CatalogoFalso(IReadOnlyList<DocumentoContractualPublicado> publicados) : ICatalogoDocumentosContractuales
    {
        public IReadOnlyList<DocumentoContractualPublicado> Publicados => publicados;
    }

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class RelojFijo(DateTime ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahora);
    }

    private sealed class SesionFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }

    private sealed class ActorFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class DirectorioFalso(Guid tenant, HashSet<Guid> administradores) : IDirectorioUsuariosService
    {
        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<bool> EsVisibleEnTenantActualAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        public Task<bool> EsCuentaActivaConRolAsync(Guid id, Guid tenantId, string rol, CancellationToken ct = default) =>
            Task.FromResult(tenantId == tenant && rol == "Administrador" && administradores.Contains(id));
        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(Guid tenantId, string rol, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class UsuarioActualFalso(string? rol, Guid usuarioId, Guid tenantOrigen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
