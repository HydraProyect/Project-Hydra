using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// La vía administrativa del piloto Outbound (<c>--sembrar-piloto-outbound</c> y
/// <c>--retirar-piloto-outbound</c>) sobre el arnés del piloto: composición real, tráfico
/// como <c>cae_app_runtime</c>, almacén en disco y entorno <c>Production</c>. Se llama a
/// <c>EjecutarAsync</c>, después de las comprobaciones que dependen de Linux y de
/// <c>/proc</c> (esas se prueban sin base en <c>PilotoOutboundAdministrativaPrecondicionesTests</c>).
/// Lo que aquí se mide es lo que la separa de la siembra local: contraseñas únicas y
/// ausentes de todo registro, cuentas de un dominio propio, la autoverificación exigida,
/// ningún correo enviado y una retirada que confirma el entorno y anuncia cada Tenant al borrarlo.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundAdministrativaTests(ITestOutputHelper salida)
{
    private const string Dominio = "demo-ejemplo.es";

    private static readonly EntornoDePrueba Produccion = new("Production");

    private static PilotoOutboundAdministrativa.Opciones Opciones(string directorio, DateOnly? fecha = null) => new(
        Dominio, directorio, "Production", fecha ?? ArnesPilotoOutbound.FechaDemostracion(),
        ContactosPilotoOutbound.Crear(ArnesPilotoOutbound.CorreoDePrueba, dominio: null));

    private static IConfiguration ConfiguracionConfirmada { get; } = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [PilotoOutboundAdministrativa.ClaveConfirmarEntorno] = "Production" })
        .Build();

    private static IConfiguration ConfiguracionVacia { get; } = new ConfigurationBuilder().Build();

    private static string Directorio() => Directory.CreateTempSubdirectory("piloto-outbound-credenciales-").FullName;

    private static async Task<PilotoOutboundAdministrativa.Resultado> SembrarAsync(
        ArnesPilotoOutbound arnes, PilotoOutboundAdministrativa.Opciones opciones, ILogger? logger = null)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;

        return await PilotoOutboundAdministrativa.EjecutarAsync(
            sp.GetRequiredService<CaeManagerDbContext>(),
            () => sp.GetRequiredService<FabricaContextoDeBootstrap>().Crear(),
            sp.GetRequiredService<UserManager<ApplicationUser>>(), sp.GetRequiredService<IUserStore<ApplicationUser>>(),
            sp.GetRequiredService<IFileStorageService>(), arnes.FabricaDeAmbitos, ConfiguracionVacia, Produccion, opciones,
            logger ?? NullLogger.Instance, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<RetiradaTenantDemoService.ResultadoRetirada>> RetirarAsync(
        ArnesPilotoOutbound arnes, IConfiguration configuracion, List<string> anunciados,
        Func<IFileStorageService, IFileStorageService>? envolverAlmacen = null)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        var almacen = sp.GetRequiredService<IFileStorageService>();

        return await PilotoOutboundRetirada.RetirarLoteConfirmadoAsync(
            configuracion, Produccion, sp.GetRequiredService<CaeManagerDbContext>(),
            () => sp.GetRequiredService<FabricaContextoDeBootstrap>().Crear(),
            envolverAlmacen?.Invoke(almacen) ?? almacen, NullLogger.Instance,
            retirado => anunciados.Add(retirado.NombreTenant));
    }

    /// <summary>
    /// LIMITACIÓN DEL ARNÉS, no de la retirada (la misma de <see cref="ArnesPilotoOutbound.RetirarAsync"/>): con claves
    /// de protección de datos efímeras, releer columnas cifradas con el contexto de bootstrap lanza. El canal de
    /// plataforma de T2 lleva una credencial cifrada; se borra antes para medir la retirada en todo lo demás.
    /// </summary>
    private static Task BorrarCanalesCifradosAsync(ArnesPilotoOutbound arnes) =>
        arnes.ComoBootstrapAsync(async b =>
        {
            var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
            var ids = await b.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
            return await b.CanalesGestionDocumental.IgnoreQueryFilters().Where(c => ids.Contains(c.TenantId)).ExecuteDeleteAsync();
        });

    private static Task<List<(string Nombre, Guid Id, bool Marcado)>> TenantsDelPilotoAsync(ArnesPilotoOutbound arnes) =>
        arnes.ComoBootstrapAsync(async b =>
        {
            var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
            return (await b.Tenants.Where(t => nombres.Contains(t.Nombre))
                    .Select(t => new { t.Nombre, t.Id, t.DatosDemoCompletadosEnUtc }).ToListAsync())
                .Select(t => (t.Nombre, t.Id, t.DatosDemoCompletadosEnUtc != null)).ToList();
        });

    private static Task<int> CuentasDelDominioAsync(ArnesPilotoOutbound arnes) =>
        arnes.ComoBootstrapAsync(b => b.Users.IgnoreQueryFilters().CountAsync(u => u.Email!.EndsWith("@" + Dominio)));

    private static readonly byte[] ContenidoAjeno = [37, 80, 68, 70, 45, 97, 106, 101, 110, 111];

    /// <summary>Crea un Tenant que no es del piloto y guarda un fichero suyo por el almacén real, dentro de su ámbito.</summary>
    private static Task<(Guid TenantId, string Clave)> GuardarFicheroEnUnTenantAjenoAsync(ArnesPilotoOutbound arnes)
    {
        var ajeno = new Tenant("Empresa Ajena con Fichero S.L.", PerfilVocabularioTenant.ClienteDirecto);
        return arnes.EnTenantAsync(ajeno.Id, async (db, sp) =>
        {
            db.Tenants.Add(ajeno);
            await db.SaveChangesAsync();

            using var contenido = new MemoryStream(ContenidoAjeno);
            return (ajeno.Id, await sp.GetRequiredService<IFileStorageService>().GuardarAsync(contenido, "ajeno.pdf"));
        });
    }

    private static string RutaEnElAlmacen(ArnesPilotoOutbound arnes, string clave) =>
        Path.Combine(arnes.DirectorioAlmacen, clave.Replace('/', Path.DirectorySeparatorChar));

    private static List<(string Email, string Rol, string? Contrasena, string? Nota)> LeerCredenciales(string fichero) =>
        JsonDocument.Parse(File.ReadAllText(fichero)).RootElement.GetProperty("cuentas").EnumerateArray()
            .Select(c => (
                c.GetProperty("email").GetString()!, c.GetProperty("rol").GetString()!,
                c.GetProperty("contrasena").GetString(), c.GetProperty("nota").GetString()))
            .ToList();

    [Fact]
    public async Task Siembra_el_lote_con_una_contrasena_por_cuenta_exige_la_matriz_no_envia_correo_y_la_retirada_confirma_el_entorno_y_anuncia_cada_Tenant()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var directorio = Directorio();
        try
        {
            // Un Tenant que NO es del piloto, con un fichero suyo en el almacén: tiene que seguir ahí al final.
            var ajeno = await GuardarFicheroEnUnTenantAjenoAsync(arnes);
            File.Exists(RutaEnElAlmacen(arnes, ajeno.Clave)).Should().BeTrue("control: el fichero ajeno existe antes de sembrar");

            var sinPiloto = await arnes.RecuentoAsync();
            var log = new LoggerDeCaptura();

            // ── A. Siembra ──────────────────────────────────────────────────
            var resultado = await SembrarAsync(arnes, Opciones(directorio), log);

            resultado.Siembra.Escribio.Should().BeTrue();
            resultado.Discrepancias.Should().BeEmpty("MEDIDO: tras la siembra administrativa lo medido es lo que la matriz declara");

            using var texto = new StringWriter();
            using var errores = new StringWriter();
            PilotoOutboundAdministrativa.Informar(resultado, "Production", texto, errores).Should().Be(0);
            salida.WriteLine(texto.ToString());
            errores.ToString().Should().BeEmpty();

            var tenants = await TenantsDelPilotoAsync(arnes);
            tenants.Should().HaveCount(7, "MEDIDO: seis Tenants propietarios y el del Operador CAE externo");
            tenants.Should().OnlyContain(t => t.Marcado, "MEDIDO: todos llevan el marcador de demo");
            resultado.Tenants.Select(t => t.Nombre).Should().Equal(CatalogoPilotoOutbound.NombresTenants);

            var credenciales = LeerCredenciales(resultado.FicheroCredenciales);
            credenciales.Should().HaveCount(6, "MEDIDO: Administrador, Coordinadora CAE y dos Gestores CAE del Operador CAE externo, y las dos cuentas de T1");
            credenciales.Select(c => c.Contrasena).Should().OnlyHaveUniqueItems("MEDIDO: una contraseña distinta por cuenta")
                .And.NotContainNulls().And.NotContain(CredencialesDemo.ContrasenaPorDefecto, "nunca la contraseña compartida");
            credenciales.Should().OnlyContain(c => c.Email.EndsWith("@" + Dominio) && !c.Email.EndsWith("@caemanager.local"));
            credenciales.Select(c => c.Email).Should().BeEquivalentTo(PilotoOutboundAdministrativa.CuentasDe(Dominio).Todas);
            resultado.Cuentas.Should().OnlyContain(c => c.ContrasenaEntregada);

            (await arnes.RecuentoAsync())["Cuentas del piloto"].Should().Be(0, "MEDIDO: ninguna cuenta con la dirección local @caemanager.local");
            (await CuentasDelDominioAsync(arnes)).Should().Be(6);

            if (OperatingSystem.IsLinux())
                File.GetUnixFileMode(resultado.FicheroCredenciales).Should().Be(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite, "MEDIDO en Linux: el fichero nace con permisos 0600");

            using (var ambito = arnes.Servicios.CreateScope())
            {
                var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                // AspNetUsers tiene RLS: el test busca por correo sin conocer el Tenant, por el mismo camino que el login.
                using var identificacion = AmbitoIdentificacionSinTenant.Abrir();
                foreach (var (email, rol, contrasena, _) in credenciales)
                {
                    var usuario = await userManager.FindByEmailAsync(email);
                    usuario.Should().NotBeNull();
                    (await userManager.CheckPasswordAsync(usuario!, contrasena!)).Should().BeTrue($"MEDIDO: {email} entra con SU contraseña del fichero");
                    (await userManager.CheckPasswordAsync(usuario!, CredencialesDemo.ContrasenaPorDefecto)).Should().BeFalse();
                    usuario!.LockoutEnd.Should().BeNull();
                    usuario.DebeCambiarContrasena.Should().BeFalse();

                    if (rol == Roles.Administrador)
                        usuario.TwoFactorEnabled.Should().BeFalse(
                            "MEDIDO: fuera de Development la siembra no asigna segundo factor; el Administrador lo configura en su primer acceso");
                }
            }

            var registrado = string.Join("\n", log.Mensajes);
            registrado.Should().NotBeEmpty("control positivo: el instrumento sí recoge lo que se registra");
            foreach (var (_, _, contrasena, _) in credenciales)
            {
                registrado.Should().NotContain(contrasena!, "MEDIDO: ninguna contraseña llega a un registro");
                texto.ToString().Should().NotContain(contrasena!, "MEDIDO: ninguna contraseña llega a la salida del modo");
            }

            arnes.Correo.Intentos.Should().BeEmpty("MEDIDO: ni la siembra, ni los pasos del arranque, ni la medición llaman a IEmailService");

            var completa = await arnes.RecuentoAsync();
            salida.WriteLine("MEDIDO tras la siembra administrativa: " + string.Join(", ", completa.Select(p => $"{p.Key} {p.Value}")));

            // ── A bis. Repetir la orden no duplica ni reentrega contraseñas ──
            var repetida = await SembrarAsync(arnes, Opciones(directorio));
            repetida.Siembra.Escribio.Should().BeFalse("MEDIDO: con el lote entero, la segunda ejecución no escribe datos");
            repetida.Cuentas.Should().OnlyContain(c => !c.ContrasenaEntregada);
            repetida.Discrepancias.Should().BeEmpty();
            repetida.FicheroCredenciales.Should().NotBe(resultado.FicheroCredenciales);
            LeerCredenciales(repetida.FicheroCredenciales).Should().OnlyContain(c => c.Contrasena == null && c.Nota != null);
            (await arnes.RecuentoAsync()).Should().BeEquivalentTo(completa, "MEDIDO: ni Tenants, ni cuentas, ni filas, ni ficheros nuevos");

            // ── A ter. Lo que un ensayo deja en T1 y la retirada tiene que llevarse ──
            // Un documento descartado (borrado lógico: su PDF sigue en disco), un logo, la plantilla en blanco de
            // un requisito de Centro, y una fila del piloto que nombra la clave del fichero AJENO: la retirada
            // pedirá borrarla dentro del ámbito de T1 y el almacén no debe resolverla.
            var idT1 = tenants.Single(t => t.Nombre == CatalogoPilotoOutbound.NombreTenantT1).Id;
            var (claveDescartada, claveLogo, clavePlantilla) = await arnes.EnTenantAsync(idT1, async (db, sp) =>
            {
                var almacen = sp.GetRequiredService<IFileStorageService>();

                var documento = await db.Documentos.Where(d => d.ArchivoUrl != null).OrderBy(d => d.Id).FirstAsync();
                documento.MarcarComoEliminado(Guid.NewGuid());

                using var png = new MemoryStream([1, 2, 3]);
                var logo = await almacen.GuardarAsync(png, "logo.png");
                (await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == idT1))
                    .EstablecerLogo(logo, new string('a', Tenant.LongitudLogoVersion), DateTime.UtcNow);

                var requisitos = await db.TiposDocumentoCentros.OrderBy(t => t.Id).Take(2).ToListAsync();
                requisitos.Should().HaveCount(2, "control: T1 tiene requisitos de Centro donde colgar una plantilla en blanco");
                using var pdf = new MemoryStream([4, 5, 6]);
                var plantilla = await almacen.GuardarAsync(pdf, "plantilla.pdf");
                requisitos[0].Actualizar(
                    requisitos[0].Incluido, requisitos[0].PeriodicidadEspecialMeses, requisitos[0].BloqueaAcceso,
                    plantilla, "plantilla.pdf", requisitos[0].ToleranciaDias);
                requisitos[1].Actualizar(
                    requisitos[1].Incluido, requisitos[1].PeriodicidadEspecialMeses, requisitos[1].BloqueaAcceso,
                    ajeno.Clave, "ajeno.pdf", requisitos[1].ToleranciaDias);

                await db.SaveChangesAsync();
                return (documento.ArchivoUrl!, logo, plantilla);
            });

            (await arnes.EnTenantAsync(idT1, (db, _) => db.Documentos.CountAsync(d => d.ArchivoUrl == claveDescartada)))
                .Should().Be(0, "control: el documento descartado ya no lo ve una consulta con los filtros globales");
            foreach (var clave in new[] { claveDescartada, claveLogo, clavePlantilla })
                File.Exists(RutaEnElAlmacen(arnes, clave)).Should().BeTrue($"control: el fichero {clave} existe antes de retirar");

            // ── B. Retirada ─────────────────────────────────────────────────
            await BorrarCanalesCifradosAsync(arnes);
            var trasBorrarCanales = await arnes.RecuentoAsync();
            var anunciados = new List<string>();

            // B.1 Sin la confirmación de entorno, o con otra, no se toca nada.
            foreach (var configuracion in new[]
                     {
                         ConfiguracionVacia,
                         new ConfigurationBuilder().AddInMemoryCollection(
                             new Dictionary<string, string?> { [PilotoOutboundAdministrativa.ClaveConfirmarEntorno] = "Staging" }).Build(),
                     })
            {
                var sinConfirmar = () => RetirarAsync(arnes, configuracion, anunciados);
                await sinConfirmar.Should().ThrowAsync<InvalidOperationException>().WithMessage("PilotoOutbound:ConfirmarEntorno debe ser exactamente*");
            }

            anunciados.Should().BeEmpty();
            (await arnes.RecuentoAsync()).Should().BeEquivalentTo(trasBorrarCanales, "MEDIDO: el rechazo es anterior a toda lectura y a todo borrado");

            // B.2 Un fallo a mitad deja anunciado lo que ya se borró, y solo eso.
            var cortada = () => RetirarAsync(
                arnes, ConfiguracionConfirmada, anunciados, real => new AlmacenQueFallaCuando(real, () => anunciados.Count >= 1));
            await cortada.Should().ThrowAsync<IOException>().WithMessage("*fallo inyectado*");

            anunciados.Should().Equal([CatalogoPilotoOutbound.NombreTenantT1], "MEDIDO: T1 se anunció en cuanto se borró, antes del fallo en el siguiente");
            (await TenantsDelPilotoAsync(arnes)).Select(t => t.Nombre).Should().BeEquivalentTo(
                CatalogoPilotoOutbound.NombresTenants.Except(anunciados),
                "MEDIDO: lo anunciado es exactamente lo que ya no existe; el resto sigue ahí");

            // B.3 Repetir la orden retira lo que faltaba y no deja nada del piloto.
            var resto = await RetirarAsync(arnes, ConfiguracionConfirmada, anunciados);
            resto.Should().HaveCount(6);
            anunciados.Should().Equal(CatalogoPilotoOutbound.NombresTenants, "MEDIDO: Tenants propietarios primero y el del Operador CAE externo al final");
            (await arnes.RecuentoAsync()).Should().BeEquivalentTo(sinPiloto, "MEDIDO: ni Tenants, ni cuentas, ni filas, ni ficheros del piloto; lo demás, igual que antes");
            (await CuentasDelDominioAsync(arnes)).Should().Be(0);

            // B.3 bis. Los ficheros del ensayo se han ido, el descartado incluido; el del Tenant ajeno sigue y se lee.
            File.Exists(RutaEnElAlmacen(arnes, claveDescartada)).Should().BeFalse(
                "MEDIDO: el PDF de un documento descartado durante el ensayo no sobrevive a la retirada");
            File.Exists(RutaEnElAlmacen(arnes, claveLogo)).Should().BeFalse("MEDIDO: el logo del Tenant retirado tampoco");
            File.Exists(RutaEnElAlmacen(arnes, clavePlantilla)).Should().BeFalse("MEDIDO: ni la plantilla en blanco de un requisito de Centro");

            File.Exists(RutaEnElAlmacen(arnes, ajeno.Clave)).Should().BeTrue(
                "MEDIDO: la retirada no borra un fichero de otro Tenant, ni cuando una fila del piloto nombra su clave");
            (await arnes.EnTenantAsync(ajeno.TenantId, async (_, sp) =>
                {
                    await using var flujo = await sp.GetRequiredService<IFileStorageService>().AbrirAsync(ajeno.Clave);
                    using var memoria = new MemoryStream();
                    await flujo.CopyToAsync(memoria);
                    return memoria.ToArray();
                }))
                .Should().Equal(ContenidoAjeno, "MEDIDO: y su Tenant lo sigue leyendo entero");

            // B.4 Lo que la retirada deja, medido: los directorios vacíos de cada Tenant en el almacén y la
            // auditoría de su propio borrado, sellada con Tenants que ya no existen.
            var idsRetirados = tenants.Select(t => t.Id).ToList();
            var directoriosQueQuedan = Directory.Exists(arnes.DirectorioAlmacen)
                ? Directory.GetDirectories(arnes.DirectorioAlmacen).Select(Path.GetFileName).ToList()
                : [];
            var auditoriaHuerfana = await arnes.ComoBootstrapAsync(b =>
                b.RegistrosAuditoria.IgnoreQueryFilters().CountAsync(r => idsRetirados.Contains(r.TenantId)));
            salida.WriteLine(
                $"MEDIDO tras la retirada: {directoriosQueQuedan.Count(d => idsRetirados.Any(id => id.ToString("N") == d))} directorios de Tenants " +
                $"retirados siguen en el almacén (vacíos) y {auditoriaHuerfana} filas de auditoría quedan selladas con Tenants retirados.");
            auditoriaHuerfana.Should().BeGreaterThan(0,
                "HUECO CONOCIDO Y DECLARADO: la retirada audita su propio borrado y esas filas no tienen ya Tenant; si esto pasa a 0, el hueco se ha cerrado y hay que retirar esta aserción");

            (await RetirarAsync(arnes, ConfiguracionConfirmada, [])).Should().BeEmpty("MEDIDO: sin nada que retirar, la orden es repetible");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    [Fact]
    public async Task Un_corte_justo_despues_de_aprovisionar_un_Tenant_no_lo_deja_sin_marcador_y_ni_la_siembra_ni_la_retirada_se_niegan_despues()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var directorio = Directorio();
        try
        {
            // El registro de AprovisionarTenantAsync es lo primero que ocurre tras el guardado que crea el
            // Tenant: fallar ahí es cortar la ejecución en el punto donde antes aún no tenía marcador.
            var corte = new LoggerQueFalla($"Tenant de demo sembrado: {CatalogoPilotoOutbound.NombreTenantOperador}");

            var cortada = () => SembrarAsync(arnes, Opciones(directorio), corte);
            await cortada.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Fallo inyectado por la prueba*");

            var tenants = await TenantsDelPilotoAsync(arnes);
            tenants.Select(t => t.Nombre).Should().Equal(
                [CatalogoPilotoOutbound.NombreTenantOperador], "control: el corte fue después de crear el Tenant, no antes");
            tenants.Should().OnlyContain(t => t.Marcado, "MEDIDO: el marcador viaja en el mismo guardado que crea el Tenant");

            using (var ambito = arnes.Servicios.CreateScope())
            {
                var guarda = () => PilotoOutboundSeeder.RechazarNombresOcupadosPorUnTenantSinMarcadorAsync(
                    ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>(), CancellationToken.None);
                await guarda.Should().NotThrowAsync("MEDIDO: la siembra no se niega tras el corte: se puede reanudar");
            }

            var anunciados = new List<string>();
            (await RetirarAsync(arnes, ConfiguracionConfirmada, anunciados)).Should().HaveCount(1, "MEDIDO: la retirada tampoco se niega");
            (await TenantsDelPilotoAsync(arnes)).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    [Fact]
    public async Task Con_un_nombre_del_piloto_ocupado_por_un_Tenant_sin_marcador_se_niega_antes_de_crear_el_fichero_de_credenciales()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var directorio = Directorio();
        try
        {
            using (var ambito = arnes.Servicios.CreateScope())
                await DelegacionDemoSeeder.AprovisionarTenantAsync(
                    ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>(),
                    CatalogoPilotoOutbound.NombreTenantT3, PerfilVocabularioTenant.ClienteDirecto, NullLogger.Instance,
                    CancellationToken.None, esOperadorCaeExterno: false);

            var antes = await arnes.RecuentoAsync();
            antes["Tenants del piloto"].Should().Be(1, "control: el Tenant que ocupa el nombre existe");

            var siembra = () => SembrarAsync(arnes, Opciones(directorio));

            await siembra.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"*«{CatalogoPilotoOutbound.NombreTenantT3}»*NO lleva el marcador de datos de demo*");
            (await arnes.RecuentoAsync()).Should().BeEquivalentTo(antes, "MEDIDO: no escribe ni en ese Tenant ni a su lado");
            (await CuentasDelDominioAsync(arnes)).Should().Be(0);
            Directory.GetFiles(directorio).Should().BeEmpty("MEDIDO: ni siquiera se abrió el fichero de credenciales");

            var retirada = () => RetirarAsync(arnes, ConfiguracionConfirmada, []);
            await retirada.Should().ThrowAsync<InvalidOperationException>("la retirada tampoco toca un Tenant sin marcador");
            (await arnes.RecuentoAsync()).Should().BeEquivalentTo(antes);
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    [Fact]
    public async Task Con_la_fecha_de_la_demostracion_fuera_de_margen_se_niega_antes_de_escribir_y_nombra_la_clave_de_este_modo()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var directorio = Directorio();
        try
        {
            var antes = await arnes.RecuentoAsync();

            var pasada = () => SembrarAsync(arnes, Opciones(directorio, DiaDeNegocio.Hoy().AddDays(-1)));
            var lejana = () => SembrarAsync(arnes, Opciones(directorio, DiaDeNegocio.Hoy().AddDays(OpcionesPilotoOutbound.MargenMaximoDias + 1)));

            (await pasada.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage($"PilotoOutbound:FechaDemostracion*es anterior a hoy*Queda por sembrar «{CatalogoPilotoOutbound.NombreTenantOperador}»*"))
                .Which.Message.Should().NotContain("DatosPrueba", "el rechazo nombra la clave de ESTE modo");
            await lejana.Should().ThrowAsync<InvalidOperationException>().WithMessage("PilotoOutbound:FechaDemostracion*queda a más de*");

            (await arnes.RecuentoAsync()).Should().BeEquivalentTo(antes, "MEDIDO: el rechazo no escribe");
            Directory.GetFiles(directorio).Should().BeEmpty("MEDIDO: ni siquiera se abrió el fichero de credenciales");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    [Fact]
    public async Task Una_cuenta_con_un_correo_del_piloto_que_pertenece_a_otro_Tenant_no_se_reutiliza()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var directorio = Directorio();
        try
        {
            var correoAjeno = PilotoOutboundAdministrativa.CuentasDe(Dominio).GestoraPrimera;
            Guid idCuenta;
            Guid idTenantAjeno;
            using (var ambito = arnes.Servicios.CreateScope())
            {
                var sp = ambito.ServiceProvider;
                var db = sp.GetRequiredService<CaeManagerDbContext>();
                var otro = new Tenant("Empresa Ajena de Prueba S.L.", PerfilVocabularioTenant.ClienteDirecto);
                using (AmbitoTenantExplicito.Establecer(otro.Id))
                {
                    db.Tenants.Add(otro);
                    await db.SaveChangesAsync();

                    var cuenta = new ApplicationUser
                    {
                        UserName = correoAjeno,
                        Email = correoAjeno,
                        NombreCompleto = "Cuenta ajena",
                        EmailConfirmed = true,
                        TenantId = otro.Id
                    };
                    (await sp.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(cuenta, "Xk7#prueba-No-Real-2026")).Succeeded.Should().BeTrue();
                    idCuenta = cuenta.Id;
                    idTenantAjeno = otro.Id;
                }
            }

            var siembra = () => SembrarAsync(arnes, Opciones(directorio));
            await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{correoAjeno}*Podrían ser cuentas reales*",
                "MEDIDO: una cuenta de otro Tenant no se hace Gestora CAE del piloto ni se le asigna cartera");

            (await TenantsDelPilotoAsync(arnes)).Should().BeEmpty("MEDIDO: la negativa fue previa a escribir: ningún Tenant del piloto");
            Directory.GetFiles(directorio).Should().BeEmpty("MEDIDO: ni siquiera se abrió el fichero de credenciales");
            (await arnes.ComoBootstrapAsync(b => b.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == idCuenta)))
                .TenantId.Should().Be(idTenantAjeno, "MEDIDO: la cuenta ajena sigue en su Tenant");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    [Fact]
    public async Task En_un_re_arranque_un_lote_que_ya_no_se_deja_medir_deja_una_advertencia_y_no_lanza_y_recien_escrito_si_lanza()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var directorio = Directorio();
        try
        {
            (await SembrarAsync(arnes, Opciones(directorio))).Discrepancias.Should().BeEmpty("control: el lote recién sembrado se mide y cuadra");

            var cuentas = PilotoOutboundAdministrativa.CuentasDe(Dominio);
            var opciones = new OpcionesPilotoOutbound(
                ArnesPilotoOutbound.FechaDemostracion(), ContactosPilotoOutbound.Crear(ArnesPilotoOutbound.CorreoDePrueba, dominio: null));

            Task MedirAsync(bool escribio, ILogger registro) => PilotoOutboundAutoverificacion.MedirYExigirOAvisarAsync(
                arnes.FabricaDeAmbitos, cuentas, opciones, escribio, tenantsConDatosDeOtraVersion: [], registro, CancellationToken.None);

            var sinCambios = () => MedirAsync(escribio: true, NullLogger.Instance);
            await sinCambios.Should().NotThrowAsync("control: sin tocar nada, ni siquiera el camino que exige lanza");

            // Lo que hace un ensayo: la Gestora CAE con la que se mide cambia de rol.
            var idOperador = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantOperador);
            await arnes.EnTenantAsync(idOperador, async (_, sp) =>
            {
                var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
                var gestora = (await userManager.FindByEmailAsync(cuentas.GestoraPrimera))!;
                (await userManager.RemoveFromRoleAsync(gestora, Roles.GestorCae)).Succeeded.Should().BeTrue();
                (await userManager.AddToRoleAsync(gestora, Roles.CoordinadorCae)).Succeeded.Should().BeTrue();
                return 0;
            });

            var avisos = new ArnesPilotoOutbound.RegistroDeAvisos();
            var reArranque = () => MedirAsync(escribio: false, avisos);
            await reArranque.Should().NotThrowAsync("MEDIDO: en un re-arranque, no poder medir no tumba el arranque");
            avisos.Avisos.Should().ContainSingle("MEDIDO: queda una advertencia, y solo una, con el motivo")
                .Which.Should().StartWith("Piloto Outbound, no se ha podido medir el lote ya sembrado")
                .And.Contain($"no tiene exactamente el rol {Roles.GestorCae}");

            var recienEscrito = () => MedirAsync(escribio: true, NullLogger.Instance);
            await recienEscrito.Should().ThrowAsync<InvalidOperationException>().WithMessage(
                $"*no tiene exactamente el rol {Roles.GestorCae}*",
                "MEDIDO: con el lote recién escrito, el mismo fallo sí lanza");
        }
        finally
        {
            Directory.Delete(directorio, recursive: true);
        }
    }

    /// <summary>El almacén real, que falla al eliminar en cuanto se cumple la condición: un corte a mitad de la retirada.</summary>
    private sealed class AlmacenQueFallaCuando(IFileStorageService real, Func<bool> condicion) : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            real.GuardarAsync(contenido, nombreArchivoOriginal, cancellationToken);

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            real.AbrirAsync(identificador, cancellationToken);

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            condicion() ? throw new IOException("fallo inyectado por la prueba al eliminar del almacén") : real.EliminarAsync(identificador, cancellationToken);
    }
}
