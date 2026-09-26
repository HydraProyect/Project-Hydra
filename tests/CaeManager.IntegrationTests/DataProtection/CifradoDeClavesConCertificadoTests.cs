using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

        var desproteger = () => Desproteger(Configuracion(nuevo), "Production", protegido);
        desproteger.Should().Throw<CryptographicException>();
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

    private ServiceProvider Construir(Dictionary<string, string?> configuracion, string entorno)
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
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

    private sealed class EntornoDePrueba(string nombre, string raiz) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nombre;
        public string ApplicationName { get; set; } = "CaeManager";
        public string ContentRootPath { get; set; } = raiz;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
