using System.Xml.Linq;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using CaeManager.Infrastructure.DataProtection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.DataProtection;

/// <summary>
/// Contrato de <see cref="KmsXmlEncryptor"/>/<see cref="KmsXmlDecryptor"/>.
///
/// No se llama a AWS: lo que hay que garantizar es que el XML de una clave de
/// Data Protection sale ilegible del encryptor y vuelve idéntico del
/// decryptor, y que el descifrado falla cerrado si KMS no está disponible.
/// Comprobar que KMS cifra bien es responsabilidad de AWS, no de esta suite.
/// </summary>
public class CifradoDeClavesConKmsTests
{
    private const string ClaveDeEjemplo = "alias/caemanager-dataprotection";

    // Una clave de Data Protection real es un XML con material criptográfico:
    // esto imita su forma para que el test no dependa de un string trivial.
    private static XElement XmlDeClave() =>
        XElement.Parse("<key id=\"3d1d1e6a\"><descriptor><encryption algorithm=\"AES_256_CBC\" /><masterKey>c2VjcmV0bw==</masterKey></descriptor></key>");

    [Fact]
    public void El_xml_cifrado_no_contiene_el_material_en_claro()
    {
        var encryptor = new KmsXmlEncryptor(new KmsFalso(), ClaveDeEjemplo);

        var cifrado = encryptor.Encrypt(XmlDeClave());

        cifrado.EncryptedElement.ToString().Should().NotContain("masterKey");
        cifrado.EncryptedElement.ToString().Should().NotContain("c2VjcmV0bw==");
        cifrado.DecryptorType.Should().Be<KmsXmlDecryptor>();
    }

    [Fact]
    public void Lo_cifrado_vuelve_intacto_al_descifrar()
    {
        var kms = new KmsFalso();
        var original = XmlDeClave();

        var cifrado = new KmsXmlEncryptor(kms, ClaveDeEjemplo).Encrypt(original);

        var servicios = new ServiceCollection();
        servicios.AddSingleton<IAmazonKeyManagementService>(kms);

        var descifrado = new KmsXmlDecryptor(servicios.BuildServiceProvider()).Decrypt(cifrado.EncryptedElement);

        descifrado.ToString(SaveOptions.DisableFormatting)
            .Should().Be(original.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void Se_cifra_con_la_clave_configurada()
    {
        var kms = new KmsFalso();

        new KmsXmlEncryptor(kms, ClaveDeEjemplo).Encrypt(XmlDeClave());

        kms.UltimaClaveUsada.Should().Be(ClaveDeEjemplo);
    }

    [Fact]
    public void Sin_KMS_disponible_el_descifrado_falla_con_un_mensaje_util()
    {
        // El caso real: alguien despliega con claves ya cifradas pero sin la
        // configuración de KMS. Tiene que fallar diciendo qué pasa, no con un
        // NullReference tres capas más abajo.
        var cifrado = new KmsXmlEncryptor(new KmsFalso(), ClaveDeEjemplo).Encrypt(XmlDeClave());
        var sinKms = new ServiceCollection().BuildServiceProvider();

        var descifrar = () => new KmsXmlDecryptor(sinKms).Decrypt(cifrado.EncryptedElement);

        descifrar.Should().Throw<InvalidOperationException>().WithMessage("*KMS no está configurado*");
    }

    /// <summary>
    /// Doble de KMS: invierte los bytes en vez de cifrar. Suficiente para
    /// comprobar que el material no viaja en claro y que el ciclo completo
    /// devuelve lo mismo, sin depender de la red ni de credenciales.
    /// </summary>
    private sealed class KmsFalso : AmazonKeyManagementServiceClient
    {
        public KmsFalso() : base("clave-de-prueba", "secreto-de-prueba", Amazon.RegionEndpoint.EUSouth2) { }

        public string? UltimaClaveUsada { get; private set; }

        public override Task<EncryptResponse> EncryptAsync(
            EncryptRequest request, CancellationToken cancellationToken = default)
        {
            UltimaClaveUsada = request.KeyId;

            var bytes = request.Plaintext.ToArray().Reverse().ToArray();
            return Task.FromResult(new EncryptResponse { CiphertextBlob = new MemoryStream(bytes) });
        }

        public override Task<DecryptResponse> DecryptAsync(
            DecryptRequest request, CancellationToken cancellationToken = default)
        {
            var bytes = request.CiphertextBlob.ToArray().Reverse().ToArray();
            return Task.FromResult(new DecryptResponse { Plaintext = new MemoryStream(bytes) });
        }
    }
}
