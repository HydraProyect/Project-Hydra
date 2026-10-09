using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lo que la siembra administrativa del piloto Outbound comprueba sin tocar la base de
/// datos: opciones, precondiciones, direcciones de las cuentas y código de salida. Vive
/// aquí y no junto a los tests de integración del piloto porque no necesita PostgreSQL.
/// </summary>
public class PilotoOutboundAdministrativaPrecondicionesTests
{
    private const string MontajesDeUnVps =
        "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
        "31 30 0:25 / /dev/shm rw,nosuid,nodev shared:3 - tmpfs tmpfs rw\n";

    private const string Dominio = "demo-ejemplo.es";
    private const string Directorio = "/dev/shm/piloto";
    private const UnixFileMode SoloPropietario = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly IHostEnvironment Produccion = new Entorno("Production");

    private static IConfiguration Configuracion(params (string Clave, string? Valor)[] valores) =>
        new ConfigurationBuilder().AddInMemoryCollection(valores.ToDictionary(v => v.Clave, v => v.Valor)).Build();

    private static PilotoOutboundAdministrativa.Opciones Bien { get; } = new(
        Dominio, Directorio, "Production", new DateOnly(2026, 10, 20), ContactosPilotoOutbound.NoEntregables);

    private static Action Validar(IConfiguration configuracion, PilotoOutboundAdministrativa.Opciones opciones, UnixFileMode? modo = SoloPropietario) =>
        () => PilotoOutboundAdministrativa.ValidarPrecondiciones(configuracion, Produccion, opciones, MontajesDeUnVps, esLinux: true, modo);

    [Fact]
    public void Las_opciones_salen_de_sus_propias_claves_fuera_de_DatosPrueba()
    {
        var opciones = PilotoOutboundAdministrativa.LeerOpciones(Configuracion(
            ("PilotoOutbound:ConfirmarEntorno", "Production"), ("PilotoOutbound:DominioCorreo", Dominio),
            ("PilotoOutbound:DirectorioCredenciales", Directorio), ("PilotoOutbound:FechaDemostracion", "2026-10-20"),
            ("PilotoOutbound:CorreoContactos", "ensayo@destino.example")));

        opciones.Should().Be(new PilotoOutboundAdministrativa.Opciones(
            Dominio, Directorio, "Production", new DateOnly(2026, 10, 20),
            new ContactosPilotoOutbound("ensayo@destino.example", ContactosPilotoOutbound.DominioPorDefecto)));
        opciones.Contactos.DireccionDe("t1-centro").Should().Be("ensayo+t1-centro@destino.example");
    }

    [Fact]
    public void Sin_correo_de_contactos_la_agenda_va_al_dominio_no_entregable_de_la_siembra_local()
    {
        var opciones = PilotoOutboundAdministrativa.LeerOpciones(Configuracion(("PilotoOutbound:FechaDemostracion", "2026-10-20")));

        opciones.Contactos.Should().Be(ContactosPilotoOutbound.NoEntregables);
        opciones.Contactos.DireccionDe("t1-centro").Should().EndWith("@caemanager.local");
    }

    [Fact]
    public void Las_claves_de_la_siembra_local_no_alimentan_este_modo()
    {
        var soloLasLocales = Configuracion(
            (OpcionesPilotoOutbound.ClaveFechaDemostracion, "2026-10-20"),
            (OpcionesPilotoOutbound.ClaveCorreoContactos, "ensayo@destino.example"));

        var lectura = () => PilotoOutboundAdministrativa.LeerOpciones(soloLasLocales);

        lectura.Should().Throw<InvalidOperationException>("la fecha de DatosPrueba:PilotoOutbound no cuenta: este modo tiene la suya")
            .WithMessage("Falta PilotoOutbound:FechaDemostracion*");
    }

    [Theory]
    [InlineData(null, "Falta PilotoOutbound:FechaDemostracion*")]
    [InlineData("", "Falta PilotoOutbound:FechaDemostracion*")]
    [InlineData("20/10/2026", "PilotoOutbound:FechaDemostracion = «20/10/2026» no es una fecha*")]
    public void La_fecha_de_la_demostracion_es_obligatoria_y_no_se_asume_hoy(string? fecha, string mensaje)
    {
        var lectura = () => PilotoOutboundAdministrativa.LeerOpciones(Configuracion(("PilotoOutbound:FechaDemostracion", fecha)));

        lectura.Should().Throw<InvalidOperationException>().WithMessage(mensaje)
            .Which.Message.Should().NotContain("DatosPrueba", "el rechazo nombra la clave de ESTE modo, que es la que hay que corregir");
    }

    [Theory]
    [InlineData("sin-arroba")]
    [InlineData("dos@arrobas@destino.example")]
    [InlineData("con+etiqueta@destino.example")]
    public void Un_correo_de_contactos_que_no_es_una_sola_direccion_se_rechaza_con_la_clave_de_este_modo(string correo)
    {
        var lectura = () => PilotoOutboundAdministrativa.LeerOpciones(Configuracion(
            ("PilotoOutbound:FechaDemostracion", "2026-10-20"), ("PilotoOutbound:CorreoContactos", correo)));

        lectura.Should().Throw<InvalidOperationException>().WithMessage("PilotoOutbound:CorreoContactos no es una dirección utilizable*")
            .Which.Message.Should().NotContain("DatosPrueba");
    }

    [Fact]
    public void Con_todo_en_orden_las_precondiciones_pasan()
    {
        Validar(Configuracion(), Bien).Should().NotThrow("control positivo: sin él, los rechazos de abajo podrían ser de cualquier cosa");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Staging")]
    [InlineData("production")]
    [InlineData("Development")]
    public void El_entorno_tiene_que_confirmarse_a_mano_y_exacto(string confirmado)
    {
        Validar(Configuracion(), Bien with { EntornoConfirmado = confirmado })
            .Should().Throw<InvalidOperationException>().WithMessage("PilotoOutbound:ConfirmarEntorno debe ser exactamente*'Production'*");
    }

    [Theory]
    [InlineData("DatosPrueba:Activo")]
    [InlineData("DatosPrueba:PilotoOutbound:Activo")]
    public void No_se_mezcla_con_la_siembra_local_de_contrasena_compartida(string claveActiva)
    {
        Validar(Configuracion((claveActiva, "true")), Bien)
            .Should().Throw<InvalidOperationException>().WithMessage("*contraseña*compartida*desactívalos*");
    }

    [Theory]
    [InlineData("caemanager.local")]
    [InlineData("piloto.test")]
    [InlineData("Demo-Ejemplo.es")]
    [InlineData("")]
    public void El_dominio_de_las_cuentas_tiene_que_ser_real_y_controlable(string dominio)
    {
        Validar(Configuracion(), Bien with { DominioCorreo = dominio })
            .Should().Throw<InvalidOperationException>().WithMessage("PilotoOutbound:DominioCorreo*")
            .Which.Message.Should().NotContain("DemoDireccion", "el rechazo nombra la clave de ESTE modo");
    }

    [Fact]
    public void El_directorio_de_credenciales_tiene_que_estar_en_memoria_y_ser_solo_de_su_propietario()
    {
        Validar(Configuracion(), Bien with { DirectorioCredenciales = "/srv/piloto" })
            .Should().Throw<InvalidOperationException>().WithMessage("*ext4*memoria*");
        Validar(Configuracion(), Bien with { DirectorioCredenciales = "piloto" })
            .Should().Throw<InvalidOperationException>().WithMessage("PilotoOutbound:DirectorioCredenciales debe ser una ruta absoluta*");
        Validar(Configuracion(), Bien, SoloPropietario | UnixFileMode.GroupRead)
            .Should().Throw<InvalidOperationException>().WithMessage("*solo por su propietario*");
        Validar(Configuracion(), Bien, modo: null)
            .Should().Throw<InvalidOperationException>().WithMessage("*ilegible*");

        var fueraDeLinux = () => PilotoOutboundAdministrativa.ValidarPrecondiciones(
            Configuracion(), Produccion, Bien, informacionDeMontajes: null, esLinux: false, SoloPropietario);
        fueraDeLinux.Should().Throw<InvalidOperationException>().WithMessage("*solo se escribe en Linux*");
    }

    [Fact]
    public void Las_seis_cuentas_son_del_dominio_indicado_y_ninguna_del_dominio_local()
    {
        var cuentas = PilotoOutboundAdministrativa.CuentasDe(Dominio);

        cuentas.Todas.Should().HaveCount(6).And.OnlyHaveUniqueItems()
            .And.OnlyContain(c => c.EndsWith("@" + Dominio) && !c.Contains("caemanager.local"));
        cuentas.Todas.Select(c => c[..c.IndexOf('@')]).Should().Equal(
            CuentasPilotoOutbound.Locales.Todas.Select(c => c[..c.IndexOf('@')]),
            "misma parte local que en la siembra local: quien ensayó en local reconoce cada cuenta");
        cuentas.GestoraPrimera.Should().Be("gestora1.piloto@" + Dominio);
    }

    [Fact]
    public void La_confirmacion_de_entorno_es_la_misma_para_la_retirada()
    {
        var sinConfirmar = () => PilotoOutboundAdministrativa.ExigirEntornoConfirmado(null, Produccion);
        var confirmada = () => PilotoOutboundAdministrativa.ExigirEntornoConfirmado("Production", Produccion);

        sinConfirmar.Should().Throw<InvalidOperationException>().WithMessage("PilotoOutbound:ConfirmarEntorno debe ser exactamente*");
        confirmada.Should().NotThrow();
    }

    [Fact]
    public void Si_la_matriz_no_cuadra_el_modo_sale_con_1_y_dice_cada_discrepancia()
    {
        var discrepancia = "T2 «Tenant de ejemplo» · Inicio · Vencidos: medido 3, esperado 4";

        var (codigo, salida, errores) = Informar(Resultado(escribio: true, discrepancias: [discrepancia]));

        codigo.Should().Be(1);
        errores.Should().Contain("Autoverificación FALLIDA").And.Contain(discrepancia);
        salida.Should().Contain("Divergencia declarada: una divergencia del catálogo", "las divergencias declaradas se imprimen siempre");
        salida.Should().NotContain("lo medido es lo que la matriz declara");
    }

    [Fact]
    public void Si_la_matriz_cuadra_el_modo_sale_con_0_e_imprime_Tenants_cuentas_y_fichero_sin_secretos()
    {
        var (codigo, salida, errores) = Informar(Resultado(escribio: true, discrepancias: []));

        codigo.Should().Be(0);
        errores.Should().BeEmpty();
        salida.Should().Contain("Piloto Outbound sembrado en Production: 1 Tenants")
            .And.Contain("Tenant de ejemplo")
            .And.Contain("gestora1.piloto@" + Dominio).And.Contain("contraseña en el fichero: sí")
            .And.Contain("/dev/shm/piloto/credenciales.json")
            .And.Contain("Autoverificación: lo medido es lo que la matriz declara.");
    }

    [Fact]
    public void Si_no_escribio_y_los_datos_ya_no_son_los_de_la_matriz_lo_dice_y_tambien_sale_con_1()
    {
        var (codigo, salida, errores) = Informar(Resultado(escribio: false, discrepancias: ["una discrepancia"]));

        codigo.Should().Be(1, "la autoverificación se exige siempre: quien lanza la siembra espera el lote como la matriz lo declara");
        salida.Should().Contain("esta ejecución no ha escrito datos");
        errores.Should().Contain("retirar y sembrar");
    }

    [Fact]
    public void El_mensaje_de_una_siembra_interrumpida_dice_el_motivo_y_como_seguir()
    {
        var mensaje = PilotoOutboundAdministrativa.MensajeDeInterrupcion(new InvalidOperationException("el motivo exacto"));

        mensaje.Should().StartWith("Siembra del piloto Outbound interrumpida: el motivo exacto")
            .And.Contain("se reanuda repitiendo la orden").And.Contain("--retirar-piloto-outbound");
    }

    [Fact]
    public void Un_fallo_que_no_es_un_rechazo_previsto_tambien_dice_su_tipo_su_mensaje_y_como_seguir()
    {
        (Exception Fallo, string Motivo)[] fallos =
        [
            (new IOException("No queda espacio en el dispositivo."), "IOException: No queda espacio en el dispositivo."),
            (new UnauthorizedAccessException("Acceso denegado a la ruta."), "UnauthorizedAccessException: Acceso denegado a la ruta."),
            (new Microsoft.EntityFrameworkCore.DbUpdateException("No se pudieron guardar los cambios.", new TimeoutException("La base no respondió.")),
                "DbUpdateException: No se pudieron guardar los cambios. Causa: TimeoutException: La base no respondió."),
        ];

        foreach (var (fallo, motivo) in fallos)
        {
            PilotoOutboundAdministrativa.MensajeDeInterrupcion(fallo)
                .Should().StartWith("Siembra del piloto Outbound interrumpida: " + motivo)
                .And.Contain("se reanuda repitiendo la orden").And.Contain("--retirar-piloto-outbound")
                .And.Contain("credenciales, se conserva");

            PilotoOutboundRetirada.MensajeDeInterrupcion(fallo)
                .Should().StartWith("Retirada del piloto Outbound interrumpida: " + motivo)
                .And.Contain("«Retirado:»").And.Contain("se reanuda repitiendo la orden");
        }
    }

    [Fact]
    public void El_mensaje_de_una_interrupcion_no_lleva_la_traza_ni_los_datos_de_la_excepcion()
    {
        Exception lanzada;
        try
        {
            throw new IOException("fallo al escribir") { Data = { ["contenido"] = "Xk7#valor-que-no-debe-salir" } };
        }
        catch (IOException ex)
        {
            lanzada = ex;
        }

        lanzada.StackTrace.Should().NotBeNullOrEmpty("control positivo: la excepción lleva traza, así que su ausencia en el mensaje es del mensaje");

        foreach (var mensaje in new[]
                 {
                     PilotoOutboundAdministrativa.MensajeDeInterrupcion(lanzada),
                     PilotoOutboundRetirada.MensajeDeInterrupcion(lanzada),
                 })
        {
            mensaje.Should().Contain("IOException: fallo al escribir");
            mensaje.Should().NotContain("Xk7#valor-que-no-debe-salir", "Exception.Data no se imprime");
            mensaje.Should().NotContain(nameof(El_mensaje_de_una_interrupcion_no_lleva_la_traza_ni_los_datos_de_la_excepcion), "la traza no se imprime");
        }
    }

    [Fact]
    public void El_rechazo_de_la_retirada_dice_el_motivo_tal_cual_y_como_seguir()
    {
        PilotoOutboundRetirada.MensajeDeInterrupcion(new InvalidOperationException("el motivo exacto"))
            .Should().StartWith("Retirada del piloto Outbound interrumpida: el motivo exacto")
            .And.Contain("se reanuda repitiendo la orden");
    }

    private static (int Codigo, string Salida, string Errores) Informar(PilotoOutboundAdministrativa.Resultado resultado)
    {
        using var salida = new StringWriter();
        using var errores = new StringWriter();
        var codigo = PilotoOutboundAdministrativa.Informar(resultado, "Production", salida, errores);
        return (codigo, salida.ToString(), errores.ToString());
    }

    private static PilotoOutboundAdministrativa.Resultado Resultado(bool escribio, IReadOnlyList<string> discrepancias) => new(
        new PilotoOutboundSeeder.Resultado(escribio, escribio ? ["Tenant de ejemplo"] : [], 12, 12, TimeSpan.FromSeconds(3)),
        [("Tenant de ejemplo", Guid.NewGuid())],
        [new PilotoOutboundAdministrativa.CuentaSembrada("gestora1.piloto@" + Dominio, "GestorCae", "Operador de ejemplo", ContrasenaEntregada: true)],
        "/dev/shm/piloto/credenciales.json",
        new PilotoOutboundAutoverificacion.Informe([]),
        ["una divergencia del catálogo"], discrepancias);

    private sealed class Entorno(string nombre) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nombre;
        public string ApplicationName { get; set; } = "CaeManager.Web.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
