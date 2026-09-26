using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.DataProtection;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CaeManager.IntegrationTests.DataProtection;

/// <summary>
/// Contrato de P1-F3 sobre el registro REAL de Data Protection
/// (<see cref="RegistroDataProtection.AgregarDataProtectionDeCaeManager"/>, el
/// mismo que llama AddInfrastructure): con el certificado configurado, una clave
/// nueva se escribe en disco sin su material en claro y vuelve a leerse desde
/// otro proceso; una clave escrita en claro antes de configurarlo se sigue
/// leyendo; la rotación conserva los certificados anteriores para descifrar; y
/// Production sin cifrado no arranca salvo declaración explícita.
///
/// Sin red ni base de datos: llavero en un directorio temporal y certificados
/// RSA autofirmados generados en el propio test, exportados a PEM como los
/// montará el servidor en /run/secretos.
/// </summary>
public sealed class CifradoDeClavesConCertificadoTests : IDisposable
{
    private const string Proposito = "P1-F3.prueba";
    private const string Secreto = "credencial-de-portal-externo";

    private readonly DirectoryInfo _raiz = Directory.CreateTempSubdirectory("p1f3-");

    private string RutaLlavero => Path.Combine(_raiz.FullName, "dataprotection-keys");

    public void Dispose() => _raiz.Delete(recursive: true);

    [Fact]
    public void Con_certificado_la_clave_persistida_no_contiene_el_material_en_claro()
    {
        var certificado = GenerarCertificadoPem("vigente");

        var protegido = Proteger(Configuracion(certificado), "Production");

        var xml = LeerLlavero();
        xml.Should().Contain("<encryptedSecret", "el material de la clave tiene que ir dentro del elemento cifrado");
        xml.Should().Contain("EncryptedData");
        xml.Should().NotContain("<masterKey", "el material en claro de la clave no puede llegar al disco");

        // Y lo cifrado sigue siendo utilizable desde otro proceso con el mismo certificado.
        Desproteger(Configuracion(certificado), "Production", protegido).Should().Be(Secreto);
    }

    [Fact]
    public void Control_negativo_sin_certificado_el_material_si_queda_en_claro()
    {
        // Sensibilidad del instrumento: la aserción «no contiene <masterKey» solo
        // prueba algo si ese texto aparece cuando no se cifra.
        Proteger(Configuracion(), "Development");

        LeerLlavero().Should().Contain("<masterKey").And.NotContain("<encryptedSecret");
    }

    [Fact]
    public void Una_clave_antigua_sin_cifrar_se_sigue_leyendo_al_activar_el_certificado()
    {
        var protegidoAntes = Proteger(Configuracion(), "Development");
        LeerLlavero().Should().Contain("<masterKey", "precondición: la clave antigua está en claro");

        var certificado = GenerarCertificadoPem("vigente");

        Desproteger(Configuracion(certificado), "Production", protegidoAntes).Should().Be(Secreto,
            "activar el cifrado no puede invalidar cookies ni secretos protegidos con claves anteriores");
    }

    [Fact]
    public void Activar_el_certificado_no_crea_una_clave_nueva_mientras_la_vigente_no_este_por_caducar()
    {
        // Sostiene la convivencia del relevo azul/verde (P1-F2): mientras la
        // ranura antigua, sin certificado, siga viva, la nueva no debe escribir
        // ninguna clave cifrada que la antigua no pueda leer. Data Protection
        // solo crea clave si el llavero está vacío o la vigente caduca dentro
        // de su ventana de propagación (2 días): activar el cifrado por sí solo
        // no la crea. El runbook exige comprobar esa fecha antes de activarlo.
        var protegidoAntes = Proteger(Configuracion(), "Development");
        var clavesAntes = Directory.GetFiles(RutaLlavero, "key-*.xml");

        var configuracion = Configuracion(GenerarCertificadoPem("vigente"));
        Desproteger(configuracion, "Production", protegidoAntes).Should().Be(Secreto);
        Proteger(configuracion, "Production");

        Directory.GetFiles(RutaLlavero, "key-*.xml").Should().BeEquivalentTo(clavesAntes,
            "con la clave vigente lejos de caducar, la ranura nueva sigue usando la clave en claro que la antigua también lee");
        LeerLlavero().Should().NotContain("<encryptedSecret");
    }

    [Fact]
    public void Tras_rotar_las_claves_cifradas_con_el_certificado_anterior_se_leen_si_sigue_en_Anteriores()
    {
        var antiguo = GenerarCertificadoPem("antiguo");
        var protegido = Proteger(Configuracion(antiguo), "Production");

        var nuevo = GenerarCertificadoPem("nuevo");

        Desproteger(Configuracion(nuevo, antiguo), "Production", protegido).Should().Be(Secreto);
    }

    [Fact]
    public void Tras_rotar_sin_conservar_el_anterior_las_claves_cifradas_con_el_no_se_leen()
    {
        // Control de la prueba anterior: si esto no fallara, «Anteriores» no
        // estaría haciendo nada y la de arriba pasaría por otro motivo.
        var antiguo = GenerarCertificadoPem("antiguo");
        var protegido = Proteger(Configuracion(antiguo), "Production");

        var nuevo = GenerarCertificadoPem("nuevo");

        // Se niega ya en el arranque, no al primer Unprotect: Data Protection
        // daría la clave por inutilizable y generaría otra en silencio.
        var arrancar = () => Construir(Configuracion(nuevo), "Production");
        arrancar.Should().Throw<InvalidOperationException>().WithMessage("*certificado que no está configurado*");
    }

    [Fact]
    public void Con_claves_ya_cifradas_quitar_el_certificado_no_arranca_aunque_este_la_bandera()
    {
        Proteger(Configuracion(GenerarCertificadoPem("vigente")), "Production");

        var sinCertificado = Configuracion();
        sinCertificado[RegistroDataProtection.ClavePermitirClavesSinCifrar] = "true";

        var arrancar = () => Construir(sinCertificado, "Production");
        arrancar.Should().Throw<InvalidOperationException>().WithMessage("*certificado que no está configurado*");
    }

    [Theory]
    [InlineData("CertificadoRuta")]
    [InlineData("ClavePrivadaRuta")]
    [InlineData("Anteriores:0:CertificadoRuta")]
    public void Un_certificado_a_medias_no_degrada_a_sin_cifrar_aunque_este_la_bandera(string unicaClaveInformada)
    {
        var configuracion = Configuracion();
        configuracion[$"DataProtection:Certificado:{unicaClaveInformada}"] = Path.Combine(_raiz.FullName, "algo.pem");
        configuracion[RegistroDataProtection.ClavePermitirClavesSinCifrar] = "true";

        var arrancar = () => Construir(configuracion, "Production");
        arrancar.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:Certificado está a medias*");
    }

    [Fact]
    public void Un_certificado_que_no_es_RSA_no_arranca()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificado = new CertificateRequest("CN=talveg-dataprotection-ec", ec, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var rutaCertificado = Path.Combine(_raiz.FullName, "ec.crt");
        var rutaClave = Path.Combine(_raiz.FullName, "ec.key");
        File.WriteAllText(rutaCertificado, certificado.ExportCertificatePem());
        File.WriteAllText(rutaClave, ec.ExportPkcs8PrivateKeyPem());

        var arrancar = () => Construir(Configuracion((rutaCertificado, rutaClave)), "Production");
        arrancar.Should().Throw<InvalidOperationException>().WithMessage("*tiene que ser RSA*");
    }

    [Fact]
    public void En_Production_sin_cifrado_el_arranque_se_niega_con_un_mensaje_claro()
    {
        var registrar = () => Construir(Configuracion(), "Production");

        registrar.Should().Throw<InvalidOperationException>()
            .WithMessage("*SIN CIFRAR en Production*DataProtection:Certificado*DataProtection:PermitirClavesSinCifrar=true*");
    }

    [Fact]
    public void En_Production_la_bandera_de_transicion_permite_arrancar_sin_cifrado()
    {
        var configuracion = Configuracion();
        configuracion[RegistroDataProtection.ClavePermitirClavesSinCifrar] = "true";

        Desproteger(configuracion, "Production", Proteger(configuracion, "Production")).Should().Be(Secreto);
    }

    [Fact]
    public async Task En_Production_con_la_bandera_cada_arranque_emite_una_alerta_operativa_que_la_nombra()
    {
        var configuracion = Configuracion();
        configuracion[RegistroDataProtection.ClavePermitirClavesSinCifrar] = "true";
        var alerta = new AlertaOperativaCapturada();

        using var proveedor = Construir(configuracion, "Production", alerta);
        foreach (var servicio in proveedor.GetServices<IHostedService>())
            await servicio.StartAsync(CancellationToken.None);

        alerta.Emitidas.Should().ContainSingle()
            .Which.Should().Match<(string Mensaje, NivelAlertaOperativa Nivel)>(a =>
                a.Mensaje.Contains(RegistroDataProtection.ClavePermitirClavesSinCifrar) && a.Nivel == NivelAlertaOperativa.Aviso);
    }

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    public void La_bandera_no_alerta_fuera_de_Production_ni_con_certificado(string entorno, bool conCertificado)
    {
        var configuracion = conCertificado ? Configuracion(GenerarCertificadoPem("vigente")) : Configuracion();
        configuracion[RegistroDataProtection.ClavePermitirClavesSinCifrar] = "true";

        using var proveedor = Construir(configuracion, entorno, new AlertaOperativaCapturada());

        proveedor.GetServices<IHostedService>().OfType<AvisoLlaveroSinCifrarHostedService>().Should().BeEmpty(
            "la bandera solo tiene efecto en Production y sin cifrador; en otro caso no hay nada que avisar");
    }

    [Fact]
    public void Con_certificado_la_bandera_de_transicion_no_desactiva_el_cifrado()
    {
        var configuracion = Configuracion(GenerarCertificadoPem("vigente"));
        configuracion[RegistroDataProtection.ClavePermitirClavesSinCifrar] = "true";

        Proteger(configuracion, "Production");

        LeerLlavero().Should().Contain("<encryptedSecret").And.NotContain("<masterKey");
    }

    [Fact]
    public void Certificado_y_KMS_a_la_vez_es_una_configuracion_que_no_arranca()
    {
        var configuracion = Configuracion(GenerarCertificadoPem("vigente"));
        configuracion["DataProtection:Kms:Activo"] = "true";
        configuracion["DataProtection:Kms:KeyId"] = "alias/prueba";
        configuracion["DataProtection:Kms:AccessKeyId"] = "id";
        configuracion["DataProtection:Kms:SecretAccessKey"] = "secreto";
        configuracion["DataProtection:Kms:Region"] = "eu-south-2";

        var registrar = () => Construir(configuracion, "Production");

        registrar.Should().Throw<InvalidOperationException>().WithMessage("*a la vez*");
    }

    [Fact]
    public void Un_certificado_configurado_que_no_existe_no_degrada_a_sin_cifrar()
    {
        var configuracion = Configuracion();
        configuracion["DataProtection:Certificado:CertificadoRuta"] = Path.Combine(_raiz.FullName, "no-existe.crt");
        configuracion["DataProtection:Certificado:ClavePrivadaRuta"] = Path.Combine(_raiz.FullName, "no-existe.key");
        configuracion[RegistroDataProtection.ClavePermitirClavesSinCifrar] = "true";

        var registrar = () => Construir(configuracion, "Production");

        registrar.Should().Throw<InvalidOperationException>()
            .WithMessage("*DataProtection:Certificado*no se puede leer*");
    }

    private (string Certificado, string ClavePrivada) GenerarCertificadoPem(string nombre)
    {
        using var rsa = RSA.Create(2048);
        var solicitud = new CertificateRequest(
            $"CN=talveg-dataprotection-{nombre}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificado = solicitud.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var rutaCertificado = Path.Combine(_raiz.FullName, $"{nombre}.crt");
        var rutaClave = Path.Combine(_raiz.FullName, $"{nombre}.key");
        File.WriteAllText(rutaCertificado, certificado.ExportCertificatePem());
        File.WriteAllText(rutaClave, rsa.ExportPkcs8PrivateKeyPem());
        return (rutaCertificado, rutaClave);
    }

    private Dictionary<string, string?> Configuracion(
        (string Certificado, string ClavePrivada)? vigente = null,
        params (string Certificado, string ClavePrivada)[] anteriores)
    {
        var configuracion = new Dictionary<string, string?> { ["DataProtection:RutaClaves"] = RutaLlavero };

        if (vigente is { } v)
        {
            configuracion["DataProtection:Certificado:CertificadoRuta"] = v.Certificado;
            configuracion["DataProtection:Certificado:ClavePrivadaRuta"] = v.ClavePrivada;
        }

        for (var i = 0; i < anteriores.Length; i++)
        {
            configuracion[$"DataProtection:Certificado:Anteriores:{i}:CertificadoRuta"] = anteriores[i].Certificado;
            configuracion[$"DataProtection:Certificado:Anteriores:{i}:ClavePrivadaRuta"] = anteriores[i].ClavePrivada;
        }

        return configuracion;
    }

    private ServiceProvider Construir(
        Dictionary<string, string?> configuracion, string entorno, IAlertaOperativa? alerta = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        if (alerta is not null)
            servicios.AddSingleton(alerta);
        servicios.AgregarDataProtectionDeCaeManager(
            new ConfigurationBuilder().AddInMemoryCollection(configuracion).Build(),
            new EntornoDePrueba(entorno, _raiz.FullName));
        return servicios.BuildServiceProvider();
    }

    // Cada llamada construye un contenedor nuevo: la lectura sale del disco, no
    // de la caché del llavero en memoria del proceso que cifró.
    private string Proteger(Dictionary<string, string?> configuracion, string entorno)
    {
        using var proveedor = Construir(configuracion, entorno);
        return proveedor.GetRequiredService<IDataProtectionProvider>().CreateProtector(Proposito).Protect(Secreto);
    }

    private string Desproteger(Dictionary<string, string?> configuracion, string entorno, string protegido)
    {
        using var proveedor = Construir(configuracion, entorno);
        return proveedor.GetRequiredService<IDataProtectionProvider>().CreateProtector(Proposito).Unprotect(protegido);
    }

    private string LeerLlavero()
    {
        var ficheros = Directory.GetFiles(RutaLlavero, "key-*.xml");
        ficheros.Should().NotBeEmpty("el registro tiene que persistir el llavero en DataProtection:RutaClaves");
        return string.Join("\n", ficheros.Select(File.ReadAllText));
    }

    private sealed class AlertaOperativaCapturada : IAlertaOperativa
    {
        public List<(string Mensaje, NivelAlertaOperativa Nivel)> Emitidas { get; } = [];

        public void Emitir(string mensaje, NivelAlertaOperativa nivel) => Emitidas.Add((mensaje, nivel));

        public void CapturarExcepcion(Exception excepcion) { }

        public void DejarMigaDePan(string mensaje) { }

        public IDisposable IniciarAmbitoDeCaptura() => new MemoryStream();
    }

    private sealed class EntornoDePrueba(string nombre, string raiz) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nombre;
        public string ApplicationName { get; set; } = "CaeManager";
        public string ContentRootPath { get; set; } = raiz;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
