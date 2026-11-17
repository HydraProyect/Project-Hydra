using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// Una base nueva con la composición real (la del arnés del piloto Outbound: tráfico como cae_app_runtime, almacén en disco
/// y un espía de <see cref="IEmailService"/> registrado el último) sobre la que se siembra la demo Inbound por la vía
/// administrativa en <c>Production</c>, una vez en la inicialización y otra en el test de la repetición.
/// </summary>
public sealed class InboundDemoAdministrativaFixture : IAsyncLifetime
{
    public const string Dominio = "demo-ejemplo.es";

    internal ArnesPilotoOutbound Arnes { get; private set; } = null!;
    internal InboundDemoAdministrativa.Resultado Primera { get; private set; } = null!;
    internal LoggerDeCaptura LogPrimera { get; } = new();
    internal string SalidaPrimera { get; private set; } = string.Empty;
    internal string ContenidoFicheroPrimera { get; private set; } = string.Empty;
    internal string Directorio { get; } = Directory.CreateTempSubdirectory("inbound-demo-credenciales-").FullName;

    internal static InboundDemoAdministrativa.Opciones Opciones(string directorio) =>
        new(Dominio, directorio, "Production", ArnesPilotoOutbound.FechaDemostracion());

    public async Task InitializeAsync()
    {
        Arnes = await ArnesPilotoOutbound.CrearAsync();
        Primera = await SembrarAsync(Opciones(Directorio), LogPrimera);
        ContenidoFicheroPrimera = await File.ReadAllTextAsync(Primera.FicheroCredenciales);

        using var salida = new StringWriter();
        using var errores = new StringWriter();
        InboundDemoAdministrativa.Informar(Primera, "Production", salida, errores);
        SalidaPrimera = salida + errores.ToString();
    }

    internal Task<InboundDemoAdministrativa.Resultado> SembrarAsync(InboundDemoAdministrativa.Opciones opciones, ILogger logger) =>
        SembrarEnAsync(Arnes, opciones, logger);

    /// <summary>La siembra administrativa en Production sobre la base de <paramref name="arnes"/>, juzgando la fecha con <paramref name="hoy"/>.</summary>
    internal static async Task<InboundDemoAdministrativa.Resultado> SembrarEnAsync(
        ArnesPilotoOutbound arnes, InboundDemoAdministrativa.Opciones opciones, ILogger logger, DateOnly? hoy = null)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;

        return await InboundDemoAdministrativa.EjecutarAsync(
            sp.GetRequiredService<CaeManagerDbContext>(),
            () => sp.GetRequiredService<FabricaContextoDeBootstrap>().Crear(),
            sp.GetRequiredService<UserManager<ApplicationUser>>(), sp.GetRequiredService<IUserStore<ApplicationUser>>(),
            sp.GetRequiredService<IFileStorageService>(), new ConfigurationBuilder().Build(), new EntornoDePrueba("Production"),
            opciones, hoy ?? DiaDeNegocio.Hoy(), logger, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await Arnes.DisposeAsync();
        if (Directory.Exists(Directorio))
            Directory.Delete(Directorio, recursive: true);
    }
}

/// <summary>
/// <b>La vía administrativa de la demo Inbound siembra en Production el lote de Development sin la contraseña compartida y
/// sin enviar un correo.</b> Lo que aquí se mide es lo que la separa de la siembra local: contraseñas únicas, solo en el
/// fichero y ausentes del registro y de la salida; cuentas de un dominio propio; ningún tipo de documento de salud (D7);
/// ningún intento de correo; la autoverificación sin discrepancias, y una repetición que no escribe ni reentrega
/// contraseñas. Las negativas que no tocan la base están en <c>InboundDemoAdministrativaPrecondicionesTests</c>; las que la
/// leen, y el control negativo de la autoverificación, en <see cref="InboundDemoAdministrativaNegativasTests"/>.
/// </summary>
public class InboundDemoAdministrativaTests(InboundDemoAdministrativaFixture fixture) : IClassFixture<InboundDemoAdministrativaFixture>
{
    private ArnesPilotoOutbound Arnes => fixture.Arnes;

    [Fact]
    public void Siembra_el_lote_entero_y_la_autoverificacion_no_encuentra_discrepancias()
    {
        fixture.Primera.Escribio.Should().BeTrue();
        fixture.Primera.Tenants.Select(t => t.Nombre).Should().BeEquivalentTo(InboundDemoSeeder.NombresTenants);
        fixture.Primera.Discrepancias.Should().BeEmpty();
        fixture.SalidaPrimera.Should().Contain("Autoverificación: el lote es el que la siembra declara");
    }

    [Fact]
    public void No_intenta_enviar_ningun_correo()
    {
        Arnes.Correo.Intentos.Should().BeEmpty(
            "en staging el SMTP es real y no tiene guarda: ni el alta de los Tenants ni el de las cuentas ni la línea Inbound escriben a nadie");
    }

    [Fact]
    public async Task Las_cuentas_son_del_dominio_propio_con_una_contrasena_distinta_que_solo_esta_en_el_fichero()
    {
        using var json = JsonDocument.Parse(fixture.ContenidoFicheroPrimera);
        var cuentas = json.RootElement.GetProperty("cuentas").EnumerateArray()
            .Select(c => (Email: c.GetProperty("email").GetString()!, Contrasena: c.GetProperty("contrasena").GetString()!))
            .ToList();

        cuentas.Should().HaveCount(5).And.OnlyContain(c => c.Email.EndsWith("@" + InboundDemoAdministrativaFixture.Dominio));
        cuentas.Select(c => c.Contrasena).Should().OnlyHaveUniqueItems().And.NotContain(c => string.IsNullOrEmpty(c));
        fixture.Primera.Cuentas.Should().OnlyContain(c => c.ContrasenaEntregada);

        var registro = string.Join("\n", fixture.LogPrimera.Mensajes);
        registro.Should().NotBeNullOrWhiteSpace("control positivo: el registro captura los mensajes de la siembra");
        foreach (var (email, contrasena) in cuentas)
        {
            registro.Should().NotContain(contrasena);
            fixture.SalidaPrimera.Should().NotContain(contrasena).And.Contain(email);
        }

        using var ambito = Arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        using var identificacion = AmbitoIdentificacionSinTenant.Abrir();
        foreach (var (email, contrasena) in cuentas)
        {
            var usuario = await userManager.FindByEmailAsync(email);
            usuario.Should().NotBeNull();
            usuario!.EmailConfirmed.Should().BeTrue("la cuenta nace confirmada: no hace falta un correo de confirmación");
            (await userManager.CheckPasswordAsync(usuario, contrasena)).Should().BeTrue($"la contraseña del fichero es la de {email}");
        }
    }

    [Fact]
    public async Task Ningun_Tenant_del_lote_tiene_tipos_de_documento_de_salud()
    {
        var conSalud = await Arnes.ComoBootstrapAsync(async b =>
        {
            var nombres = InboundDemoSeeder.NombresTenants.ToList();
            var ids = await b.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
            ids.Should().HaveCount(4, "control positivo: el instrumento ve los Tenants del lote");
            (await b.TiposDocumento.IgnoreQueryFilters().CountAsync(t => ids.Contains(t.TenantId))).Should().BePositive(
                "control positivo: el instrumento ve el catálogo de esos Tenants, así que un cero de salud es una ausencia");
            return await b.TiposDocumento.IgnoreQueryFilters()
                .CountAsync(t => ids.Contains(t.TenantId) && t.Sensibilidad == SensibilidadDocumental.CategoriaEspecialSalud);
        });

        conSalud.Should().Be(0, "D7: los tipos de salud solo existen en Development");
    }

    [Fact]
    public async Task Repetir_la_orden_no_escribe_ni_vuelve_a_entregar_contrasenas()
    {
        var directorio = Directory.CreateTempSubdirectory("inbound-demo-credenciales-").FullName;
        try
        {
            var segunda = await fixture.SembrarAsync(InboundDemoAdministrativaFixture.Opciones(directorio), new LoggerDeCaptura());

            segunda.Escribio.Should().BeFalse();
            segunda.Discrepancias.Should().BeEmpty();
            segunda.Cuentas.Should().HaveCount(5).And.OnlyContain(c => !c.ContrasenaEntregada);

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(segunda.FicheroCredenciales));
            json.RootElement.GetProperty("cuentas").EnumerateArray()
                .Should().OnlyContain(c => c.GetProperty("contrasena").ValueKind == JsonValueKind.Null);
            Arnes.Correo.Intentos.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    /// <summary>La fecha solo se exige si queda algo por sembrar: con el lote entero, una fecha ya pasada no lo niega.</summary>
    [Fact]
    public async Task Con_el_lote_entero_una_fecha_pasada_no_se_niega_y_no_escribe()
    {
        var directorio = Directory.CreateTempSubdirectory("inbound-demo-credenciales-").FullName;
        try
        {
            var hoy = DiaDeNegocio.Hoy();
            InboundDemoAdministrativa.MotivoFechaNoUtilizable(hoy.AddDays(-1), hoy).Should().NotBeNull(
                "control: con algo pendiente, esta fecha se negaría");

            var repeticion = await InboundDemoAdministrativaFixture.SembrarEnAsync(
                Arnes, InboundDemoAdministrativaFixture.Opciones(directorio) with { FechaDemostracion = hoy.AddDays(-1) },
                new LoggerDeCaptura(), hoy);

            repeticion.Escribio.Should().BeFalse();
            repeticion.Discrepancias.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }
}

/// <summary>
/// <b>Las negativas de la vía administrativa que leen la base se niegan antes de escribir nada</b>, cada una en una base
/// nueva: un nombre del lote ocupado por un Tenant sin marcador de demo, un Tenant del lote con datos de otra versión, una
/// fecha de demostración pasada con algo pendiente y una cuenta del lote que pertenece a otro Tenant. En cada caso se mide,
/// con la identidad de bootstrap y sin filtros, que la base queda igual, que no se crea el fichero de credenciales ni un
/// fichero en el almacén y que no se intenta ningún correo.
/// <para>
/// Código de salida: la negativa es una <see cref="InvalidOperationException"/> que sale de <c>EjecutarAsync</c>; el único
/// <c>catch (Exception)</c> del modo en <c>Program.cs</c> la convierte en <c>Environment.ExitCode = 1</c> con
/// <see cref="InboundDemoAdministrativa.MensajeDeInterrupcion"/>, y eso lo fija el test de arquitectura
/// <c>InboundDemoSoloDesdeElModoCliTests.Cualquier_excepcion_del_modo_termina_con_codigo_distinto_de_cero_y_diciendo_como_reanudar</c>.
/// Aquí se prueba la mitad que depende de la base: que la negativa lanza, con su motivo, y no escribe.
/// </para>
/// <para>
/// Y el control negativo de la autoverificación: sin la línea Inbound del Tenant TCE, <c>MedirAsync</c> da la discrepancia
/// e <see cref="InboundDemoAdministrativa.Informar"/> devuelve 1.
/// </para>
/// </summary>
public class InboundDemoAdministrativaNegativasTests
{
    private const string Dominio = InboundDemoAdministrativaFixture.Dominio;

    [Fact]
    public async Task Un_nombre_del_lote_ocupado_por_un_Tenant_sin_marcador_de_demo_se_niega_sin_escribir()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        await CrearTenantAsync(arnes, InboundDemoSeeder.NombreTenantTce, conMarcador: false);

        await SeNiegaSinEscribirAsync(
            arnes, "*sin el marcador de datos de demo*",
            antes => antes["Tenants del lote"].Should().Be(1, "control positivo: el instrumento ve el Tenant que ocupa el nombre"));
    }

    [Fact]
    public async Task Un_Tenant_del_lote_con_datos_de_otra_version_se_niega_sin_escribir()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var moncayo = await CrearTenantAsync(arnes, InboundDemoSeeder.NombreTenantMoncayo, conMarcador: true);

        // Otra versión: el Tenant TEC tiene una Empresa, pero ninguna con el CIF que esta versión le da.
        var deOtraVersion = DatosPruebaSeeder.GenerarCifValido(9_912_345);
        deOtraVersion.Should().NotBe(InboundDemoSeeder.CifDeTec(0), "control: el CIF que se pone no es el que esta versión espera");
        await arnes.EnTenantAsync(moncayo, async (db, _) =>
        {
            db.Empresas.Add(new CaeManager.Domain.Empresas.Empresa("Montajes de otra versión S.L.", deOtraVersion));
            return await db.SaveChangesAsync();
        });

        await SeNiegaSinEscribirAsync(
            arnes, "*datos de otra versión*",
            antes =>
            {
                antes["Tenants del lote"].Should().Be(1, "control positivo: el Tenant del lote existe y los otros tres están pendientes");
                antes["Empresas del lote"].Should().Be(1, "control positivo: el instrumento ve la Empresa de otra versión");
            });
    }

    [Fact]
    public async Task Una_fecha_de_demostracion_pasada_con_algo_pendiente_se_niega_sin_escribir()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var hoy = DiaDeNegocio.Hoy();

        await SeNiegaSinEscribirAsync(
            arnes, "*es anterior a hoy*no escribe nada*",
            antes => antes["Tenants del lote"].Should().Be(0, "control: no hay nada sembrado, así que todo el lote está pendiente"),
            fecha: hoy.AddDays(-1), hoy: hoy);
    }

    [Fact]
    public async Task Una_cuenta_del_lote_que_pertenece_a_otro_Tenant_se_niega_sin_escribir()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var ajeno = await CrearTenantAsync(arnes, "Tenant ajeno a la demo Inbound (prueba)", conMarcador: false);
        var correo = InboundDemoAdministrativa.CuentasDe(Dominio).AdministradorTce;

        using (var ambito = arnes.Servicios.CreateScope())
        using (AmbitoTenantExplicito.Establecer(ajeno))
        {
            var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = new ApplicationUser { UserName = correo, Email = correo, NombreCompleto = correo, TenantId = ajeno };
            (await userManager.CreateAsync(cuenta)).Succeeded.Should().BeTrue("control: la cuenta ajena se crea");
        }

        await SeNiegaSinEscribirAsync(
            arnes, "*NO pertenecen al Tenant que les corresponde*",
            antes =>
            {
                antes["Cuentas del lote"].Should().Be(1, "control positivo: el instrumento ve la cuenta ajena con el correo del lote");
                antes["Tenants del lote"].Should().Be(0);
            });
    }

    [Fact]
    public async Task Un_Tenant_del_lote_con_tipos_de_documento_de_salud_se_niega_sin_escribir()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var tce = await CrearTenantAsync(arnes, InboundDemoSeeder.NombreTenantTce, conMarcador: true);

        // Otra versión (la de Development): el catálogo entero, con los tipos que revelan salud (D7).
        await arnes.EnTenantAsync(tce, async (db, _) =>
        {
            db.TiposDocumento.AddRange(TipoDocumentoSeedData.CrearCopiasParaTenant());
            return await db.SaveChangesAsync();
        });
        var deSalud = await arnes.ComoBootstrapAsync(b => b.TiposDocumento.IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == tce && t.Sensibilidad == SensibilidadDocumental.CategoriaEspecialSalud));
        deSalud.Should().BePositive("control positivo: el Tenant del lote tiene tipos de documento de salud");

        await SeNiegaSinEscribirAsync(
            arnes, "*datos de otra versión*",
            antes => antes["Tenants del lote"].Should().Be(1, "control: el Tenant TCE existe y los tres TEC están pendientes"));
    }

    [Fact]
    public async Task Un_Tenant_del_lote_con_sus_cuentas_en_otro_dominio_se_niega_sin_escribir()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var tce = await CrearTenantAsync(arnes, InboundDemoSeeder.NombreTenantTce, conMarcador: true);
        var deOtroDominio = InboundDemoAdministrativa.CuentasDe("otro-dominio.es").AdministradorTce;

        using (var ambito = arnes.Servicios.CreateScope())
        using (AmbitoTenantExplicito.Establecer(tce))
        {
            var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = new ApplicationUser { UserName = deOtroDominio, Email = deOtroDominio, NombreCompleto = deOtroDominio, TenantId = tce };
            (await userManager.CreateAsync(cuenta)).Succeeded.Should().BeTrue("control: la cuenta de la primera ejecución se crea");
        }

        (await arnes.ComoBootstrapAsync(b => b.Users.IgnoreQueryFilters().CountAsync(u => u.Email == deOtroDominio && u.TenantId == tce)))
            .Should().Be(1, "control positivo: el Tenant TCE tiene su Administrador del lote en otro dominio");

        await SeNiegaSinEscribirAsync(
            arnes, "*en un dominio distinto*",
            antes => antes["Cuentas del lote"].Should().Be(0, "control: ninguna cuenta del lote en el dominio de esta ejecución"));
    }

    [Fact]
    public async Task Sin_la_linea_Inbound_la_autoverificacion_da_la_discrepancia_e_Informar_devuelve_1()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var directorio = Directory.CreateTempSubdirectory("inbound-demo-credenciales-").FullName;
        try
        {
            var resultado = await InboundDemoAdministrativaFixture.SembrarEnAsync(
                arnes, InboundDemoAdministrativaFixture.Opciones(directorio), NullLogger.Instance);
            resultado.Discrepancias.Should().BeEmpty("control positivo: recién sembrado, el lote cuadra");

            var tce = await arnes.TenantIdAsync(InboundDemoSeeder.NombreTenantTce);
            var borradas = await arnes.ComoBootstrapAsync(async b =>
            {
                using (AmbitoTenantExplicito.Establecer(tce))
                    return await b.ProductosContratados.IgnoreQueryFilters()
                        .Where(p => p.TenantId == tce && p.Linea == LineaProductoContratado.Inbound)
                        .ExecuteDeleteAsync();
            });
            borradas.Should().Be(1, "control: se borra la línea Inbound que la siembra contrató, y solo esa");

            IReadOnlyList<string> medido, discrepancias;
            using (var ambito = arnes.Servicios.CreateScope())
            {
                var sp = ambito.ServiceProvider;
                (_, medido, discrepancias) = await InboundDemoAdministrativa.MedirAsync(
                    sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
                    InboundDemoAdministrativa.CuentasDe(Dominio), CancellationToken.None);
            }

            discrepancias.Should().ContainSingle("solo falta la línea Inbound")
                .Which.Should().Contain(InboundDemoSeeder.NombreTenantTce).And.Contain("línea Inbound vigente");

            using var salida = new StringWriter();
            using var errores = new StringWriter();
            var codigo = InboundDemoAdministrativa.Informar(
                resultado with { Medido = medido, Discrepancias = discrepancias }, "Production", salida, errores);

            codigo.Should().Be(1, "un lote que no cuadra termina el modo con 1");
            errores.ToString().Should().Contain("Autoverificación FALLIDA").And.Contain("línea Inbound vigente");
            salida.ToString().Should().NotContain("el lote es el que la siembra declara");

            // Hallazgo 5 de la ronda 1: el Tenant TCE sin su línea cuenta como pendiente, así que repetir la orden la contrata.
            var reanudacion = await InboundDemoAdministrativaFixture.SembrarEnAsync(
                arnes, InboundDemoAdministrativaFixture.Opciones(directorio), NullLogger.Instance);
            reanudacion.Escribio.Should().BeTrue("faltaba la línea Inbound del Tenant TCE");
            reanudacion.Discrepancias.Should().BeEmpty("la repetición vuelve a contratar la línea y el lote cuadra");
            reanudacion.Cuentas.Should().OnlyContain(c => !c.ContrasenaEntregada, "las cuentas ya existían");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    /// <summary>
    /// Lanza la siembra en un directorio de credenciales vacío y comprueba que se niega con <paramref name="motivo"/> y que la
    /// base, el directorio, el almacén y el espía de correo quedan como estaban. <paramref name="controlAntes"/> afirma sobre
    /// el recuento previo que el instrumento ve el estado preparado.
    /// </summary>
    private static async Task SeNiegaSinEscribirAsync(
        ArnesPilotoOutbound arnes, string motivo, Action<Dictionary<string, int>> controlAntes,
        DateOnly? fecha = null, DateOnly? hoy = null)
    {
        var directorio = Directory.CreateTempSubdirectory("inbound-demo-credenciales-").FullName;
        try
        {
            var opciones = InboundDemoAdministrativaFixture.Opciones(directorio) with
            {
                FechaDemostracion = fecha ?? ArnesPilotoOutbound.FechaDemostracion(),
            };
            var antes = await RecuentoAsync(arnes, directorio);
            controlAntes(antes);

            var siembra = () => InboundDemoAdministrativaFixture.SembrarEnAsync(arnes, opciones, NullLogger.Instance, hoy);
            var negativa = (await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage(motivo)).Which;

            (await RecuentoAsync(arnes, directorio)).Should().BeEquivalentTo(antes,
                "la negativa va antes de crear el fichero de credenciales, una cuenta o un Tenant: no se escribe nada");
            arnes.Correo.Intentos.Should().BeEmpty();
            InboundDemoAdministrativa.MensajeDeInterrupcion(negativa).Should().Contain(negativa.Message,
                "lo que el modo imprime al salir con 1 lleva el motivo de la negativa");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    private static async Task<Guid> CrearTenantAsync(ArnesPilotoOutbound arnes, string nombre, bool conMarcador)
    {
        var tenant = new CaeManager.Domain.Tenants.Tenant(nombre, CaeManager.Domain.Tenants.PerfilVocabularioTenant.ClienteDirecto);
        if (conMarcador)
            tenant.MarcarDatosDemoCompletados();

        await arnes.ComoBootstrapAsync(async b =>
        {
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                b.Tenants.Add(tenant);
                return await b.SaveChangesAsync();
            }
        });
        return tenant.Id;
    }

    /// <summary>Lo que la siembra podría escribir, contado con la identidad de bootstrap y sin filtros (también lo eliminado).</summary>
    private static async Task<Dictionary<string, int>> RecuentoAsync(ArnesPilotoOutbound arnes, string directorio)
    {
        var recuento = await arnes.ComoBootstrapAsync(async b =>
        {
            var nombres = InboundDemoSeeder.NombresTenants.ToList();
            var ids = await b.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
            var correos = InboundDemoAdministrativa.CuentasDe(Dominio).Todas.ToList();

            return new Dictionary<string, int>
            {
                ["Tenants del lote"] = ids.Count,
                ["Tenants en total"] = await b.Tenants.CountAsync(),
                ["Cuentas del lote"] = await b.Users.IgnoreQueryFilters().CountAsync(u => correos.Contains(u.Email!)),
                ["Cuentas en total"] = await b.Users.IgnoreQueryFilters().CountAsync(),
                ["Empresas del lote"] = await b.Empresas.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Empresas en total"] = await b.Empresas.IgnoreQueryFilters().CountAsync(),
                ["Trabajadores en total"] = await b.Trabajadores.IgnoreQueryFilters().CountAsync(),
                ["Documentos en total"] = await b.Documentos.IgnoreQueryFilters().CountAsync(),
                ["Tipos de documento en total"] = await b.TiposDocumento.IgnoreQueryFilters().CountAsync(),
                ["Contratos de acceso en total"] = await b.ContratosAcceso.IgnoreQueryFilters().CountAsync(),
                ["Productos contratados en total"] = await b.ProductosContratados.IgnoreQueryFilters().CountAsync(),
            };
        });

        recuento["Ficheros de credenciales"] = Directory.EnumerateFiles(directorio).Count();
        recuento["Ficheros en el almacén"] = arnes.FicherosEnElAlmacen();
        return recuento;
    }
}
