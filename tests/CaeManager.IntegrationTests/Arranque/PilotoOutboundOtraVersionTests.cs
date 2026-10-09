using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// La guarda del lote de otra versión de la siembra del piloto Outbound. La idempotencia
/// de los datos de un Tenant propietario mira el identificador fiscal de su Empresa
/// propia, y ese identificador lo calcula la siembra: una versión que lo calcule de otra
/// manera encuentra el Tenant por su nombre, no encuentra el identificador y, sin la
/// guarda, intenta sembrar encima de lo que ya hay.
///
/// <para>
/// <b>Montaje, el mismo en las dos vías.</b> Una siembra cortada justo después de
/// aprovisionar T2: quedan el Tenant del Operador CAE externo, T4 y T3 con sus datos, T2
/// aprovisionado y sin datos, y T5, T6 y T1 sin existir. Después se cambia, con las
/// entidades y dentro del ámbito de T3, el identificador fiscal de su Empresa propia: T3
/// pasa a ser un Tenant con datos de otra versión, y sigue habiendo algo pendiente que
/// una ejecución querría escribir.
/// </para>
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundOtraVersionTests(ITestOutputHelper salida)
{
    private const string Dominio = "demo-ejemplo.es";

    private static readonly TenantPilotoOutbound ConDatosDeOtraVersion = CatalogoPilotoOutbound.T3;
    private static readonly EntornoDePrueba Produccion = new("Production");
    private static readonly IConfiguration ConfiguracionVacia = new ConfigurationBuilder().Build();

    /// <summary>T2 es el siguiente a T3 en el orden de siembra: el corte lo deja aprovisionado y sin datos.</summary>
    private const string NombreDelPendiente = CatalogoPilotoOutbound.NombreTenantT2;

    private static LoggerQueFalla CorteJustoDespuesDeAprovisionarElPendiente() => new($"Tenant de demo sembrado: {NombreDelPendiente}");

    private static PilotoOutboundAdministrativa.Opciones Opciones(string directorio) => new(
        Dominio, directorio, "Production", ArnesPilotoOutbound.FechaDemostracion(),
        ContactosPilotoOutbound.Crear(ArnesPilotoOutbound.CorreoDePrueba, dominio: null));

    private static async Task<PilotoOutboundAdministrativa.Resultado> EjecutarAsync(
        ArnesPilotoOutbound arnes, PilotoOutboundAdministrativa.Opciones opciones, ILogger logger)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;

        return await PilotoOutboundAdministrativa.EjecutarAsync(
            sp.GetRequiredService<CaeManagerDbContext>(),
            () => sp.GetRequiredService<FabricaContextoDeBootstrap>().Crear(),
            sp.GetRequiredService<UserManager<ApplicationUser>>(), sp.GetRequiredService<IUserStore<ApplicationUser>>(),
            sp.GetRequiredService<IFileStorageService>(), arnes.FabricaDeAmbitos, ConfiguracionVacia, Produccion, opciones,
            logger, CancellationToken.None);
    }

    /// <summary>
    /// Cómo termina la orden: con un resultado —lo que <c>Program.cs</c> pasa a <c>Informar</c>, que puede salir con
    /// 0— o con una excepción, que el modo <c>--sembrar-piloto-outbound</c> escribe por la salida de error y convierte
    /// siempre en código de salida 1. Ese <c>catch</c> no se ejecuta aquí: lo vigila
    /// <c>PilotoOutboundSoloDesdeElModoCliTests</c>. Lo que este test mide es por cuál de los dos caminos se sale.
    /// </summary>
    private static async Task<(PilotoOutboundAdministrativa.Resultado? Sembrado, Exception? Negativa)> DesenlaceDeLaOrdenAsync(
        ArnesPilotoOutbound arnes, PilotoOutboundAdministrativa.Opciones opciones)
    {
        try
        {
            return (await EjecutarAsync(arnes, opciones, new ArnesPilotoOutbound.RegistroDeAvisos()), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    /// <summary>
    /// Simula un lote de otra versión: la Empresa propia del Tenant deja de llevar el identificador fiscal que esta
    /// versión le da y pasa a llevar otro, válido y de una provincia que la siembra no usa. Nada más cambia.
    /// </summary>
    private static async Task CambiarElIdentificadorFiscalDeLaEmpresaPropiaAsync(ArnesPilotoOutbound arnes, Guid tenantId)
    {
        var deEstaVersion = IdentidadesPilotoOutbound.Cif(ConDatosDeOtraVersion, 0);
        var deOtraVersion = DatosPruebaSeeder.GenerarCifValido(9_912_345);
        deOtraVersion.Should().NotBe(deEstaVersion, "control: el identificador que se pone no es el que esta versión espera");

        await arnes.EnTenantAsync(tenantId, async (db, _) =>
        {
            var propia = await db.Empresas.SingleAsync(e => e.Cif == deEstaVersion);
            propia.EsPropia.Should().BeTrue("control: el ordinal 0 es la Empresa propia del Tenant");
            propia.Actualizar(propia.RazonSocial, deOtraVersion, propia.Cnae, propia.ConvenioAplicable, propia.EsActividadAnexoI);
            return await db.SaveChangesAsync();
        });

        (await arnes.EnTenantAsync(tenantId, (db, _) => db.Empresas.AnyAsync(e => e.Cif == deEstaVersion)))
            .Should().BeFalse("control: el cambio se guardó, y en el Tenant ya no hay ninguna Empresa con el identificador esperado");
    }

    /// <summary>Las filas de un solo Tenant, contadas con la identidad de bootstrap y sin filtros (también lo eliminado).</summary>
    private static Task<Dictionary<string, int>> RecuentoDeAsync(ArnesPilotoOutbound arnes, Guid tenantId) =>
        arnes.ComoBootstrapAsync(async b => new Dictionary<string, int>
        {
            ["Empresas"] = await b.Empresas.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId),
            ["Centros"] = await b.Centros.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId),
            ["Trabajadores"] = await b.Trabajadores.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId),
            ["Documentos"] = await b.Documentos.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId),
            ["Contactos de agenda"] = await b.ContactosAgenda.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId),
            ["Asignaciones de Operación"] = await b.AsignacionesOperacion.IgnoreQueryFilters().CountAsync(e => e.PropietarioTenantId == tenantId),
        });

    private static async Task<string?> PrimerTenantSinSembrarAsync(ArnesPilotoOutbound arnes)
    {
        using var ambito = arnes.Servicios.CreateScope();
        return await PilotoOutboundSeeder.PrimerTenantSinSembrarAsync(
            ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>(), CancellationToken.None);
    }

    private static async Task<IReadOnlyList<string>> TenantsConDatosDeOtraVersionAsync(ArnesPilotoOutbound arnes)
    {
        using var ambito = arnes.Servicios.CreateScope();
        return await PilotoOutboundSeeder.TenantsConDatosDeOtraVersionAsync(
            ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>(), CancellationToken.None);
    }

    private static string Texto(Dictionary<string, int> recuento) => string.Join(", ", recuento.Select(p => $"{p.Key} {p.Value}"));

    [Fact]
    public async Task En_el_arranque_un_Tenant_con_datos_de_otra_version_no_se_toca_se_avisa_con_su_nombre_y_los_demas_se_siembran()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var configuracion = ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion());

        var cortada = () => arnes.SembrarAsync(configuracion, logger: CorteJustoDespuesDeAprovisionarElPendiente());
        await cortada.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Fallo inyectado por la prueba*");

        var tenantId = await arnes.TenantIdAsync(ConDatosDeOtraVersion.Nombre);
        (await TenantsConDatosDeOtraVersionAsync(arnes)).Should().BeEmpty("control: recién sembrado, ningún Tenant es de otra versión");
        await CambiarElIdentificadorFiscalDeLaEmpresaPropiaAsync(arnes, tenantId);

        var antes = await RecuentoDeAsync(arnes, tenantId);
        salida.WriteLine($"MEDIDO «{ConDatosDeOtraVersion.Nombre}» antes del re-arranque: {Texto(antes)}");
        antes["Empresas"].Should().BeGreaterThan(1, "control positivo: el Tenant tiene su Empresa propia y sus Clientes empresariales");
        antes["Trabajadores"].Should().BeGreaterThan(0, "control positivo: el recuento ve a los Trabajadores del Tenant");
        antes["Documentos"].Should().BeGreaterThan(0, "control positivo: el recuento ve los Documentos del Tenant");
        (await PrimerTenantSinSembrarAsync(arnes)).Should().Be(
            NombreDelPendiente, "control: queda algo por sembrar, así que esta ejecución va a escribir en otros Tenants");

        var avisos = new ArnesPilotoOutbound.RegistroDeAvisos();
        PilotoOutboundSeeder.Resultado? resultado = null;
        var arranque = async () => { resultado = await arnes.SembrarAsync(configuracion, logger: avisos); };

        await arranque.Should().NotThrowAsync(
            "MEDIDO: el arranque no se cae; sin la guarda, la siembra intenta escribir otra vez los datos de ese Tenant encima de los que tiene");

        (await RecuentoDeAsync(arnes, tenantId)).Should().BeEquivalentTo(
            antes, "MEDIDO: en el Tenant con datos de otra versión no se escribe nada: ni una Empresa, ni un Trabajador, ni un Documento más");
        avisos.Avisos.Should().ContainSingle(a => a.Contains("otra versión de la siembra"), "MEDIDO: un aviso, y solo del Tenant afectado")
            .Which.Should().Contain($"«{ConDatosDeOtraVersion.Nombre}»")
            .And.Contain("Hay que retirar el lote").And.Contain(PilotoOutboundRetirada.Argumento)
            .And.Contain("no se escribe nada y el arranque continúa");
        resultado!.TenantsConDatosNuevos.Should().Equal(
            [NombreDelPendiente, CatalogoPilotoOutbound.NombreTenantT5, CatalogoPilotoOutbound.NombreTenantT6, CatalogoPilotoOutbound.NombreTenantT1],
            "MEDIDO: los demás siguen su curso —también el que estaba aprovisionado y sin datos—, y el de otra versión no está entre los escritos");
        (await PrimerTenantSinSembrarAsync(arnes)).Should().BeNull("MEDIDO: no queda nada que una ejecución vaya a escribir");
        (await TenantsConDatosDeOtraVersionAsync(arnes)).Should().Equal(
            [ConDatosDeOtraVersion.Nombre], "MEDIDO: sigue siendo de otra versión hasta que se retire el lote");
    }

    [Fact]
    public async Task Por_la_via_administrativa_un_Tenant_con_datos_de_otra_version_hace_salir_con_codigo_distinto_de_cero_sin_escribir_en_ningun_Tenant()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var directorio = Directory.CreateTempSubdirectory("piloto-outbound-credenciales-").FullName;
        try
        {
            var cortada = () => EjecutarAsync(arnes, Opciones(directorio), CorteJustoDespuesDeAprovisionarElPendiente());
            await cortada.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Fallo inyectado por la prueba*");

            await CambiarElIdentificadorFiscalDeLaEmpresaPropiaAsync(arnes, await arnes.TenantIdAsync(ConDatosDeOtraVersion.Nombre));

            var antes = await arnes.RecuentoAsync();
            salida.WriteLine("MEDIDO antes de repetir la orden: " + Texto(antes));
            antes["Tenants del piloto"].Should().Be(4, "control: el del Operador CAE externo, T4 y T3 sembrados y T2 aprovisionado; faltan T5, T6 y T1");
            antes["Empresas"].Should().BeGreaterThan(0, "control positivo: el recuento ve las Empresas del lote");
            antes["Documentos"].Should().BeGreaterThan(0, "control positivo: el recuento ve los Documentos del lote");
            (await PrimerTenantSinSembrarAsync(arnes)).Should().Be(
                NombreDelPendiente, "control: hay un Tenant pendiente que la orden querría escribir");
            var ficherosDeCredenciales = Directory.GetFiles(directorio);
            ficherosDeCredenciales.Should().HaveCount(1, "control: el de la ejecución cortada");

            var (sembrado, negativa) = await DesenlaceDeLaOrdenAsync(arnes, Opciones(directorio));

            (await arnes.RecuentoAsync()).Should().BeEquivalentTo(
                antes, "MEDIDO: no se escribe en NINGÚN Tenant: ni Tenants, ni cuentas, ni filas, ni ficheros más que antes de la orden");
            (await PrimerTenantSinSembrarAsync(arnes)).Should().Be(NombreDelPendiente, "MEDIDO: el Tenant pendiente sigue pendiente");
            Directory.GetFiles(directorio).Should().BeEquivalentTo(
                ficherosDeCredenciales, "MEDIDO: la negativa llega antes de abrir el fichero de credenciales");

            sembrado.Should().BeNull(
                "MEDIDO: la orden no termina con un resultado que informar, que es el único camino por el que el modo puede salir con 0");
            var motivo = PilotoOutboundSeeder.MensajeDeDatosDeOtraVersion([ConDatosDeOtraVersion.Nombre]) +
                         " La siembra del piloto se niega y no escribe nada.";
            negativa.Should().BeOfType<InvalidOperationException>(
                    "MEDIDO: la orden se niega con una excepción, que el modo convierte en código de salida distinto de cero")
                .Which.Message.Should().Be(motivo, "MEDIDO: el motivo nombra el Tenant y dice que hay que retirar el lote");
            motivo.Should().Contain($"«{ConDatosDeOtraVersion.Nombre}»").And.Contain(PilotoOutboundRetirada.Argumento, "control: el motivo dice lo que se afirma de él");
            PilotoOutboundAdministrativa.MensajeDeInterrupcion(negativa!).Should().StartWith(
                "Siembra del piloto Outbound interrumpida: " + motivo, "MEDIDO: lo que el modo escribe por la salida de error empieza por ese motivo");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }
}
