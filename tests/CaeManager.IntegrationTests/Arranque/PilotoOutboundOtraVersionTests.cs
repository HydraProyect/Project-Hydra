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
///
/// <para>
/// <b>La autoverificación del arranque.</b> Con ese mismo montaje la ejecución escribe los
/// Tenants que faltaban, y la autoverificación, que exige la matriz a lo recién escrito, del
/// Tenant con datos de otra versión solo avisa.
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

    /// <summary>Lo que la autoverificación dice de un documento cuyo vencimiento ya no sale de su emisión.</summary>
    private const string DiscrepanciaDeFecha =
        "Documentos · vencimiento que no es la emisión más los meses de su Tipo: medido 1, esperado 0.";

    private static string Prefijo(TenantPilotoOutbound tenant) => $"{tenant.Clave} «{tenant.Nombre}»";

    /// <summary>
    /// Aparta de la matriz un documento del Tenant: a uno de un Tipo que vence solo se le mueve la emisión y se le
    /// deja el vencimiento, con la corrección del dominio. Deja de tener las fechas que el producto le habría dado,
    /// que es una de las cosas que la autoverificación mide en los seis Tenants.
    /// </summary>
    private static async Task DescuadrarUnaFechaAsync(ArnesPilotoOutbound arnes, Guid tenantId) =>
        (await arnes.EnTenantAsync(tenantId, async (db, _) =>
        {
            var queVencenSolos = (await db.TiposDocumento.ToListAsync()).Where(t => t.FijaVigenciaDesdeLaEmision).Select(t => t.Id).ToList();
            var documento = await db.Documentos
                .Where(d => queVencenSolos.Contains(d.TipoDocumentoId) && d.SustituidoPorDocumentoId == null && d.FechaVencimiento != null)
                .OrderBy(d => d.Id).FirstOrDefaultAsync();
            documento.Should().NotBeNull("control: el Tenant tiene algún documento en uso de un Tipo que vence solo");

            documento!.CorregirVigencia(documento.FechaEmision.AddDays(-40), documento.Vigencia);
            return await db.SaveChangesAsync();
        })).Should().BeGreaterThan(0, "control: la alteración se escribió");

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

    /// <summary>
    /// El caso mixto, por la misma función de decisión que invoca <c>Program.cs</c> y en el orden del arranque
    /// (siembra, reparto de asignaciones, autoverificación): la ejecución escribe los Tenants que faltaban y deja
    /// como está el que tiene datos de otra versión, que además no cuadra con la matriz.
    /// </summary>
    [Fact]
    public async Task En_el_arranque_la_autoverificacion_solo_avisa_de_un_Tenant_con_datos_de_otra_version_y_sigue_exigiendo_la_matriz_a_los_recien_escritos()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var configuracion = ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion());
        var opciones = OpcionesPilotoOutbound.Leer(configuracion);

        var cortada = () => arnes.SembrarAsync(configuracion, logger: CorteJustoDespuesDeAprovisionarElPendiente());
        await cortada.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Fallo inyectado por la prueba*");

        // El Tenant de otra versión, además, lleva un documento que la matriz no admite: cambiarle el identificador
        // fiscal no altera nada de lo que la autoverificación mide.
        var idDeOtraVersion = await arnes.TenantIdAsync(ConDatosDeOtraVersion.Nombre);
        await CambiarElIdentificadorFiscalDeLaEmpresaPropiaAsync(arnes, idDeOtraVersion);
        await DescuadrarUnaFechaAsync(arnes, idDeOtraVersion);

        var resultado = await arnes.SembrarAsync(configuracion);
        var escribio = resultado!.Escribio;
        escribio.Should().BeTrue("control: es el caso mixto, esta ejecución ha escrito");
        resultado.TenantsConDatosNuevos.Should().Contain(NombreDelPendiente, "control: el pendiente se acaba de escribir")
            .And.NotContain(ConDatosDeOtraVersion.Nombre, "control: en el de otra versión no se ha escrito");
        await arnes.BackfillAsync();

        var medidas = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion));
        foreach (var linea in medidas) salida.WriteLine("MEDIDO " + linea);
        medidas.Should().Contain(
            $"{Prefijo(ConDatosDeOtraVersion)} · {DiscrepanciaDeFecha}", "control positivo: el Tenant de otra versión no supera la autoverificación");
        medidas.Should().OnlyContain(
            l => l.StartsWith(Prefijo(ConDatosDeOtraVersion)), "control: lo recién escrito cuadra, así que lo único que podría tumbar el arranque es ese Tenant");

        Task DecidirAsync(ILogger registro) => PilotoOutboundAutoverificacion.MedirYExigirOAvisarAsync(
            arnes.FabricaDeAmbitos, opciones, escribio, registro);

        static List<string> NoExigidas(ArnesPilotoOutbound.RegistroDeAvisos registro) =>
            [.. registro.Avisos.Where(a => a.StartsWith("Piloto Outbound, discrepancia que no se exige porque el Tenant «"))];

        var avisos = new ArnesPilotoOutbound.RegistroDeAvisos();
        var arranque = () => DecidirAsync(avisos);
        await arranque.Should().NotThrowAsync(
            "MEDIDO: el arranque no se cae por las discrepancias de un Tenant en el que esta ejecución no ha escrito");

        NoExigidas(avisos).Should().HaveCount(medidas.Count, "MEDIDO: cada discrepancia de ese Tenant queda como aviso")
            .And.OnlyContain(
                a => a.Contains($"el Tenant «{ConDatosDeOtraVersion.Nombre}» tiene datos de otra versión de la siembra"),
                "MEDIDO: el aviso nombra el Tenant y dice por qué no se exige")
            .And.Contain(a => a.EndsWith($"{Prefijo(ConDatosDeOtraVersion)} · {DiscrepanciaDeFecha}"), "MEDIDO: y trae la discrepancia entera");

        // La misma ejecución, con un Tenant recién escrito que tampoco cuadra: eso sí se exige.
        await DescuadrarUnaFechaAsync(arnes, await arnes.TenantIdAsync(NombreDelPendiente));

        var avisosConDescuadre = new ArnesPilotoOutbound.RegistroDeAvisos();
        var arranqueConDescuadre = () => DecidirAsync(avisosConDescuadre);
        var negativa = (await arranqueConDescuadre.Should().ThrowAsync<InvalidOperationException>(
            "MEDIDO: una discrepancia de un Tenant recién escrito sigue tumbando el arranque, también en el caso mixto")).Which;
        salida.WriteLine("MEDIDO " + negativa.Message);

        negativa.Message.Should().Contain($"{Prefijo(CatalogoPilotoOutbound.T2)} · {DiscrepanciaDeFecha}", "MEDIDO: lo que tumba es el Tenant recién escrito")
            .And.NotContain($"«{ConDatosDeOtraVersion.Nombre}»", "MEDIDO: el Tenant de otra versión no está entre lo exigido");
        CatalogoPilotoOutbound.T2.Nombre.Should().Be(NombreDelPendiente, "control: el Tenant descuadrado es el que se acaba de escribir");
        NoExigidas(avisosConDescuadre).Should().HaveCount(medidas.Count, "MEDIDO: del Tenant de otra versión se sigue avisando, y solo de él");
    }

    /// <summary>
    /// Sin base de datos: el reparto que decide qué se exige. Lo que no es de un solo Tenant —el control positivo
    /// de «cero filas», que mira los seis a la vez— no se puede exigir cuando uno de ellos lleva datos de otra
    /// versión; sin ninguno de otra versión se exige todo, como siempre.
    /// </summary>
    [Fact]
    public void Con_un_Tenant_de_otra_version_lo_que_es_del_lote_entero_solo_se_avisa_y_sin_ninguno_se_exige_todo()
    {
        var atribuidas = PilotoOutboundAutoverificacion.DiscrepanciasAtribuidas(new PilotoOutboundAutoverificacion.Informe([]));
        var delLoteEntero = atribuidas.Should().ContainSingle(d => d.NombreDeTenant == null, "MEDIDO: la matriz tiene una sola comprobación de lote entero").Which;
        delLoteEntero.Texto.Should().StartWith("Mi trabajo · control positivo", "MEDIDO: es el control positivo de «cero filas»");
        atribuidas.Where(d => d.NombreDeTenant != null).Select(d => d.NombreDeTenant).Should().BeEquivalentTo(
            CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre), "control: sin nada medido, cada Tenant propietario tiene su discrepancia, a su nombre");

        var deOtraVersion = atribuidas.Single(d => d.NombreDeTenant == ConDatosDeOtraVersion.Nombre);
        var recienEscrito = atribuidas.Single(d => d.NombreDeTenant == NombreDelPendiente);
        IReadOnlyList<PilotoOutboundAutoverificacion.Discrepancia> tres = [deOtraVersion, recienEscrito, delLoteEntero];

        var (exigidas, soloAvisadas) = PilotoOutboundAutoverificacion.Repartir(tres, [ConDatosDeOtraVersion.Nombre]);
        exigidas.Should().Equal([recienEscrito], "MEDIDO: en el caso mixto solo se exige lo de un Tenant que no es de otra versión");
        soloAvisadas.Should().Equal([deOtraVersion, delLoteEntero], "MEDIDO: lo del Tenant de otra versión y lo del lote entero solo se avisan");

        var (todas, ninguna) = PilotoOutboundAutoverificacion.Repartir(tres, []);
        todas.Should().Equal(tres, "MEDIDO: sin ningún Tenant de otra versión se exige todo, también lo del lote entero");
        ninguna.Should().BeEmpty();
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
