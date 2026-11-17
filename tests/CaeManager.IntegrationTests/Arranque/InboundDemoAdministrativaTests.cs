using System.Text.Json;
using CaeManager.Application.Common;
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

    internal async Task<InboundDemoAdministrativa.Resultado> SembrarAsync(InboundDemoAdministrativa.Opciones opciones, ILogger logger)
    {
        using var ambito = Arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;

        return await InboundDemoAdministrativa.EjecutarAsync(
            sp.GetRequiredService<CaeManagerDbContext>(),
            () => sp.GetRequiredService<FabricaContextoDeBootstrap>().Crear(),
            sp.GetRequiredService<UserManager<ApplicationUser>>(), sp.GetRequiredService<IUserStore<ApplicationUser>>(),
            sp.GetRequiredService<IFileStorageService>(), new ConfigurationBuilder().Build(), new EntornoDePrueba("Production"),
            opciones, DiaDeNegocio.Hoy(), logger, CancellationToken.None);
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
/// contraseñas. Las negativas que no tocan la base están en <c>InboundDemoAdministrativaPrecondicionesTests</c>.
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
}
