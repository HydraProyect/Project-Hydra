using System.Text.Json;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// La siembra administrativa de la demo a dirección (camino a un entorno real). Lo que se
/// mide aquí es lo que la separa de la siembra local: contraseñas únicas y ausentes de
/// todo log, un fichero que no puede ir a disco, un dominio que controla el propietario,
/// un lote con marcador que solo se retira entero, y ningún Tenant real tocado.
/// </summary>
public class SiembraDemoDireccionAdministrativaPurasTests
{
    private const string MontajesDeUnVps =
        "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
        "30 22 0:6 / /dev rw,nosuid shared:2 - devtmpfs devtmpfs rw\n" +
        "31 30 0:25 / /dev/shm rw,nosuid,nodev shared:3 - tmpfs tmpfs rw\n" +
        "40 22 8:2 / /var/lib/docker rw,relatime shared:4 - ext4 /dev/sdb1 rw\n" +
        "41 40 0:30 / /var/lib/docker/overlay2/x/merged rw - overlay overlay rw\n";

    [Theory]
    [InlineData("/dev/shm/demo", "tmpfs")]
    [InlineData("/dev/shm", "tmpfs")]
    [InlineData("/srv/backups/demo", "ext4")]
    [InlineData("/var/lib/docker/volumes/v/_data", "ext4")]
    [InlineData("/var/lib/docker/overlay2/x/merged/salida", "overlay")]
    [InlineData("/dev/shmx/demo", "devtmpfs")]
    public void El_tipo_de_sistema_de_ficheros_es_el_del_montaje_mas_especifico(string ruta, string esperado) =>
        SiembraDemoDireccionAdministrativa.TipoDeSistemaDeFicheros(ruta, MontajesDeUnVps).Should().Be(esperado);

    [Fact]
    public void Un_directorio_en_disco_se_rechaza_aunque_este_bien_protegido()
    {
        var llamada = () => SiembraDemoDireccionAdministrativa.ValidarDirectorioDeSalida(
            "/srv/backups/demo", MontajesDeUnVps, esLinux: true, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        llamada.Should().Throw<InvalidOperationException>().WithMessage("*ext4*memoria*",
            "MEDIDO: un directorio persistente puede acabar en un respaldo; solo se acepta memoria");
    }

    [Fact]
    public void Un_directorio_en_tmpfs_solo_del_propietario_se_acepta()
    {
        var llamada = () => SiembraDemoDireccionAdministrativa.ValidarDirectorioDeSalida(
            "/dev/shm/demo", MontajesDeUnVps, esLinux: true, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        llamada.Should().NotThrow("control positivo: sin él, los rechazos de arriba y de abajo podrían ser de cualquier cosa");
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute)]
    [InlineData((UnixFileMode)0x1FF)]
    public void Un_directorio_visible_para_otros_usuarios_se_rechaza(UnixFileMode modo)
    {
        var llamada = () => SiembraDemoDireccionAdministrativa.ValidarDirectorioDeSalida(
            "/dev/shm/demo", MontajesDeUnVps, esLinux: true, modo);

        llamada.Should().Throw<InvalidOperationException>().WithMessage("*solo por su propietario*");
    }

    [Fact]
    public void Un_modo_ilegible_o_una_ruta_relativa_o_otro_sistema_operativo_se_rechazan()
    {
        var sinModo = () => SiembraDemoDireccionAdministrativa.ValidarDirectorioDeSalida("/dev/shm/demo", MontajesDeUnVps, true, null);
        var relativa = () => SiembraDemoDireccionAdministrativa.ValidarDirectorioDeSalida("demo", MontajesDeUnVps, true, UnixFileMode.UserRead);
        var noLinux = () => SiembraDemoDireccionAdministrativa.ValidarDirectorioDeSalida("/dev/shm/demo", null, false, UnixFileMode.UserRead);

        sinModo.Should().Throw<InvalidOperationException>().WithMessage("*ilegible*");
        relativa.Should().Throw<InvalidOperationException>().WithMessage("*absoluta*");
        noLinux.Should().Throw<InvalidOperationException>().WithMessage("*solo se escribe en Linux*");
    }

    [Theory]
    [InlineData("ejemplo.es", true)]
    [InlineData("mail.ejemplo.com", true)]
    [InlineData("caemanager.local", false)]
    [InlineData("demo.local", false)]
    [InlineData("ejemplo.test", false)]
    [InlineData("Ejemplo.es", false)]
    [InlineData("sin-punto", false)]
    [InlineData("", false)]
    [InlineData("a b.es", false)]
    public void El_dominio_tiene_que_ser_real_y_no_local_ni_reservado(string dominio, bool valido)
    {
        var llamada = () => SiembraDemoDireccionAdministrativa.ValidarDominio(dominio);

        if (valido) llamada.Should().NotThrow();
        else llamada.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Las_contrasenas_son_de_24_caracteres_con_las_cuatro_clases_y_no_se_repiten()
    {
        var muestras = Enumerable.Range(0, 500).Select(_ => SiembraDemoDireccionAdministrativa.GenerarContrasena()).ToList();

        muestras.Should().OnlyContain(c => c.Length == 24);
        muestras.Should().OnlyContain(c =>
            c.Count(char.IsUpper) >= 2 && c.Count(char.IsLower) >= 2 && c.Count(char.IsDigit) >= 2 &&
            c.Count(x => "#%+-=?@_".Contains(x)) >= 2, "MEDIDO: cumple la política de Identity con margen");
        muestras.Distinct().Should().HaveCount(500, "MEDIDO: ninguna repetición en 500 (una distinta por cuenta)");
        muestras.Should().NotContain(CredencialesDemo.ContrasenaPorDefecto);
    }

    [Fact]
    public void Las_precondiciones_exigen_el_entorno_confirmado_y_ni_una_de_las_claves_de_la_siembra_local()
    {
        var entorno = new EntornoDePrueba("Production");
        var vacia = new ConfigurationBuilder().Build();
        var conLocal = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DatosPrueba:Activo"] = "true" }).Build();
        var directorio = "/dev/shm/demo";
        var modo = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var bien = new SiembraDemoDireccionAdministrativa.Opciones("ejemplo.es", directorio, "Production");

        var todoBien = () => SiembraDemoDireccionAdministrativa.ValidarPrecondiciones(vacia, entorno, bien, MontajesDeUnVps, true, modo);
        var otroEntorno = () => SiembraDemoDireccionAdministrativa.ValidarPrecondiciones(
            vacia, entorno, bien with { EntornoConfirmado = "Staging" }, MontajesDeUnVps, true, modo);
        var mezclada = () => SiembraDemoDireccionAdministrativa.ValidarPrecondiciones(conLocal, entorno, bien, MontajesDeUnVps, true, modo);

        todoBien.Should().NotThrow("control positivo");
        otroEntorno.Should().Throw<InvalidOperationException>().WithMessage("*entorno en el que se ejecuta*");
        mezclada.Should().Throw<InvalidOperationException>().WithMessage("*contraseña compartida*");
    }

    [Fact]
    public async Task Sembrar_rechaza_un_directorio_que_no_es_memoria_y_no_deja_ni_un_Tenant()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        var directorio = Directory.CreateTempSubdirectory("demo-direccion-").FullName;

        try
        {
            var llamada = () => SiembraDemoDireccionAdministrativa.SembrarAsync(
                sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
                arnes.Servicios.GetRequiredService<IConfiguration>(), new EntornoDePrueba("Production"),
                new SiembraDemoDireccionAdministrativa.Opciones("ejemplo.es", directorio, "Production"),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            await llamada.Should().ThrowAsync<InvalidOperationException>(
                "MEDIDO: un directorio temporal está en disco (o en un SO donde no se puede comprobar): la operación entera se niega");

            await using var bootstrap = arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
            (await bootstrap.Tenants.CountAsync(t => SiembraDemoDireccionAdministrativa.NombresTenantsDelLote.Contains(t.Nombre)))
                .Should().Be(0, "MEDIDO: el rechazo es anterior a toda escritura");
            Directory.GetFiles(directorio).Should().BeEmpty("no se crea el fichero de credenciales");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }
}

/// <summary>Captura todo lo que se registra, para poder afirmar que ninguna contraseña llega a un log.</summary>
internal sealed class LoggerDeCaptura : ILogger
{
    public List<string> Mensajes { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Mensajes.Add(formatter(state, exception));
        if (exception is not null) Mensajes.Add(exception.ToString());
    }
}

/// <summary>La siembra real sobre el arnés de runtime (RLS efectiva, interceptores de producción).</summary>
public class SiembraDemoDireccionAdministrativaSobreBaseTests
{
    private const string Dominio = "demo-ejemplo.es";

    private static SiembraDemoDireccionAdministrativa.Opciones Opciones(string directorio) => new(Dominio, directorio, "Production");

    private static async Task<(SiembraDemoDireccionAdministrativa.Resultado Resultado, LoggerDeCaptura Log)> SembrarAsync(
        ArnesDeArranqueRuntime arnes, string directorio)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        var log = new LoggerDeCaptura();
        var resultado = await SiembraDemoDireccionAdministrativa.EjecutarAsync(
            sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
            new EntornoDePrueba("Production"), Opciones(directorio), log, CancellationToken.None);
        return (resultado, log);
    }

    private static string Directorio() => Directory.CreateTempSubdirectory("demo-direccion-").FullName;

    [Fact]
    public async Task Siembra_siete_tenants_marcados_y_cuatro_cuentas_con_contrasena_unica_que_no_llega_a_ningun_log()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var directorio = Directorio();
        try
        {
            var (resultado, log) = await SembrarAsync(arnes, directorio);

            await using var bootstrap = arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
            var tenants = await bootstrap.Tenants.Where(t => SiembraDemoDireccionAdministrativa.NombresTenantsDelLote.Contains(t.Nombre)).ToListAsync();
            tenants.Should().HaveCount(7, "MEDIDO: seis ramas y el Operador CAE");
            tenants.Should().OnlyContain(t => t.DatosDemoCompletadosEnUtc != null, "MEDIDO: todos llevan el marcador de demo");

            (await bootstrap.Empresas.IgnoreQueryFilters().CountAsync(e => e.EsCritico != null))
                .Should().Be(CatalogoEscenariosDireccionDemo.Ramas.Sum(r => r.Clientes.Count), "la misma matriz que la demo local");

            var credenciales = JsonDocument.Parse(await File.ReadAllTextAsync(resultado.FicheroCredenciales))
                .RootElement.GetProperty("cuentas").EnumerateArray()
                .Select(c => (Email: c.GetProperty("email").GetString()!, Contrasena: c.GetProperty("contrasena").GetString()!))
                .ToList();

            credenciales.Should().HaveCount(4);
            credenciales.Select(c => c.Contrasena).Distinct().Should().HaveCount(4, "MEDIDO: una contraseña distinta por cuenta");
            credenciales.Should().OnlyContain(c => c.Email.EndsWith("@" + Dominio) && !c.Email.EndsWith("@caemanager.local"));
            credenciales.Should().NotContain(c => c.Contrasena == CredencialesDemo.ContrasenaPorDefecto, "nunca la contraseña compartida");

            using var ambito = arnes.Servicios.CreateScope();
            var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var (email, contrasena) in credenciales)
            {
                var usuario = await userManager.FindByEmailAsync(email);
                usuario.Should().NotBeNull();
                (await userManager.CheckPasswordAsync(usuario!, contrasena)).Should().BeTrue($"MEDIDO: {email} entra con SU contraseña del fichero");
                (await userManager.CheckPasswordAsync(usuario!, CredencialesDemo.ContrasenaPorDefecto)).Should().BeFalse();
                usuario!.LockoutEnd.Should().BeNull("las cuentas nuevas no están bloqueadas; las 35 heredadas no se tocan");
            }

            var registrado = string.Join("\n", log.Mensajes);
            registrado.Should().NotBeEmpty("control positivo: el instrumento sí recoge lo que se registra");
            foreach (var (_, contrasena) in credenciales)
                registrado.Should().NotContain(contrasena, "MEDIDO: ninguna contraseña llega a un log");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    [Fact]
    public async Task En_Linux_el_fichero_de_credenciales_nace_con_permisos_0600()
    {
        // Solo observable en Linux (CI y el servidor): en Windows el modo Unix no existe y el test no puede
        // medir nada, así que no cuenta como evidencia allí. Aquí no se puede dar por «pasado» en local.
        if (!OperatingSystem.IsLinux()) return;

        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var directorio = Directorio();
        try
        {
            var (resultado, _) = await SembrarAsync(arnes, directorio);

            File.GetUnixFileMode(resultado.FicheroCredenciales).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                "MEDIDO: 0600 desde la creación, no después: ninguna ventana con la contraseña legible por otros");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    [Fact]
    public async Task Sembrar_dos_veces_no_duplica_ni_reentrega_contrasenas()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var directorio = Directorio();
        try
        {
            var (primera, _) = await SembrarAsync(arnes, directorio);
            await using var bootstrap = arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
            var empresas = await bootstrap.Empresas.IgnoreQueryFilters().CountAsync();
            var tenants = await bootstrap.Tenants.CountAsync();

            var (segunda, _) = await SembrarAsync(arnes, directorio);

            (await bootstrap.Empresas.IgnoreQueryFilters().CountAsync()).Should().Be(empresas, "MEDIDO: idempotente");
            (await bootstrap.Tenants.CountAsync()).Should().Be(tenants);
            primera.Cuentas.Should().OnlyContain(c => c.ContrasenaEntregada);
            segunda.Cuentas.Should().OnlyContain(c => !c.ContrasenaEntregada, "MEDIDO: no se rotan ni se reentregan contraseñas de cuentas existentes");
            segunda.Cuentas.Select(c => c.Id).Should().BeEquivalentTo(primera.Cuentas.Select(c => c.Id));
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    [Fact]
    public async Task Un_Tenant_real_con_el_nombre_de_uno_del_lote_no_se_toca_ni_se_retira()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var directorio = Directorio();
        try
        {
            var nombreReal = SiembraDemoDireccionAdministrativa.Ramas[0].NombreTenant;
            Guid idReal;
            using (var ambito = arnes.Servicios.CreateScope())
            {
                var db = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
                var real = new Tenant(nombreReal, PerfilVocabularioTenant.ClienteDirecto);
                idReal = real.Id;
                using (CaeManager.Application.Common.AmbitoTenantExplicito.Establecer(real.Id))
                {
                    db.Tenants.Add(real);
                    await db.SaveChangesAsync();
                }
            }

            var siembra = () => SembrarAsync(arnes, directorio);
            await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage("*SIN el marcador*",
                "MEDIDO: un Tenant con el nombre del lote y sin marcador es un Tenant real: no se siembra en él");

            using (var ambito = arnes.Servicios.CreateScope())
            {
                var db = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
                var retirar = () => RetiradaTenantDemoService.ValidarTenantRetirableAsync(db, idReal);
                await retirar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*NO lleva el marcador*",
                    "MEDIDO: el nombre solo no basta para retirar un Tenant sin sufijo «demo»");
            }

            await using var bootstrap = arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
            (await bootstrap.Tenants.CountAsync(t => SiembraDemoDireccionAdministrativa.NombresTenantsDelLote.Contains(t.Nombre)))
                .Should().Be(1, "MEDIDO: no se creó ningún otro Tenant del lote: la negativa fue previa a escribir");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    [Fact]
    public async Task La_retirada_del_lote_lo_borra_entero_con_sus_cuentas_y_es_repetible()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var directorio = Directorio();
        try
        {
            await SembrarAsync(arnes, directorio);

            // LIMITACIÓN CONOCIDA DEL ARNÉS, no de la retirada (ver RetiradaTenantDemoServiceTests, test omitido): con
            // claves de protección de datos efímeras, RELEER columnas cifradas por el contexto de bootstrap lanza
            // CryptographicException. Los canales de gestión documental llevan una credencial cifrada; se borran
            // antes (ExecuteDelete no lee) para que este test mida el resto de la retirada. El binario real
            // (claves persistentes) queda para el ensayo del runbook, que es donde se ve de verdad.
            await using (var limpieza = arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear())
                await limpieza.CanalesGestionDocumental.IgnoreQueryFilters().ExecuteDeleteAsync();

            using (var ambito = arnes.Servicios.CreateScope())
            {
                var retirados = await SiembraDemoDireccionAdministrativa.RetirarLoteAsync(
                    ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>(),
                    () => arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

                retirados.Select(r => r.NombreTenant).Should().Equal(
                    SiembraDemoDireccionAdministrativa.NombresTenantsDelLote,
                    "MEDIDO: las seis ramas primero y el Operador CAE el último");
            }

            await using var bootstrap = arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
            (await bootstrap.Tenants.CountAsync(t => SiembraDemoDireccionAdministrativa.NombresTenantsDelLote.Contains(t.Nombre))).Should().Be(0);
            (await bootstrap.Empresas.IgnoreQueryFilters().CountAsync(e => e.EsCritico != null)).Should().Be(0, "MEDIDO: sin filas huérfanas");
            (await bootstrap.Users.CountAsync(u => u.Email!.EndsWith("@" + Dominio))).Should().Be(0, "MEDIDO: las cuentas se van con su Tenant");
            (await bootstrap.AsignacionesCartera.CountAsync()).Should().Be(0);

            using var otra = arnes.Servicios.CreateScope();
            var otraVez = await SiembraDemoDireccionAdministrativa.RetirarLoteAsync(
                otra.ServiceProvider.GetRequiredService<CaeManagerDbContext>(),
                () => arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            otraVez.Should().BeEmpty("MEDIDO: repetir la retirada no falla ni borra nada más");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    [Fact]
    public async Task Si_un_Tenant_del_lote_no_supera_la_validacion_no_se_retira_ninguno()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var directorio = Directorio();
        try
        {
            await SembrarAsync(arnes, directorio);

            await using (var bootstrap = arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear())
            {
                var operador = await bootstrap.Tenants.SingleAsync(t => t.Nombre == SiembraDemoDireccionAdministrativa.NombreTenantOperador);
                bootstrap.Entry(operador).Property(nameof(Tenant.DatosDemoCompletadosEnUtc)).CurrentValue = null;
                using (CaeManager.Application.Common.AmbitoTenantExplicito.Establecer(operador.Id))
                    await bootstrap.SaveChangesAsync();
            }

            using var ambito = arnes.Servicios.CreateScope();
            var retirar = () => SiembraDemoDireccionAdministrativa.RetirarLoteAsync(
                ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>(),
                () => arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            await retirar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*NO lleva el marcador*");

            await using var comprobar = arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
            (await comprobar.Tenants.CountAsync(t => SiembraDemoDireccionAdministrativa.NombresTenantsDelLote.Contains(t.Nombre)))
                .Should().Be(7, "MEDIDO: valida todo antes de elevar; si uno falla, no se retira ninguno");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }
}
