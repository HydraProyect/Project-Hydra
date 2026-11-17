using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las negativas de la siembra administrativa de la demo Inbound que no tocan la base: se comprueban antes de abrir el fichero
/// de credenciales y de escribir nada. La siembra en sí (Production, sin correo, contraseñas fuera del registro, idempotencia)
/// la prueba <c>InboundDemoAdministrativaTests</c> en la suite de integración.
/// </summary>
public class InboundDemoAdministrativaPrecondicionesTests
{
    private const string MontajesDeUnVps =
        "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
        "31 30 0:25 / /dev/shm rw,nosuid,nodev shared:3 - tmpfs tmpfs rw\n";

    private const string Dominio = "demo-ejemplo.es";
    private const string Directorio = "/dev/shm/inbound";
    private const UnixFileMode SoloPropietario = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly IHostEnvironment Produccion = new Entorno("Production");

    private static IConfiguration Configuracion(params (string Clave, string? Valor)[] valores) =>
        new ConfigurationBuilder().AddInMemoryCollection(valores.ToDictionary(v => v.Clave, v => v.Valor)).Build();

    private static InboundDemoAdministrativa.Opciones Bien { get; } = new(Dominio, Directorio, "Production", new DateOnly(2026, 11, 20));

    private static Action Validar(IConfiguration configuracion, InboundDemoAdministrativa.Opciones opciones, UnixFileMode? modo = SoloPropietario) =>
        () => InboundDemoAdministrativa.ValidarPrecondiciones(configuracion, Produccion, opciones, MontajesDeUnVps, esLinux: true, modo);

    [Fact]
    public void Las_opciones_salen_de_sus_propias_claves_fuera_de_DatosPrueba()
    {
        var opciones = InboundDemoAdministrativa.LeerOpciones(Configuracion(
            ("InboundDemo:ConfirmarEntorno", "Production"), ("InboundDemo:DominioCorreo", Dominio),
            ("InboundDemo:DirectorioCredenciales", Directorio), ("InboundDemo:FechaDemostracion", "2026-11-20")));

        opciones.Should().Be(Bien);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("20/11/2026")]
    [InlineData("mañana")]
    public void La_fecha_de_la_demostracion_es_obligatoria_y_con_formato_ISO(string? fecha)
    {
        var leer = () => InboundDemoAdministrativa.LeerOpciones(Configuracion(("InboundDemo:FechaDemostracion", fecha)));

        leer.Should().Throw<InvalidOperationException>().WithMessage("*InboundDemo:FechaDemostracion*");
    }

    [Fact]
    public void Con_todo_en_orden_no_se_niega()
    {
        Validar(Configuracion(), Bien).Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("production")]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void Sin_el_entorno_confirmado_exactamente_se_niega(string confirmado)
    {
        Validar(Configuracion(), Bien with { EntornoConfirmado = confirmado })
            .Should().Throw<InvalidOperationException>().WithMessage("*InboundDemo:ConfirmarEntorno*");
    }

    [Theory]
    [InlineData("DatosPrueba:Activo")]
    [InlineData("DatosPrueba:Inbound:Activo")]
    public void Con_la_siembra_local_activa_se_niega_porque_usaria_la_contrasena_compartida(string clave)
    {
        Validar(Configuracion((clave, "true")), Bien)
            .Should().Throw<InvalidOperationException>().WithMessage("*CredencialesDemo*");
    }

    [Theory]
    [InlineData("caemanager.local")]
    [InlineData("")]
    [InlineData("sin-punto")]
    public void Un_dominio_de_la_siembra_local_o_mal_formado_se_niega_con_la_clave_de_este_modo(string dominio)
    {
        Validar(Configuracion(), Bien with { DominioCorreo = dominio })
            .Should().Throw<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("InboundDemo:DominioCorreo") && !ex.Message.Contains("DemoDireccion"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("credenciales")]
    public void Un_directorio_que_no_es_una_ruta_absoluta_se_niega_con_la_clave_de_este_modo(string directorio)
    {
        Validar(Configuracion(), Bien with { DirectorioCredenciales = directorio })
            .Should().Throw<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("InboundDemo:DirectorioCredenciales") && !ex.Message.Contains("DemoDireccion"));
    }

    [Fact]
    public void Un_directorio_que_no_esta_en_memoria_se_niega()
    {
        Validar(Configuracion(), Bien with { DirectorioCredenciales = "/var/lib/credenciales" })
            .Should().Throw<InvalidOperationException>().WithMessage("*tmpfs*");
    }

    [Fact]
    public void Un_directorio_que_otros_pueden_leer_se_niega()
    {
        Validar(Configuracion(), Bien, SoloPropietario | UnixFileMode.GroupRead | UnixFileMode.OtherRead)
            .Should().Throw<InvalidOperationException>().WithMessage("*0700*");
    }

    [Fact]
    public void Fuera_de_Linux_se_niega()
    {
        var validar = () => InboundDemoAdministrativa.ValidarPrecondiciones(
            Configuracion(), Produccion, Bien, MontajesDeUnVps, esLinux: false, SoloPropietario);

        validar.Should().Throw<InvalidOperationException>().WithMessage("*Linux*");
    }

    [Fact]
    public void Las_cinco_cuentas_van_al_dominio_indicado_con_la_parte_local_de_la_siembra_local()
    {
        var cuentas = InboundDemoAdministrativa.CuentasDe(Dominio);

        cuentas.Todas.Should().HaveCount(5).And.OnlyContain(c => c.EndsWith("@" + Dominio, StringComparison.Ordinal));
        cuentas.Todas.Should().NotContain(c => c.Contains("caemanager.local"));
        cuentas.Todas.Select(c => c.Split('@')[0])
            .Should().Equal(CuentasInboundDemo.Locales.Todas.Select(c => c.Split('@')[0]));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(9, null)]
    [InlineData(-1, "anterior a hoy")]
    [InlineData(10, "más de 9 días")]
    public void La_fecha_sirve_si_es_hoy_o_queda_a_lo_sumo_nueve_dias(int dias, string? motivo)
    {
        var hoy = new DateOnly(2026, 11, 17);

        var resultado = InboundDemoAdministrativa.MotivoFechaNoUtilizable(hoy.AddDays(dias), hoy);

        if (motivo is null) resultado.Should().BeNull();
        else resultado.Should().Contain(motivo).And.Contain("InboundDemo:FechaDemostracion");
    }

    /// <summary>D7: el catálogo de staging no lleva tipos de salud y conserva todos los que usa el lote, con el sustituto.</summary>
    [Fact]
    public void El_catalogo_sin_salud_no_tiene_tipos_de_salud_y_conserva_los_del_lote()
    {
        var catalogo = TipoDocumentoSeedData.CrearCopiasParaTenantSinTiposDeSalud();
        var completo = TipoDocumentoSeedData.CrearCopiasParaTenant();

        completo.Should().Contain(t => t.RevelaSalud, "control positivo: el catálogo completo sí trae tipos de salud");
        catalogo.Should().NotContain(t => t.RevelaSalud || t.Sensibilidad == SensibilidadDocumental.CategoriaEspecialSalud);
        catalogo.Select(t => t.Nombre).Should().NotContain("Certificado de aptitud médica");
        catalogo.Select(t => t.Nombre).Should().Contain(
        [
            InboundDemoSeeder.TipoTrabajosEnAltura, "Entrega de EPI", "Formación PRL del puesto", "RNT",
            "Certificado de estar al corriente con la Seguridad Social", "Certificado de estar al corriente con Hacienda",
            "Seguro de Responsabilidad Civil",
        ]);
        catalogo.Should().HaveCount(completo.Count(t => !t.RevelaSalud));
    }

    [Fact]
    public void Informar_sale_con_0_si_el_lote_cuadra_y_con_1_si_no_y_nunca_escribe_una_contrasena()
    {
        var (codigo, salida, errores) = Informar(Resultado([]));
        codigo.Should().Be(0);
        salida.Should().Contain("Autoverificación: el lote es el que la siembra declara").And.Contain("/dev/shm/inbound/credenciales.json");
        errores.Should().BeEmpty();

        var (codigoMal, _, erroresMal) = Informar(Resultado(["«Montajes Moncayo S.L. (Inbound demo)» · Documentos: medido 8, esperado 9"]));
        codigoMal.Should().Be(1);
        erroresMal.Should().Contain("Autoverificación FALLIDA").And.Contain("Documentos: medido 8, esperado 9");
    }

    [Fact]
    public void El_mensaje_de_interrupcion_lleva_el_tipo_y_el_mensaje_y_como_retirar_sin_la_traza()
    {
        Exception excepcion;
        try { throw new IOException("disco lleno", new UnauthorizedAccessException("causa interna")); }
        catch (IOException ex) { excepcion = ex; }

        var mensaje = InboundDemoAdministrativa.MensajeDeInterrupcion(excepcion);

        mensaje.Should().Contain("IOException: disco lleno").And.Contain("Causa: UnauthorizedAccessException: causa interna")
            .And.Contain("--retirar-tenant-demo");
        InboundDemoAdministrativa.MensajeDeInterrupcion(new InvalidOperationException("rechazo previsto"))
            .Should().Contain("interrumpida: rechazo previsto", "un rechazo previsto va con su mensaje tal cual");
        mensaje.Should().NotContain(" at ").And.NotContain(nameof(El_mensaje_de_interrupcion_lleva_el_tipo_y_el_mensaje_y_como_retirar_sin_la_traza));
    }

    private static (int Codigo, string Salida, string Errores) Informar(InboundDemoAdministrativa.Resultado resultado)
    {
        using var salida = new StringWriter();
        using var errores = new StringWriter();
        var codigo = InboundDemoAdministrativa.Informar(resultado, "Production", salida, errores);
        return (codigo, salida.ToString(), errores.ToString());
    }

    private static InboundDemoAdministrativa.Resultado Resultado(IReadOnlyList<string> discrepancias) => new(
        true,
        [(InboundDemoSeeder.NombreTenantTce, Guid.NewGuid())],
        [new InboundDemoAdministrativa.CuentaSembrada("gestora.deltasur.inbound@" + Dominio, "GestorCae", InboundDemoSeeder.NombreTenantTce, ContrasenaEntregada: true)],
        "/dev/shm/inbound/credenciales.json",
        ["  Tenant TCE: contratos de acceso 7."],
        discrepancias);

    private sealed class Entorno(string nombre) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nombre;
        public string ApplicationName { get; set; } = "CaeManager.Web.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
