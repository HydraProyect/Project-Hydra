using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CaeManager.Application.Inbound.CredencialesAcceso;
using CaeManager.Infrastructure.Inbound;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests.Inbound;

/// <summary>
/// AF-03: la firma de la credencial de acceso. Lo que se prueba es la propiedad de seguridad (un token alterado, de otra
/// clave, de un <c>kid</c> desconocido, de serie antigua o vencido no autoriza), no la forma del código. Todo falla
/// cerrado: <see cref="IFirmanteCredencialAcceso.Verificar"/> devuelve <c>null</c> sin decir por qué.
/// </summary>
public sealed class FirmanteCredencialAccesoTests : IDisposable
{
    private static readonly Guid CredencialId = Guid.Parse("1111111a-2222-3333-4444-55555555555b");
    private static readonly Guid Centro1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Centro2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Centro3 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private readonly string _directorio = Path.Combine(Path.GetTempPath(), "llavero-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _proteccion = new EphemeralDataProtectionProvider();

    public void Dispose()
    {
        if (Directory.Exists(_directorio))
            Directory.Delete(_directorio, recursive: true);
    }

    private string Fichero => Path.Combine(_directorio, "llavero.json");

    private FirmanteCredencialAcceso Crear(string? fichero = null, IDataProtectionProvider? proteccion = null, int tope = 8) =>
        new(new LlaveroCredencialAcceso(fichero ?? Fichero, proteccion ?? _proteccion),
            Options.Create(new OpcionesFirmaCredencialAcceso { MaxCentrosPorToken = tope }));

    private static CredencialParaFirmar Credencial(int serie = 3, params Guid[] centros) =>
        new(CredencialId, serie,
            (centros.Length == 0 ? [Centro1] : centros)
                .Select(c => new CentroConFechaMaxima(c, new DateOnly(2026, 12, 31))).ToList());

    private static string UnToken(FirmanteCredencialAcceso f, int serie = 3) => f.Firmar(Credencial(serie)).Single();

    [Fact]
    public void Firmar_y_verificar_devuelve_el_mismo_contenido_y_sin_datos_personales()
    {
        var f = Crear();
        var token = UnToken(f);

        token.Should().MatchRegex(@"^v1\.[0-9a-f]{16}\.[0-9a-f]{32}\.3\.[0-9a-f]{32}:20261231\.[A-Za-z0-9_-]{86}$");

        var verificada = f.Verificar(token);
        verificada.Should().NotBeNull();
        verificada!.CredencialId.Should().Be(CredencialId);
        verificada.Serie.Should().Be(3);
        verificada.Centros.Should().ContainSingle().Which.Should().Be(new CentroConFechaMaxima(Centro1, new DateOnly(2026, 12, 31)));
        verificada.Admite(Centro1, new DateOnly(2026, 12, 31), serieVigente: 3).Should().BeTrue();
    }

    [Fact]
    public void La_firma_es_P1363_y_se_verifica_con_la_clave_publica_SPKI_sin_el_llavero()
    {
        // Lo mismo que hará WebCrypto en el lector (AF-12b): importar el SPKI y verificar r‖s sobre los bytes UTF-8.
        var f = Crear();
        var token = UnToken(f);
        var publica = f.ClavesPublicasVigentes().Single();

        var ultimoPunto = token.LastIndexOf('.');
        var cuerpo = Encoding.UTF8.GetBytes(token[..ultimoPunto]);
        var firma = FormatoTokenCredencial.DesdeBase64Url(token[(ultimoPunto + 1)..])!;

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publica.SpkiBase64), out _);
        ecdsa.KeySize.Should().Be(256);
        firma.Should().HaveCount(64);
        ecdsa.VerifyData(cuerpo, firma, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            .Should().BeTrue();
    }

    [Fact]
    public void Un_token_con_cualquier_caracter_cambiado_no_verifica()
    {
        var f = Crear();
        var token = UnToken(f);

        // Cambiar un solo carácter en cada posición: cuerpo, separadores y firma (incluidos los bits sobrantes del
        // último carácter base64url, que sin el análisis canónico darían la misma firma).
        for (var i = 0; i < token.Length; i++)
        {
            var alterado = new StringBuilder(token) { [i] = token[i] == 'a' ? 'b' : 'a' }.ToString();
            f.Verificar(alterado).Should().BeNull($"la posición {i} alterada no puede verificar");
        }
    }

    [Fact]
    public void Un_token_con_un_bit_cambiado_en_los_bytes_de_la_firma_no_verifica()
    {
        var f = Crear();
        var token = UnToken(f);
        var punto = token.LastIndexOf('.');
        var firma = FormatoTokenCredencial.DesdeBase64Url(token[(punto + 1)..])!;

        for (var bit = 0; bit < firma.Length * 8; bit += 7)
        {
            var copia = (byte[])firma.Clone();
            copia[bit / 8] ^= (byte)(1 << (bit % 8));
            f.Verificar(token[..(punto + 1)] + FormatoTokenCredencial.Base64Url(copia)).Should().BeNull();
        }
    }

    [Fact]
    public void Un_token_emitido_con_otra_clave_no_verifica()
    {
        var f = Crear();
        var ajeno = Crear(Path.Combine(_directorio, "otro.json"));
        var tokenAjeno = UnToken(ajeno);

        f.Verificar(tokenAjeno).Should().BeNull();

        // Y aun con el kid de la clave legítima: la firma es de otra.
        UnToken(f); // asegura que existe la clave activa.
        var kidLegitimo = f.ClavesPublicasVigentes().Single().Kid;
        var partes = tokenAjeno.Split('.');
        partes[1] = kidLegitimo;
        f.Verificar(string.Join('.', partes)).Should().BeNull();
    }

    [Fact]
    public void Un_kid_desconocido_no_verifica()
    {
        var f = Crear();
        var token = UnToken(f);
        var partes = token.Split('.');
        partes[1] = "0123456789abcdef";
        f.Verificar(string.Join('.', partes)).Should().BeNull();
    }

    [Fact]
    public void Sin_llavero_creado_no_se_crea_uno_al_verificar()
    {
        var f = Crear();
        var tokenDeOtro = UnToken(Crear(Path.Combine(_directorio, "otro.json")));

        f.Verificar(tokenDeOtro).Should().BeNull();
        File.Exists(Fichero).Should().BeFalse("verificar no debe crear claves");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("basura")]
    [InlineData("v2.a.b.c.d.e")]
    [InlineData("v1.0123456789abcdef.x.3.y.z")]
    public void Un_token_mal_formado_no_verifica(string? token) =>
        Crear().Verificar(token).Should().BeNull();

    [Fact]
    public void Un_token_no_canonico_no_verifica_aunque_el_contenido_sea_el_mismo()
    {
        var f = Crear();
        var token = UnToken(f);
        var partes = token.Split('.');

        partes[2] = partes[2].ToUpperInvariant(); // mismo Guid, otra cadena
        f.Verificar(string.Join('.', partes)).Should().BeNull();

        partes = token.Split('.');
        partes[3] = "03"; // misma serie con cero a la izquierda
        f.Verificar(string.Join('.', partes)).Should().BeNull();
    }

    [Fact]
    public void Una_serie_antigua_no_se_admite()
    {
        var f = Crear();
        var verificada = f.Verificar(UnToken(f, serie: 3))!;

        verificada.Admite(Centro1, new DateOnly(2026, 12, 1), serieVigente: 3).Should().BeTrue();
        verificada.Admite(Centro1, new DateOnly(2026, 12, 1), serieVigente: 4).Should().BeFalse("la serie 3 quedó invalidada");
        verificada.Admite(Centro1, new DateOnly(2026, 12, 1), serieVigente: 2).Should().BeFalse();
    }

    [Fact]
    public void Una_fecha_maxima_vencida_o_un_Centro_ausente_no_se_admiten()
    {
        var f = Crear();
        var verificada = f.Verificar(UnToken(f))!;

        verificada.Admite(Centro1, new DateOnly(2026, 12, 31), 3).Should().BeTrue();
        verificada.Admite(Centro1, new DateOnly(2027, 1, 1), 3).Should().BeFalse();
        verificada.Admite(Centro2, new DateOnly(2026, 12, 1), 3).Should().BeFalse();
    }

    [Fact]
    public void Con_los_Centros_bajo_el_tope_sale_un_solo_token_y_por_encima_uno_por_Centro()
    {
        var f = Crear(tope: 2);

        var uno = f.Firmar(Credencial(3, Centro1, Centro2));
        uno.Should().ContainSingle();
        f.Verificar(uno[0])!.Centros.Select(c => c.CentroId).Should().BeEquivalentTo([Centro1, Centro2]);

        var varios = f.Firmar(Credencial(3, Centro1, Centro2, Centro3));
        varios.Should().HaveCount(3);
        varios.Select(t => f.Verificar(t)!.Centros.Single().CentroId).Should().BeEquivalentTo([Centro1, Centro2, Centro3]);
    }

    [Fact]
    public void Firmar_rechaza_datos_invalidos()
    {
        var f = Crear();
        f.Invoking(x => x.Firmar(new CredencialParaFirmar(Guid.Empty, 1, [new(Centro1, new DateOnly(2026, 1, 1))])))
            .Should().Throw<ArgumentException>();
        f.Invoking(x => x.Firmar(new CredencialParaFirmar(CredencialId, 0, [new(Centro1, new DateOnly(2026, 1, 1))])))
            .Should().Throw<ArgumentException>();
        f.Invoking(x => x.Firmar(new CredencialParaFirmar(CredencialId, 1, [])))
            .Should().Throw<ArgumentException>();
        f.Invoking(x => x.Firmar(new CredencialParaFirmar(CredencialId, 1,
                [new(Centro1, new DateOnly(2026, 1, 1)), new(Centro1, new DateOnly(2026, 2, 1))])))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void La_rotacion_mantiene_la_anterior_una_generacion_y_la_siguiente_la_retira()
    {
        var f = Crear();
        var t0 = UnToken(f);
        var kid0 = f.ClavesPublicasVigentes().Single().Kid;

        f.Rotar();
        var t1 = UnToken(f);
        f.ClavesPublicasVigentes().Should().HaveCount(2);
        f.ClavesPublicasVigentes().Single(c => c.Activa).Kid.Should().NotBe(kid0);
        f.Verificar(t0).Should().NotBeNull("la clave anterior sigue verificando");
        f.Verificar(t1).Should().NotBeNull();

        f.Rotar();
        f.ClavesPublicasVigentes().Should().HaveCount(2);
        f.Verificar(t0).Should().BeNull("la clave de t0 ya salió del llavero");
        f.Verificar(t1).Should().NotBeNull();
    }

    [Fact]
    public void La_rotacion_de_emergencia_invalida_todo_lo_emitido()
    {
        var f = Crear();
        var t0 = UnToken(f);
        f.Rotar();
        var t1 = UnToken(f);

        f.RotarDeEmergencia();

        f.ClavesPublicasVigentes().Should().ContainSingle();
        f.Verificar(t0).Should().BeNull();
        f.Verificar(t1).Should().BeNull();
        f.Verificar(UnToken(f)).Should().NotBeNull("lo emitido después sí vale");
    }

    [Fact]
    public void El_llavero_persiste_y_otra_instancia_verifica_lo_firmado()
    {
        var token = UnToken(Crear());
        Crear().Verificar(token).Should().NotBeNull();
    }

    [Fact]
    public void La_privada_va_protegida_y_sin_el_protector_no_se_puede_firmar_pero_si_verificar()
    {
        var token = UnToken(Crear());

        var otroProtector = new EphemeralDataProtectionProvider();
        var f = Crear(proteccion: otroProtector);

        f.Verificar(token).Should().NotBeNull("la pública va en claro");
        f.Invoking(x => x.Firmar(Credencial())).Should().Throw<CryptographicException>();
        File.ReadAllText(Fichero).Should().NotContain("BEGIN").And.NotContain("PRIVATE");
    }

    [Fact]
    public void Un_llavero_ilegible_falla_cerrado_y_no_se_regenera()
    {
        Directory.CreateDirectory(_directorio);
        File.WriteAllText(Fichero, "{ esto no es json");
        var f = Crear();

        f.Verificar("v1.0123456789abcdef.1111111a22223333444455555555555b.3.aaaaaaaa000000000000000000000001:20261231." +
                    new string('A', 86)).Should().BeNull();
        f.Invoking(x => x.Firmar(Credencial())).Should().Throw<InvalidOperationException>();
        File.ReadAllText(Fichero).Should().Be("{ esto no es json", "no se sustituye en silencio");
    }
}
