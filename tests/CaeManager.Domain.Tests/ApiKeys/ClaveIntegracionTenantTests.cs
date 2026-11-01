using CaeManager.Domain.ApiKeys;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.ApiKeys;

public class ClaveIntegracionTenantTests
{
    private static readonly string[] Lectura = [AlcancesIntegracion.ClientesLeer, AlcancesIntegracion.TrabajadoresLeer];

    private static ClaveIntegracionTenant Crear(IEnumerable<string>? alcances = null, DateTime? caducaEnUtc = null) =>
        new("ERP de nóminas", "ERP", "talveg_int_ab12", new string('a', 64), alcances ?? Lectura,
            caducaEnUtc ?? DateTime.UtcNow.AddDays(ClaveIntegracionTenant.DiasCaducidadPorDefecto), Guid.NewGuid());

    [Fact]
    public void Una_clave_nueva_autentica_hasta_su_caducidad()
    {
        var caduca = DateTime.UtcNow.AddDays(30);
        var clave = Crear(caducaEnUtc: caduca);

        clave.PuedeAutenticar(DateTime.UtcNow).Should().BeTrue();
        clave.PuedeAutenticar(caduca).Should().BeFalse();
    }

    [Fact]
    public void La_caducidad_es_obligatoria_y_no_supera_365_dias()
    {
        var pasada = () => Crear(caducaEnUtc: DateTime.UtcNow.AddMinutes(-1));
        var excesiva = () => Crear(caducaEnUtc: DateTime.UtcNow.AddDays(366));
        var local = () => Crear(caducaEnUtc: DateTime.SpecifyKind(DateTime.UtcNow.AddDays(1), DateTimeKind.Local));

        pasada.Should().Throw<ArgumentException>();
        excesiva.Should().Throw<ArgumentException>();
        local.Should().Throw<ArgumentException>();
        Crear(caducaEnUtc: DateTime.UtcNow.AddDays(364)).Should().NotBeNull();
    }

    [Fact]
    public void Ningun_alcance_se_concede_solo()
    {
        var accion = () => Crear(alcances: []);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("todo")]
    [InlineData("clientes.escribir")]
    [InlineData("documentos.salud.leer")]
    [InlineData("CLIENTES.LEER")]
    public void Un_alcance_desconocido_se_rechaza(string alcance)
    {
        var accion = () => Crear(alcances: [alcance]);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Un_alcance_repetido_se_rechaza()
    {
        var accion = () => Crear(alcances: [AlcancesIntegracion.ClientesLeer, AlcancesIntegracion.ClientesLeer]);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Los_datos_personales_no_valen_sin_la_lectura_de_trabajadores()
    {
        var sin = () => Crear(alcances: [AlcancesIntegracion.TrabajadoresDatosPersonalesLeer]);
        var con = () => Crear(alcances: [AlcancesIntegracion.TrabajadoresLeer, AlcancesIntegracion.TrabajadoresDatosPersonalesLeer]);

        sin.Should().Throw<ArgumentException>();
        con.Should().NotThrow();
    }

    [Fact]
    public void Ningun_alcance_da_acceso_a_datos_de_salud()
    {
        AlcancesIntegracion.Todos.Should().NotContain(a => a.Contains("salud", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revocar_corta_y_no_mueve_la_fecha_al_repetirse()
    {
        var clave = Crear();

        clave.Revocar();
        var fecha = clave.RevocadaEnUtc;
        clave.Revocar();

        clave.PuedeAutenticar(DateTime.UtcNow).Should().BeFalse();
        clave.RevocadaEnUtc.Should().Be(fecha);
    }

    [Fact]
    public void Suspender_corta_y_reactivar_devuelve_la_clave()
    {
        var clave = Crear();

        clave.Suspender("Clave publicada por error");

        clave.PuedeAutenticar(DateTime.UtcNow).Should().BeFalse();
        clave.MotivoSuspension.Should().Be("Clave publicada por error");

        clave.Reactivar();

        clave.PuedeAutenticar(DateTime.UtcNow).Should().BeTrue();
        clave.MotivoSuspension.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Suspender_exige_motivo(string motivo)
    {
        var accion = () => Crear().Suspender(motivo);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Una_clave_revocada_no_se_reactiva()
    {
        var clave = Crear();
        clave.Suspender("Incidente");
        clave.Revocar();

        var accion = clave.Reactivar;

        accion.Should().Throw<InvalidOperationException>();
        clave.PuedeAutenticar(DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public async Task Una_clave_caducada_no_se_reactiva()
    {
        var clave = Crear(caducaEnUtc: DateTime.UtcNow.AddMilliseconds(100));
        clave.Suspender("Incidente");
        await Task.Delay(150);

        var accion = clave.Reactivar;

        accion.Should().Throw<InvalidOperationException>();
        clave.MotivoSuspension.Should().Be("Incidente");
    }

    [Fact]
    public void Los_alcances_no_se_alteran_desde_fuera()
    {
        var clave = Crear();

        var accion = () => ((ICollection<string>)clave.Alcances).Add(AlcancesIntegracion.DocumentosLeer);

        accion.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Una_clave_invalidada_definitivamente_no_vuelve()
    {
        var clave = Crear();
        clave.InvalidarDefinitivamente();

        clave.PuedeAutenticar(DateTime.UtcNow).Should().BeFalse();

        var accion = clave.Reactivar;

        accion.Should().Throw<InvalidOperationException>();
        clave.PuedeAutenticar(DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Rotar_emite_una_sucesora_y_deja_la_antecesora_14_dias()
    {
        var antecesora = Crear();
        var emisor = Guid.NewGuid();
        var antes = DateTime.UtcNow;

        var sucesora = antecesora.EmitirSucesora("talveg_int_cd34", new string('b', 64), Lectura, DateTime.UtcNow.AddDays(180), emisor);

        sucesora.SucedeAClaveId.Should().Be(antecesora.Id);
        sucesora.CreadaPorUsuarioId.Should().Be(emisor);
        sucesora.Nombre.Should().Be(antecesora.Nombre);
        antecesora.CaducaEnUtc.Should().BeCloseTo(antes.AddDays(14), TimeSpan.FromMinutes(1));
        antecesora.PuedeAutenticar(DateTime.UtcNow).Should().BeTrue();
        antecesora.PuedeAutenticar(antes.AddDays(15)).Should().BeFalse();
    }

    [Fact]
    public void Rotar_nunca_alarga_la_vida_de_la_antecesora()
    {
        var caduca = DateTime.UtcNow.AddDays(3);
        var antecesora = Crear(caducaEnUtc: caduca);

        antecesora.EmitirSucesora("talveg_int_cd34", new string('b', 64), Lectura, DateTime.UtcNow.AddDays(180), Guid.NewGuid());

        antecesora.CaducaEnUtc.Should().Be(caduca);
    }

    [Fact]
    public void Rotar_puede_estrechar_los_alcances()
    {
        var sucesora = Crear().EmitirSucesora(
            "talveg_int_cd34", new string('b', 64), [AlcancesIntegracion.ClientesLeer], DateTime.UtcNow.AddDays(180), Guid.NewGuid());

        sucesora.Alcances.Should().Equal(AlcancesIntegracion.ClientesLeer);
    }

    [Fact]
    public void Rotar_no_amplia_los_alcances_ni_acorta_la_antecesora_si_falla()
    {
        var antecesora = Crear();
        var caduca = antecesora.CaducaEnUtc;

        var accion = () => antecesora.EmitirSucesora(
            "talveg_int_cd34", new string('b', 64), [.. Lectura, AlcancesIntegracion.DocumentosLeer], DateTime.UtcNow.AddDays(180), Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
        antecesora.CaducaEnUtc.Should().Be(caduca);
    }

    [Fact]
    public void Suspender_no_toca_una_clave_ya_cortada_para_siempre()
    {
        var clave = Crear();
        clave.Revocar();

        clave.Suspender("Incidente");

        clave.SuspendidaEnUtc.Should().BeNull();
        clave.MotivoSuspension.Should().BeNull();
    }

    [Theory]
    [InlineData("clave_ajena_ab12")]
    [InlineData("talveg_int_0123456789abcdef")]
    public void El_prefijo_visible_no_puede_llevar_el_secreto(string prefijo)
    {
        var accion = () => new ClaveIntegracionTenant(
            "ERP", "ERP", prefijo, new string('a', 64), Lectura, DateTime.UtcNow.AddDays(1), Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("TALVEG_INT_ESTO_NO_ES_UN_HASH_ESTO_NO_ES_UN_HASH_ESTO_NO_ES_UN_HA")]
    public void El_hash_debe_ser_SHA256_en_hexadecimal(string hash)
    {
        var accion = () => new ClaveIntegracionTenant(
            "ERP", "ERP", "talveg_int_ab12", hash, Lectura, DateTime.UtcNow.AddDays(1), Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    public static TheoryData<Action<ClaveIntegracionTenant>> CortesDeLaClave =>
    [
        c => c.Revocar(),
        c => c.Suspender("Incidente"),
        c => c.InvalidarDefinitivamente(),
    ];

    [Theory]
    [MemberData(nameof(CortesDeLaClave))]
    public void Solo_se_rota_una_clave_vigente(Action<ClaveIntegracionTenant> corte)
    {
        var antecesora = Crear();
        corte(antecesora);

        var accion = () => antecesora.EmitirSucesora("talveg_int_cd34", new string('b', 64), Lectura, DateTime.UtcNow.AddDays(180), Guid.NewGuid());

        accion.Should().Throw<InvalidOperationException>();
    }
}
