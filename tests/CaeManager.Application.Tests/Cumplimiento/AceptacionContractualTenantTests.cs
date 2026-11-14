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
    private const string CorreoAdministrador = "admin@ejemplo.test";

    private static readonly DocumentoContractualPublicado CondicionesV1 =
        new("Condiciones", "Condiciones de prueba", "1.0", VigenciaV1, ObligatorioEnAlta: true, "Texto de condiciones 1.0");
    private static readonly DocumentoContractualPublicado CondicionesV2 =
        new("Condiciones", "Condiciones de prueba", "2.0", VigenciaV2, ObligatorioEnAlta: true, "Texto de condiciones 2.0 <b>");
    private static readonly DocumentoContractualPublicado EncargoV1 =
        new("Encargo", "Encargo de prueba", "1.0", VigenciaV1, ObligatorioEnAlta: true, "Texto del encargo 1.0");
    private static readonly DocumentoContractualPublicado OpcionalV1 =
        new("Opcional", "Opcional de prueba", "1.0", VigenciaV1, ObligatorioEnAlta: false, "Texto opcional");

    private static AceptacionDeclarada Declarada(DocumentoContractualPublicado d, DateTime cuando, bool facultades = false, string? hash = null) =>
        new(d.Documento, d.Version, hash ?? d.HashSha256, facultades, "203.0.113.7", cuando, 177.00m);

    private static RegistrarAceptacionContractualAlta Datos(params AceptacionDeclarada[] aceptaciones) =>
        new(Tenant, Administrador, SolicitudConAceptaciones(aceptaciones));

    /// <summary>
    /// Una Solicitud de alta real, pagada: las aceptaciones entran por el agregado, que ya impide documentos
    /// repetidos, la falta de declaración de facultades y las fechas posteriores al paso a pago. El correo va
    /// con mayúsculas para probar que se compara normalizado.
    /// </summary>
    private static SolicitudAlta SolicitudConAceptaciones(
        IReadOnlyCollection<AceptacionDeclarada> aceptaciones, bool pagada = true, Guid? completadaPara = null)
    {
        var inicio = Ahora.AddDays(-2);
        var token = new string('t', 43);
        var solicitud = SolicitudAlta.Iniciar("Admin@Ejemplo.test", "hash", token, "203.0.113.7", "Navegador", inicio);
        solicitud.VerificarCorreo(token, inicio.AddHours(1)).Should().BeTrue();
        if (aceptaciones.Count == 0)
            return solicitud;
        solicitud.CompletarDatos("Org", "B12345674", "Calle 1", TramoTrabajadoresActivos.De1A10, true, false, inicio.AddHours(1));
        solicitud.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual, aceptaciones, "cs_test_2009", Ahora);
        if (!pagada)
            return solicitud;
        solicitud.MarcarPagada("cs_test_2009", "cus_test_2009", "sub_test_2009", Ahora);
        if (completadaPara is { } tenantCreado)
            solicitud.Completar(tenantCreado, Ahora);
        return solicitud;
    }

    private sealed class Escenario
    {
        public Guid? TenantDelAmbito { get; set; } = Tenant;
        public HashSet<Guid> Administradores { get; } = [Administrador];
        public string CorreoCuenta { get; set; } = CorreoAdministrador;
        public List<DocumentoContractualPublicado> Catalogo { get; } = [CondicionesV1, EncargoV1, OpcionalV1];
        public RepositorioFalso Repositorio { get; } = new();

        public RegistroAceptacionContractualAlta Registro() => new(
            new TenantFijo(TenantDelAmbito), new DirectorioFalso(Administradores),
            new CorreoDeCuentaFalso(Administrador, CorreoCuenta), new CatalogoFalso(Catalogo), Repositorio, new RelojFijo(Ahora));
    }

    [Fact]
    public async Task Una_Solicitud_de_alta_completada_para_otro_Tenant_no_deja_sus_aceptaciones_en_este()
    {
        var e = new Escenario();
        var solicitud = SolicitudConAceptaciones(
            [Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)], completadaPara: Guid.NewGuid());

        var r = await e.Registro().RegistrarAsync(new RegistrarAceptacionContractualAlta(Tenant, Administrador, solicitud));

        r.Error.Should().Be(ErroresAceptacionContractual.FueraDelTenant);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Solicitud_de_alta_completada_para_este_Tenant_se_registra()
    {
        var e = new Escenario();
        var solicitud = SolicitudConAceptaciones(
            [Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)], completadaPara: Tenant);

        var r = await e.Registro().RegistrarAsync(new RegistrarAceptacionContractualAlta(Tenant, Administrador, solicitud));

        r.EsExitoso.Should().BeTrue();
        e.Repositorio.Aceptaciones.Should().HaveCount(2);
    }

    [Fact]
    public async Task Una_Solicitud_de_alta_sin_pagar_no_deja_evidencia_contractual()
    {
        var e = new Escenario();
        var solicitud = SolicitudConAceptaciones(
            [Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)], pagada: false);

        var r = await e.Registro().RegistrarAsync(new RegistrarAceptacionContractualAlta(Tenant, Administrador, solicitud));

        r.Error.Should().Be(ErroresAceptacionContractual.SolicitudNoPagada);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Las_aceptaciones_pendientes_de_otra_cuenta_no_dan_por_registrada_esta()
    {
        var e = new Escenario();
        var datos = Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora));
        e.Repositorio.Agregar(new AceptacionContractualTenant(
            Guid.NewGuid(), datos.Solicitud.Id, Declarada(CondicionesV1, Ahora, facultades: true), Ahora));

        var r = await e.Registro().RegistrarAsync(datos);

        r.EsExitoso.Should().BeTrue();
        e.Repositorio.Aceptaciones.Count(a => a.UsuarioId == Administrador).Should().Be(2);
    }

    [Fact]
    public async Task Otra_cuenta_no_recibe_exito_por_lo_ya_registrado_para_quien_acepto()
    {
        var e = new Escenario();
        var datos = Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora));
        (await e.Registro().RegistrarAsync(datos)).EsExitoso.Should().BeTrue();
        var otraCuenta = Guid.NewGuid();
        e.Administradores.Add(otraCuenta);

        var r = await e.Registro().RegistrarAsync(datos with { UsuarioId = otraCuenta });

        r.Error.Should().Be(ErroresAceptacionContractual.AceptanteNoAcreditado);
        e.Repositorio.Aceptaciones.Should().HaveCount(2).And.OnlyContain(a => a.UsuarioId == Administrador);
    }

    [Theory]
    [InlineData("admin@ejemplo.test", true)]
    [InlineData("  Admin@Ejemplo.TEST ", true)]
    [InlineData("otra@ejemplo.test", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    public void El_correo_de_la_cuenta_se_compara_recortado_y_normalizado(string? correoCuenta, bool coincide)
    {
        SolicitudAlta.CorreoCoincide(correoCuenta, SolicitudAlta.NormalizarCorreo("Admin@Ejemplo.test")).Should().Be(coincide);
    }

    [Fact]
    public void El_correo_compara_igual_en_forma_Unicode_compuesta_y_descompuesta()
    {
        var compuesto = SolicitudAlta.NormalizarCorreo("josé@ejemplo.test");

        SolicitudAlta.CorreoCoincide("josé@ejemplo.test", compuesto).Should().BeTrue();
        SolicitudAlta.CorreoCoincide("josé@ejemplo.test", "").Should().BeFalse();
    }

    [Fact]
    public async Task Registra_una_fila_por_documento_con_version_hash_facultades_ip_fecha_e_importe()
    {
        var e = new Escenario();
        var datos = Datos(Declarada(CondicionesV1, Ahora.AddMinutes(-5), facultades: true), Declarada(EncargoV1, Ahora.AddMinutes(-5)));
        var r = await e.Registro().RegistrarAsync(datos);

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
        condiciones.SolicitudAltaId.Should().Be(datos.Solicitud.Id);
    }

    [Fact]
    public async Task Rechaza_atribuir_las_aceptaciones_a_una_cuenta_que_no_es_la_del_correo_verificado()
    {
        // Otra persona, aunque sea Administrador del Tenant propietario, no aceptó en esta Solicitud de alta.
        var e = new Escenario { CorreoCuenta = "otra@ejemplo.test" };

        var r = await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));

        r.Error.Should().Be(ErroresAceptacionContractual.AceptanteNoAcreditado);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Solicitud_de_alta_sin_aceptaciones_no_registra_nada()
    {
        var e = new Escenario();

        var r = await e.Registro().RegistrarAsync(new RegistrarAceptacionContractualAlta(Tenant, Administrador, SolicitudConAceptaciones([])));

        r.Error.Should().Be(ErroresAceptacionContractual.SinAceptaciones);
        e.Repositorio.Aceptaciones.Should().BeEmpty();
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
    public async Task El_importe_registrado_es_el_que_la_Solicitud_de_alta_guardo_con_cada_aceptacion()
    {
        var e = new Escenario();
        var r = await e.Registro().RegistrarAsync(Datos(
            Declarada(CondicionesV1, Ahora, facultades: true) with { ImportePermanencia = 59.00m },
            Declarada(EncargoV1, Ahora) with { ImportePermanencia = null }));

        r.EsExitoso.Should().BeTrue();
        e.Repositorio.Aceptaciones.Single(a => a.Documento == "Condiciones").ImportePermanencia.Should().Be(59.00m);
        e.Repositorio.Aceptaciones.Single(a => a.Documento == "Encargo").ImportePermanencia.Should().BeNull();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.001")]
    [InlineData("10000000000")]
    public void La_Solicitud_de_alta_no_admite_un_importe_de_permanencia_invalido(string importe)
    {
        var declarada = Declarada(CondicionesV1, Ahora, facultades: true) with
        {
            ImportePermanencia = decimal.Parse(importe, System.Globalization.CultureInfo.InvariantCulture),
        };

        var crear = () => SolicitudConAceptaciones([declarada]);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Repetir_el_registro_de_la_misma_Solicitud_de_alta_no_duplica_filas()
    {
        var e = new Escenario();
        var datos = Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora));

        (await e.Registro().RegistrarAsync(datos)).EsExitoso.Should().BeTrue();

        // Lo ya registrado no deja de estarlo porque después se desactive la cuenta o se retire la versión.
        e.Administradores.Clear();
        e.Catalogo.Clear();
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
    public void Una_copia_con_otro_texto_lleva_la_huella_de_su_texto()
    {
        var copia = CondicionesV1 with { Version = "1.1", Texto = "Texto revisado" };

        copia.HashSha256.Should().Be(DocumentoContractualPublicado.CalcularHash("Texto revisado"))
            .And.NotBe(CondicionesV1.HashSha256);
    }

    [Fact]
    public async Task El_acuse_envia_el_texto_aceptado_de_cada_documento_y_lo_deja_registrado()
    {
        var e = new Escenario();
        e.Catalogo.Add(CondicionesV2);
        await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV2, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));
        var bitacora = new List<string>();
        var correo = new CorreoFalso(bitacora: bitacora);
        var unidad = new UnidadFalsa(e.Repositorio, bitacora);

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

        // Primero se guarda lo preparado, después sale el correo y al final se guarda el resultado.
        bitacora.Should().Equal("guardado: Preparado, Preparado", "correo", "guardado: Preparado, Preparado, Enviado, Enviado");
        e.Repositorio.Acuses.Should().HaveCount(4)
            .And.OnlyContain(a => a.UsuarioDestinatarioId == Administrador && a.RegistradoEnUtc == Ahora);
        e.Repositorio.Acuses.Select(a => a.EnvioId).Distinct().Should().ContainSingle("las cuatro filas son del mismo envío");
        (await e.Repositorio.UltimoAcusePorAceptacionAsync()).Should().HaveCount(2).And.OnlyContain(kv => kv.Value == Ahora);
    }

    [Fact]
    public async Task Si_no_se_puede_guardar_la_preparacion_el_correo_no_sale()
    {
        var e = new Escenario();
        await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));
        var correo = new CorreoFalso();

        var enviar = () => new EnvioAcuseAceptacionContractual(
                e.Repositorio, new CatalogoFalso(e.Catalogo), new CuentasFalsas(Administrador, propia: true), correo,
                new UnidadFalsa(e.Repositorio, fallaEnGuardado: 1), new RelojFijo(Ahora))
            .EnviarAsync(Administrador);

        await enviar.Should().ThrowAsync<InvalidOperationException>();
        correo.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_correo_rechazado_queda_como_Fallido_y_no_cuenta_como_acuse()
    {
        var e = new Escenario();
        await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));
        var bitacora = new List<string>();

        var fallido = await new EnvioAcuseAceptacionContractual(
                e.Repositorio, new CatalogoFalso(e.Catalogo), new CuentasFalsas(Administrador, propia: true),
                new CorreoFalso(falla: true, bitacora: bitacora), new UnidadFalsa(e.Repositorio, bitacora), new RelojFijo(Ahora))
            .EnviarAsync(Administrador);

        fallido.EsFallido.Should().BeTrue();
        bitacora.Should().Equal("guardado: Preparado, Preparado", "correo", "guardado: Preparado, Preparado, Fallido, Fallido");
        e.Repositorio.Acuses.Should().NotContain(a => a.Fase == FaseAcuseAceptacionContractual.Enviado);
        (await e.Repositorio.UltimoAcusePorAceptacionAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Si_no_se_puede_guardar_el_resultado_el_envio_queda_preparado_sin_afirmarse()
    {
        var e = new Escenario();
        await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));
        var correo = new CorreoFalso();
        var unidad = new UnidadFalsa(e.Repositorio, fallaEnGuardado: 2);

        var enviar = () => new EnvioAcuseAceptacionContractual(
                e.Repositorio, new CatalogoFalso(e.Catalogo), new CuentasFalsas(Administrador, propia: true), correo,
                unidad, new RelojFijo(Ahora))
            .EnviarAsync(Administrador);

        await enviar.Should().ThrowAsync<InvalidOperationException>();
        correo.Enviados.Should().ContainSingle();
        unidad.Confirmadas.Should().HaveCount(2).And.OnlyContain(a => a.Fase == FaseAcuseAceptacionContractual.Preparado);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task El_acuse_no_se_envia_dentro_de_una_transaccion_ni_con_cambios_pendientes(bool transaccion, bool pendientes)
    {
        var e = new Escenario();
        await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));
        e.Repositorio.HayTransaccionAbierta = transaccion;
        e.Repositorio.HayCambiosPendientes = pendientes;
        var correo = new CorreoFalso();
        var unidad = new UnidadFalsa(e.Repositorio);

        var r = await new EnvioAcuseAceptacionContractual(
                e.Repositorio, new CatalogoFalso(e.Catalogo), new CuentasFalsas(Administrador, propia: true), correo,
                unidad, new RelojFijo(Ahora))
            .EnviarAsync(Administrador);

        r.Error.Should().Be(EnvioAcuseAceptacionContractual.EnvioNoAislado);
        correo.Enviados.Should().BeEmpty();
        unidad.Guardados.Should().Be(0);
        e.Repositorio.Acuses.Should().BeEmpty();
    }

    [Fact]
    public async Task El_acuse_no_se_envia_a_una_cuenta_ajena_ni_con_un_texto_que_no_es_el_aceptado()
    {
        var e = new Escenario();
        await e.Registro().RegistrarAsync(Datos(Declarada(CondicionesV1, Ahora, facultades: true), Declarada(EncargoV1, Ahora)));

        var correo = new CorreoFalso();
        var ajena = await new EnvioAcuseAceptacionContractual(
                e.Repositorio, new CatalogoFalso(e.Catalogo), new CuentasFalsas(Administrador, propia: false), correo,
                new UnidadFalsa(), new RelojFijo(Ahora))
            .EnviarAsync(Administrador);
        ajena.Error.Should().Be(EnvioAcuseAceptacionContractual.CuentaNoValida);

        var inactiva = await new EnvioAcuseAceptacionContractual(
                e.Repositorio, new CatalogoFalso(e.Catalogo), new CuentasFalsas(Administrador, propia: true, activa: false), correo,
                new UnidadFalsa(), new RelojFijo(Ahora))
            .EnviarAsync(Administrador);
        inactiva.Error.Should().Be(EnvioAcuseAceptacionContractual.CuentaNoValida);

        var sinCorreo = await new EnvioAcuseAceptacionContractual(
                e.Repositorio, new CatalogoFalso(e.Catalogo), new CuentasFalsas(Administrador, propia: true, email: " "), correo,
                new UnidadFalsa(), new RelojFijo(Ahora))
            .EnviarAsync(Administrador);
        sinCorreo.Error.Should().Be(EnvioAcuseAceptacionContractual.CuentaNoValida);

        // Si la versión aceptada ya no está en el catálogo, no se manda otro texto en su lugar.
        var sinTexto = await new EnvioAcuseAceptacionContractual(
                e.Repositorio, new CatalogoFalso([CondicionesV2, EncargoV1]), new CuentasFalsas(Administrador, propia: true), correo,
                new UnidadFalsa(), new RelojFijo(Ahora))
            .EnviarAsync(Administrador);
        sinTexto.Error.Should().Be(EnvioAcuseAceptacionContractual.TextoNoDisponible);

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
        public bool HayTransaccionAbierta { get; set; }
        public bool HayCambiosPendientes { get; set; }

        public Task<bool> ExistenDeSolicitudAltaAsync(Guid solicitudAltaId, Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Aceptaciones.Any(a => a.SolicitudAltaId == solicitudAltaId && a.UsuarioId == usuarioId));

        public Task<IReadOnlyList<AceptacionContractualTenant>> ListarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AceptacionContractualTenant>>(
                Aceptaciones.OrderByDescending(a => a.AceptadaEnUtc).ThenByDescending(a => a.RegistradaEnUtc).ToList());

        public Task<IReadOnlyDictionary<Guid, DateTime>> UltimoAcusePorAceptacionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, DateTime>>(
                Acuses.Where(a => a.Fase == FaseAcuseAceptacionContractual.Enviado)
                    .GroupBy(a => a.AceptacionContractualTenantId).ToDictionary(g => g.Key, g => g.Max(a => a.RegistradoEnUtc)));
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

    /// <summary>La cuenta <paramref name="usuarioId"/> es propia de <see cref="Tenant"/> y tiene <paramref name="email"/>.</summary>
    private sealed class CorreoDeCuentaFalso(Guid usuarioId, string email) : ICorreoDeCuentaAceptante
    {
        public Task<bool> EsCorreoDeLaCuentaAsync(Guid id, Guid tenantId, string correoNormalizado, CancellationToken cancellationToken = default) =>
            Task.FromResult(id == usuarioId && tenantId == Tenant && SolicitudAlta.CorreoCoincide(email, correoNormalizado));
    }

    private sealed class CuentasFalsas(Guid usuarioId, bool propia, string email = CorreoAdministrador, bool activa = true) : IGestionCuentasUsuario
    {
        public Task<CuentaUsuario?> ObtenerAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<CuentaUsuario?>(id == usuarioId
                ? new CuentaUsuario(id, email, "Administrador de prueba", propia, ["Administrador"], false, activa, false)
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

    private sealed class CorreoFalso(bool falla = false, List<string>? bitacora = null) : IEmailService
    {
        public List<(string Destinatario, string Asunto, string Cuerpo)> Enviados { get; } = [];

        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            bitacora?.Add("correo");
            if (falla)
                return Task.FromResult(Result.Fallo(Error.Crear("Correo.Fallo", "No salió.")));
            Enviados.Add((destinatarioEmail, asunto, cuerpoHtml));
            return Task.FromResult(Result.Exito());
        }
    }

    /// <summary>
    /// Anota en <paramref name="bitacora"/> las fases de acuse que había al guardar, y conserva en
    /// <see cref="Confirmadas"/> lo que quedó guardado de verdad. El guardado número <paramref name="fallaEnGuardado"/>
    /// lanza, como un fallo de la base de datos, y no confirma nada.
    /// </summary>
    private sealed class UnidadFalsa(RepositorioFalso? repositorio = null, List<string>? bitacora = null, int fallaEnGuardado = 0) : IUnitOfWork
    {
        public int Guardados { get; private set; }
        public List<AcuseAceptacionContractualTenant> Confirmadas { get; } = [];

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Guardados++;
            if (Guardados == fallaEnGuardado)
                throw new InvalidOperationException("Fallo simulado de la base de datos.");
            if (repositorio is not null)
            {
                Confirmadas.Clear();
                Confirmadas.AddRange(repositorio.Acuses);
                bitacora?.Add("guardado: " + string.Join(", ", repositorio.Acuses.Select(a => a.Fase)));
            }

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
