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

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; } = ahora;
        public override DateTimeOffset GetUtcNow() => Ahora;
    }

    private readonly RelojFijo _reloj = new(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero));

    private FirmanteCredencialAcceso Crear(string? fichero = null, IDataProtectionProvider? proteccion = null, int tope = 8) =>
        new(new LlaveroCredencialAcceso(fichero ?? Fichero, proteccion ?? _proteccion),
            Options.Create(new OpcionesFirmaCredencialAcceso { MaxCentrosPorToken = tope }), _reloj);

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
    public void La_rotacion_conserva_la_anterior_mientras_tenga_fechas_vivas_y_la_retira_despues()
    {
        var f = Crear();
        var t0 = UnToken(f); // fecha máxima 2026-12-31
        var kid0 = f.ClavesPublicasVigentes().Single().Kid;

        f.Rotar();
        var t1 = UnToken(f);
        f.ClavesPublicasVigentes().Should().HaveCount(2);
        f.ClavesPublicasVigentes().Single(c => c.Activa).Kid.Should().NotBe(kid0);
        f.Verificar(t0).Should().NotBeNull("la clave anterior sigue verificando");
        f.Verificar(t1).Should().NotBeNull();

        // Una segunda rotación NO retira la clave de t0: su fecha máxima (2026-12-31) no ha vencido.
        f.Rotar();
        f.ClavesPublicasVigentes().Should().HaveCount(3);
        f.Verificar(t0).Should().NotBeNull("sus credenciales siguen vivas");
        f.Verificar(t1).Should().NotBeNull();

        // Pasada la mayor fecha máxima firmada, la siguiente rotación retira las anteriores.
        _reloj.Ahora = new DateTimeOffset(2027, 1, 2, 0, 0, 0, TimeSpan.Zero);
        f.Rotar();
        f.Verificar(t0).Should().BeNull("la clave de t0 ya no tiene credenciales vivas");
        f.Verificar(t1).Should().BeNull();
    }

    [Fact]
    public void Al_pasar_a_anterior_la_clave_pierde_su_privada_del_disco()
    {
        var f = Crear();
        UnToken(f);
        f.Rotar();

        var claves = LeerClavesDelFichero();
        claves.Should().HaveCount(2);
        claves.Count(c => c.GetProperty("PrivadaProtegida").GetString() != "").Should().Be(1);
    }

    [Fact]
    public void Dos_procesos_sobre_el_mismo_fichero_comparten_clave_y_ven_la_rotacion_del_otro()
    {
        var a = Crear();
        var b = Crear(); // otra instancia = otro proceso: caché propia, mismo fichero.

        b.Verificar(UnToken(Crear(Path.Combine(_directorio, "otro.json")))).Should().BeNull(); // b cachea «sin fichero»
        var tokenA = UnToken(a);
        b.Verificar(tokenA).Should().NotBeNull("b recarga el fichero que escribió a");
        UnToken(b); // b firma con la clave de a, no crea otra
        b.ClavesPublicasVigentes().Should().ContainSingle();

        a.RotarDeEmergencia();
        b.Verificar(tokenA).Should().BeNull("la emergencia de a vale para b");
        b.Rotar();
        b.Verificar(tokenA).Should().BeNull("la clave comprometida no vuelve por una caché vieja");
    }

    [Fact]
    public void Un_llavero_con_entradas_nulas_o_sin_kid_falla_cerrado_sin_excepcion_al_verificar()
    {
        Directory.CreateDirectory(_directorio);
        var token = UnToken(Crear(Path.Combine(_directorio, "otro.json")));
        foreach (var contenido in new[] { "{\"Version\":1,\"Claves\":[null]}", "{\"Version\":1,\"Claves\":[{\"Activa\":true}]}" })
        {
            File.WriteAllText(Fichero, contenido);
            Crear().Verificar(token).Should().BeNull();
        }
    }

    [Fact]
    public void Las_firmas_emitidas_tienen_s_baja_y_la_variante_s_alta_no_verifica()
    {
        var f = Crear();
        for (var i = 0; i < 40; i++)
        {
            var token = UnToken(f);
            var punto = token.LastIndexOf('.');
            var firma = FormatoTokenCredencial.DesdeBase64Url(token[(punto + 1)..])!;
            FormatoTokenCredencial.TieneSAlta(firma).Should().BeFalse();

            // (r, n−s): matemáticamente válida en ECDSA, pero no la aceptamos.
            var n = System.Numerics.BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551",
                System.Globalization.NumberStyles.HexNumber);
            var s = new System.Numerics.BigInteger(firma.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
            var alta = (byte[])firma.Clone();
            Array.Clear(alta, 32, 32);
            var bytes = (n - s).ToByteArray(isUnsigned: true, isBigEndian: true);
            bytes.CopyTo(alta, 64 - bytes.Length);
            f.Verificar(token[..(punto + 1)] + FormatoTokenCredencial.Base64Url(alta)).Should().BeNull();
        }
    }

    [Fact]
    public void Un_tope_desmesurado_se_recorta_y_todos_los_tokens_emitidos_verifican()
    {
        var f = Crear(tope: 100_000);
        var centros = Enumerable.Range(1, 95).Select(i => new CentroConFechaMaxima(
            Guid.Parse($"bbbbbbbb-0000-0000-0000-{i:D12}"), new DateOnly(2026, 12, 31))).ToList();

        var tokens = f.Firmar(new CredencialParaFirmar(CredencialId, 3, centros));

        tokens.Should().NotBeEmpty();
        tokens.Should().OnlyContain(t => t.Length <= 4096);
        tokens.Select(t => f.Verificar(t)).Should().OnlyContain(v => v != null);
        tokens.SelectMany(t => f.Verificar(t)!.Centros).Should().HaveCount(95);
    }

    [Fact]
    public void Una_clave_anterior_se_conserva_el_mismo_dia_de_su_fecha_maxima_y_se_retira_al_dia_siguiente()
    {
        var f = Crear();
        var t0 = UnToken(f); // 2026-12-31
        f.Rotar();
        f.Rotar();

        _reloj.Ahora = new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero);
        f.Rotar();
        f.Verificar(t0).Should().NotBeNull("el 31 la credencial aún vale");

        _reloj.Ahora = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        f.Rotar();
        f.Verificar(t0).Should().BeNull();
    }

    [Fact]
    public void Instancias_en_paralelo_sobre_el_mismo_fichero_no_pierden_la_primera_clave()
    {
        // Cada instancia hace de proceso distinto (caché propia); el cerrojo de fichero y la relectura impiden que dos
        // «primeras emisiones» creen dos claves y la segunda pise a la primera.
        var tokens = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8)
            .Select(_ => { var f = Crear(); return (f, token: UnToken(f)); }).ToList();

        var uno = Crear();
        tokens.Select(x => uno.Verificar(x.token)).Should().OnlyContain(v => v != null, "todos firmaron con la misma clave persistida");
        uno.ClavesPublicasVigentes().Should().ContainSingle();
    }

    private IReadOnlyList<System.Text.Json.JsonElement> LeerClavesDelFichero() =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(Fichero)).RootElement.GetProperty("Claves")
            .EnumerateArray().Select(e => e.Clone()).ToList();

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
    public void La_privada_va_protegida_y_sin_el_protector_ni_se_firma_ni_se_verifica()
    {
        var token = UnToken(Crear());

        var f = Crear(proteccion: new EphemeralDataProtectionProvider());

        f.Verificar(token).Should().BeNull("sin Data Protection no se puede comprobar la integridad del llavero");
        f.Invoking(x => x.Firmar(Credencial())).Should().Throw<InvalidOperationException>();

        // Lo guardado no es un PKCS#8 importable: va protegido.
        foreach (var clave in LeerClavesDelFichero())
        {
            var guardada = clave.GetProperty("PrivadaProtegida").GetString()!;
            using var ecdsa = ECDsa.Create();
            var bytes = Encoding.UTF8.GetBytes(guardada);
            ecdsa.Invoking(e => e.ImportPkcs8PrivateKey(bytes, out _)).Should().Throw<CryptographicException>();
        }
    }

    [Fact]
    public void Un_json_manipulado_con_una_publica_ajena_se_rechaza()
    {
        var f = Crear();
        UnToken(f);

        // El atacante genera su par, sustituye la pública del llavero y firma un token con su privada y el kid vigente.
        using var suya = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var kid = f.ClavesPublicasVigentes().Single().Kid;
        var actual = File.ReadAllText(Fichero);
        var spkiOriginal = f.ClavesPublicasVigentes().Single().SpkiBase64;
        File.WriteAllText(Fichero, actual.Replace(spkiOriginal, Convert.ToBase64String(suya.ExportSubjectPublicKeyInfo())));

        var cuerpo = FormatoTokenCredencial.Cuerpo(kid, CredencialId, 3, [new(Centro1, new DateOnly(2026, 12, 31))]);
        var firma = FormatoTokenCredencial.NormalizarSBaja(suya.SignData(
            Encoding.UTF8.GetBytes(cuerpo), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var falso = FormatoTokenCredencial.Componer(cuerpo, firma);

        Crear().Verificar(falso).Should().BeNull();
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
