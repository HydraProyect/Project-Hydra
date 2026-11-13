using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento.AceptacionContractual;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Cumplimiento;

/// <summary>
/// #2009: registro de la aceptación contractual en el alta, catálogo de versiones, acuse con el texto
/// aceptado, y consulta y reenvío solo para el Administrador del Tenant propietario.
/// </summary>
public class AceptacionContractualTenantTests
{
    private static readonly DateTime Ahora = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime VigenciaV1 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime VigenciaV2 = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Administrador = Guid.NewGuid();
    private static readonly Guid Solicitud = Guid.NewGuid();

    private static readonly DocumentoContractualPublicado CondicionesV1 =
        new("Condiciones", "Condiciones de prueba", "1.0", VigenciaV1, ObligatorioEnAlta: true, "Texto de condiciones 1.0");
    private static readonly DocumentoContractualPublicado CondicionesV2 =
        new("Condiciones", "Condiciones de prueba", "2.0", VigenciaV2, ObligatorioEnAlta: true, "Texto de condiciones 2.0 <b>");
    private static readonly DocumentoContractualPublicado EncargoV1 =
        new("Encargo", "Encargo de prueba", "1.0", VigenciaV1, ObligatorioEnAlta: true, "Texto del encargo 1.0");
    private static readonly DocumentoContractualPublicado OpcionalV1 =
        new("Opcional", "Opcional de prueba", "1.0", VigenciaV1, ObligatorioEnAlta: false, "Texto opcional");

    private static AceptacionDeclarada Declarada(DocumentoContractualPublicado d, DateTime cuando, bool facultades = false, string? hash = null) =>
        new(d.Documento, d.Version, hash ?? d.HashSha256, facultades, "203.0.113.7", cuando);

    private static RegistrarAceptacionContractualAlta Datos(params AceptacionDeclarada[] aceptaciones) =>
        new(Tenant, Administrador, Solicitud, aceptaciones, 177.00m);

    private sealed class Escenario
    {
        public Guid? TenantDelAmbito { get; set; } = Tenant;
        public HashSet<Guid> Administradores { get; } = [Administrador];
        public List<DocumentoContractualPublicado> Catalogo { get; } = [CondicionesV1, EncargoV1, OpcionalV1];
        public RepositorioFalso Repositorio { get; } = new();

        public RegistroAceptacionContractualAlta Registro() => new(
            new TenantFijo(TenantDelAmbito), new DirectorioFalso(Administradores), new CatalogoFalso(Catalogo), Repositorio, new RelojFijo(Ahora));
    }

    [Fact]
    public async Task Registra_una_fila_por_documento_con_version_hash_facultades_ip_fecha_e_importe()
    {
        var e = new Escenario();
        var r = await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora.AddMinutes(-5), facultades: true), Declarada(EncargoV1, Ahora.AddMinutes(-5))));

        r.EsExitoso.Should().BeTrue();
        e.Repositorio.Aceptaciones.Should().HaveCount(2);
        var condiciones = e.Repositorio.Aceptaciones.Single(a => a.Documento == "Condiciones");
        condiciones.Version.Should().Be("1.0");
        condiciones.HashSha256.Should().Be(CondicionesV1.HashSha256);
        condiciones.DeclaraFacultades.Should().BeTrue();
        condiciones.Ip.Should().Be("203.0.113.7");
        condiciones.AceptadaEnUtc.Should().Be(Ahora.AddMinutes(-5));
        condiciones.RegistradaEnUtc.Should().Be(Ahora);
        condiciones.ImportePermanencia.Should().Be(177.00m);
        condiciones.UsuarioId.Should().Be(Administrador);
        condiciones.SolicitudAltaId.Should().Be(Solicitud);
    }

    [Fact]
    public async Task Rechaza_una_cuenta_que_no_es_Administrador_propio_del_Tenant_propietario()
    {
        // Una cuenta de un Operador CAE externo no es propia del Tenant propietario: el directorio no la
        // da por Administrador de ese Tenant, aunque lo sea de su organización.
        var e = new Escenario();
        e.Administradores.Clear();

        var r = await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));

        r.Error.Should().Be(ErroresAceptacionContractual.SoloAdministradorDelTenantPropietario);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Rechaza_si_nadie_declara_facultades()
    {
        var e = new Escenario();
        var r = await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora), Declarada(EncargoV1, Ahora)));

        r.Error.Should().Be(ErroresAceptacionContractual.SinDeclaracionFacultades);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rechaza_fuera_del_ambito_del_Tenant_que_se_da_de_alta(bool sinAmbito)
    {
        var e = new Escenario { TenantDelAmbito = sinAmbito ? null : Guid.NewGuid() };
        var r = await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));

        r.Error.Should().Be(ErroresAceptacionContractual.FueraDelTenant);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Rechaza_un_hash_que_no_es_el_del_texto_publicado()
    {
        var e = new Escenario();
        var r = await e.Registro().RegistrarAsync(Datos(
            Declarada(CondicionesV1, Ahora, facultades: true, hash: new string('a', 64)), Declarada(EncargoV1, Ahora)));

        r.Error.Should().Be(ErroresAceptacionContractual.VersionNoPublicada);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Compara_con_la_version_vigente_al_aceptar_y_no_con_la_de_hoy()
    {
        var e = new Escenario();
        e.Catalogo.Add(CondicionesV2);
        var antesDeV2 = VigenciaV2.AddHours(-1);

        var conV1 = await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, antesDeV2, facultades: true), Declarada(EncargoV1, antesDeV2)));
        conV1.EsExitoso.Should().BeTrue("la 1.0 regía cuando se aceptó, aunque hoy rija la 2.0");

        var otra = new Escenario();
        otra.Catalogo.Add(CondicionesV2);
        var v1TrasV2 = await otra.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));
        v1TrasV2.Error.Should().Be(ErroresAceptacionContractual.VersionNoPublicada, "la 1.0 ya no regía cuando se aceptó");
    }

    [Fact]
    public async Task Rechaza_si_falta_un_documento_obligatorio_y_admite_que_falte_uno_opcional()
    {
        var e = new Escenario();
        var sinEncargo = await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true)));
        sinEncargo.Error.Should().Be(ErroresAceptacionContractual.FaltaDocumentoObligatorio);

        var sinOpcional = await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));
        sinOpcional.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Con_el_catalogo_vacio_ninguna_aceptacion_se_registra()
    {
        var e = new Escenario();
        e.Catalogo.Clear();

        var r = await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true)));

        r.Error.Should().Be(ErroresAceptacionContractual.VersionNoPublicada);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Rechaza_documentos_repetidos_listas_vacias_y_fechas_futuras()
    {
        var e = new Escenario();
        (await e.Registro().RegistrarAsync(Datos())).Error.Should().Be(ErroresAceptacionContractual.SinAceptaciones);
        (await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(CondicionesV1, Ahora))))
            .Error.Should().Be(ErroresAceptacionContractual.DocumentoRepetido);
        (await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora.AddSeconds(1), facultades: true), Declarada(EncargoV1, Ahora))))
            .Error.Should().Be(ErroresAceptacionContractual.AceptacionFutura);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Rechaza_un_importe_de_permanencia_negativo()
    {
        var e = new Escenario();
        var r = await e.Registro().RegistrarAsync(
            Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)) with { ImportePermanencia = -1m });

        r.Error.Should().Be(ErroresAceptacionContractual.DatoNoValido);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Repetir_el_registro_de_la_misma_Solicitud_de_alta_no_duplica_filas()
    {
        var e = new Escenario();
        var datos = Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora));

        (await e.Registro().RegistrarAsync(datos)).EsExitoso.Should().BeTrue();
        (await e.Registro().RegistrarAsync(datos)).EsExitoso.Should().BeTrue();

        e.Repositorio.Aceptaciones.Should().HaveCount(2);
    }

    [Fact]
    public void El_hash_es_el_SHA256_hexadecimal_en_minusculas_del_texto_en_UTF8()
    {
        // SHA-256 de "abc", vector conocido de FIPS 180-2.
        DocumentoContractualPublicado.CalcularHash("abc")
            .Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void Un_catalogo_ambiguo_no_se_acepta()
    {
        var duplicada = () => ResolucionDocumentosContractuales.ExigirCoherente([CondicionesV1, CondicionesV1 with { Texto = "otro" }]);
        duplicada.Should().Throw<InvalidOperationException>();

        var mismoInicio = () => ResolucionDocumentosContractuales.ExigirCoherente([CondicionesV1, CondicionesV2 with { VigenteDesdeUtc = VigenciaV1 }]);
        mismoInicio.Should().Throw<InvalidOperationException>();

        var coherente = () => ResolucionDocumentosContractuales.ExigirCoherente([CondicionesV1, CondicionesV2, EncargoV1]);
        coherente.Should().NotThrow();
    }

    [Fact]
    public async Task El_acuse_envia_el_texto_aceptado_de_cada_documento_y_lo_deja_registrado()
    {
        var e = new Escenario();
        e.Catalogo.Add(CondicionesV2);
        await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV2, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));
        var correo = new CorreoFalso();
        var unidad = new UnidadFalsa();

        var r = await new EnvioAcuseAceptacionContractual(
                e.Repositorio, new CatalogoFalso(e.Catalogo), new CuentasFalsas(Administrador, propia: true), correo, unidad, new RelojFijo(Ahora))
            .EnviarAsync(Administrador);

        r.EsExitoso.Should().BeTrue();
        var enviado = correo.Enviados.Should().ContainSingle().Subject;
        enviado.Destinatario.Should().Be("admin@ejemplo.test");
        enviado.Cuerpo.Should().Contain("Texto de condiciones 2.0 &lt;b&gt;", "el texto aceptado va completo y escapado")
            .And.Contain("Texto del encargo 1.0")
            .And.Contain(CondicionesV2.HashSha256)
            .And.NotContain("Texto de condiciones 1.0");
        e.Repositorio.Acuses.Should().HaveCount(2).And.OnlyContain(a => a.UsuarioDestinatarioId == Administrador && a.EnviadoEnUtc == Ahora);
        unidad.Guardados.Should().Be(1);
    }

    [Fact]
    public async Task El_acuse_no_se_registra_si_el_correo_no_sale_ni_se_envia_a_una_cuenta_ajena()
    {
        var e = new Escenario();
        await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));

        var fallido = await new EnvioAcuseAceptacionContractual(
                e.Repositorio, new CatalogoFalso(e.Catalogo), new CuentasFalsas(Administrador, propia: true), new CorreoFalso(falla: true),
                new UnidadFalsa(), new RelojFijo(Ahora))
            .EnviarAsync(Administrador);
        fallido.EsFallido.Should().BeTrue();

        var correo = new CorreoFalso();
        var ajena = await new EnvioAcuseAceptacionContractual(
                e.Repositorio, new CatalogoFalso(e.Catalogo), new CuentasFalsas(Administrador, propia: false), correo,
                new UnidadFalsa(), new RelojFijo(Ahora))
            .EnviarAsync(Administrador);
        ajena.Error.Should().Be(EnvioAcuseAceptacionContractual.CuentaNoValida);
        correo.Enviados.Should().BeEmpty();

        e.Repositorio.Acuses.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Administrador", true, true)]
    [InlineData("Administrador", false, false)]
    [InlineData("GestorCae", true, false)]
    public async Task Consulta_y_reenvio_solo_para_el_Administrador_con_el_Tenant_propietario_como_origen(
        string rol, bool origenEsElTenant, bool permitido)
    {
        var e = new Escenario();
        await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));
        var usuario = new UsuarioActualFalso(rol, Administrador, origenEsElTenant ? Tenant : Guid.NewGuid());
        var tenant = new TenantFijo(Tenant);

        var consulta = await new ObtenerAceptacionesContractualesTenantQueryHandler(usuario, tenant, e.Repositorio)
            .Handle(new ObtenerAceptacionesContractualesTenantQuery(), CancellationToken.None);
        var envio = new EnvioFalso();
        var reenvio = await new ReenviarAcuseAceptacionContractualCommandHandler(usuario, tenant, envio)
            .Handle(new ReenviarAcuseAceptacionContractualCommand(), CancellationToken.None);

        consulta.EsExitoso.Should().Be(permitido);
        reenvio.EsExitoso.Should().Be(permitido);
        envio.Destinatarios.Should().HaveCount(permitido ? 1 : 0);
        if (permitido)
        {
            consulta.Valor.Should().HaveCount(2);
            envio.Destinatarios.Should().Equal(Administrador);
        }
    }

    private sealed class RepositorioFalso : IAceptacionContractualTenantRepository
    {
        public List<AceptacionContractualTenant> Aceptaciones { get; } = [];
        public List<AcuseAceptacionContractualTenant> Acuses { get; } = [];

        public void Agregar(AceptacionContractualTenant aceptacion) => Aceptaciones.Add(aceptacion);
        public void AgregarAcuse(AcuseAceptacionContractualTenant acuse) => Acuses.Add(acuse);

        public Task<bool> ExistenDeSolicitudAltaAsync(Guid solicitudAltaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Aceptaciones.Any(a => a.SolicitudAltaId == solicitudAltaId));

        public Task<IReadOnlyList<AceptacionContractualTenant>> ListarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AceptacionContractualTenant>>(
                Aceptaciones.OrderByDescending(a => a.AceptadaEnUtc).ThenByDescending(a => a.RegistradaEnUtc).ToList());

        public Task<IReadOnlyDictionary<Guid, DateTime>> UltimoAcusePorAceptacionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, DateTime>>(
                Acuses.GroupBy(a => a.AceptacionContractualTenantId).ToDictionary(g => g.Key, g => g.Max(a => a.EnviadoEnUtc)));
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

    private sealed class DirectorioFalso(HashSet<Guid> administradores) : IDirectorioUsuariosService
    {
        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<bool> EsVisibleEnTenantActualAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        public Task<bool> EsCuentaActivaConRolAsync(Guid id, Guid tenant, string rol, CancellationToken ct = default) =>
            Task.FromResult(tenant == Tenant && rol == "Administrador" && administradores.Contains(id));
        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(Guid tenant, string rol, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class UsuarioActualFalso(string rol, Guid usuarioId, Guid tenantOrigen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class CuentasFalsas(Guid usuarioId, bool propia) : IGestionCuentasUsuario
    {
        public Task<CuentaUsuario?> ObtenerAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<CuentaUsuario?>(id == usuarioId
                ? new CuentaUsuario(id, "admin@ejemplo.test", "Administrador de prueba", propia, ["Administrador"], false, true, false)
                : null);

        public Task<bool> EsPropiaDelTenantActualAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TieneVinculoOperativoAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<Guid>> CrearAsync(NuevaCuentaUsuario cuenta, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> AsignarRolAsync(Guid id, string rol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ActualizarDatosAsync(Guid id, DatosCuentaUsuario datos, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResultadoCambioRol> CambiarRolAsync(Guid id, string rolNuevo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> CambiarActivacionAsync(Guid id, bool activar, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> EliminarAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<string>> GenerarTokenActivacionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<string>> CorregirCorreoPendienteAsync(Guid id, string correoNuevo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CorreoFalso(bool falla = false) : IEmailService
    {
        public List<(string Destinatario, string Asunto, string Cuerpo)> Enviados { get; } = [];

        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            if (falla)
                return Task.FromResult(Result.Fallo(Error.Crear("Correo.Fallo", "No salió.")));
            Enviados.Add((destinatarioEmail, asunto, cuerpoHtml));
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class UnidadFalsa : IUnitOfWork
    {
        public int Guardados { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Guardados++;
            return Task.FromResult(0);
        }
    }

    private sealed class EnvioFalso : IEnvioAcuseAceptacionContractual
    {
        public List<Guid> Destinatarios { get; } = [];

        public Task<Result> EnviarAsync(Guid usuarioDestinatarioId, CancellationToken cancellationToken = default)
        {
            Destinatarios.Add(usuarioDestinatarioId);
            return Task.FromResult(Result.Exito());
        }
    }
}
