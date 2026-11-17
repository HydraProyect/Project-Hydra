using CaeManager.Application.Common;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// El almacén de la siembra Inbound en memoria. Exige un Tenant resuelto para guardar y cuelga cada clave de su carpeta, como
/// <c>DiskFileStorageService</c> (que aquí no se monta): la siembra guarda dentro del ámbito del Tenant TEC, no fuera. Cuenta
/// las llamadas para probar que una siembra inerte o rechazada no toca el almacén.
/// </summary>
internal sealed class AlmacenDeSiembraInboundEnMemoria : IFileStorageService
{
    private readonly Dictionary<string, byte[]> _archivos = [];

    public int Llamadas { get; private set; }

    public IReadOnlyDictionary<string, byte[]> Archivos => _archivos;

    public async Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
    {
        Llamadas++;
        var tenantId = AmbitoTenantExplicito.TenantIdActual
            ?? throw new InvalidOperationException("No se puede guardar un archivo sin un tenant resuelto.");
        using var memoria = new MemoryStream();
        await contenido.CopyToAsync(memoria, cancellationToken);
        var clave = $"{tenantId:N}/{Guid.NewGuid():N}{Path.GetExtension(nombreArchivoOriginal)}";
        _archivos[clave] = memoria.ToArray();
        return clave;
    }

    public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default)
    {
        Llamadas++;
        return Task.FromResult<Stream>(new MemoryStream(_archivos[identificador]));
    }

    public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
    {
        Llamadas++;
        _archivos.Remove(identificador);
        return Task.CompletedTask;
    }
}

/// <summary>
/// <b>La siembra Inbound de Development deja lo que declara, en el Tenant que lo posee, y una segunda pasada no escribe
/// nada.</b> Corre sobre PostgreSQL real con el <see cref="CaeManagerDbContext"/> como cae_app_runtime (RLS efectiva), igual
/// que el arranque fuera de la base local; cada recuento se lee dentro del ámbito del Tenant propietario de la fila, así que
/// una fila sembrada en el Tenant equivocado no se contaría.
/// </summary>
public sealed class InboundDemoSeederFixture : IAsyncLifetime
{
    internal ArnesDeArranqueRuntime Arnes { get; private set; } = null!;
    internal IConfiguration Configuracion { get; private set; } = null!;
    internal AlmacenDeSiembraInboundEnMemoria Almacen { get; } = new();

    public async Task InitializeAsync()
    {
        Arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        Configuracion = new ConfigurationBuilder()
            .AddConfiguration(Arnes.Servicios.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?> { [InboundDemoSeeder.ClaveConfiguracion] = "true" })
            .Build();

        await SembrarAsync();
    }

    internal async Task SembrarAsync()
    {
        using var ambito = Arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        await InboundDemoSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IUserStore<ApplicationUser>>(), Almacen, Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);

        // Como en Program.cs: la línea Inbound, con el contexto de bootstrap y después de la siembra que crea su Tenant.
        await using var bootstrap = sp.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        await LineaInboundDemoSeeder.SeedAsync(bootstrap, Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
    }

    public Task DisposeAsync() => Arnes.DisposeAsync().AsTask();
}

public class InboundDemoSeederTests(InboundDemoSeederFixture fixture) : IClassFixture<InboundDemoSeederFixture>
{
    [Fact]
    public async Task El_Tenant_TCE_tiene_un_contrato_de_acceso_en_cada_estado()
    {
        var porEstado = await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, async db =>
            (await db.ContratosAcceso.ToListAsync()).GroupBy(c => c.EstadoEfectivo).ToDictionary(g => g.Key, g => g.Count()));

        porEstado.Should().BeEquivalentTo(new Dictionary<EstadoContratoAcceso, int>
        {
            [EstadoContratoAcceso.Activo] = 2,
            [EstadoContratoAcceso.PendienteDeConfirmar] = 1,
            [EstadoContratoAcceso.Suspendido] = 1,
            [EstadoContratoAcceso.Finalizado] = 1,
            [EstadoContratoAcceso.Cancelado] = 1,
            [EstadoContratoAcceso.Rechazado] = 1,
        });
    }

    [Fact]
    public async Task Los_contratos_llevan_condiciones_historial_y_su_Tenant_TEC()
    {
        var tecs = await IdsAsync(InboundDemoSeeder.NombreTenantMoncayo, InboundDemoSeeder.NombreTenantRibera, InboundDemoSeeder.NombreTenantSomontano);

        await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, async db =>
        {
            var contratos = await db.ContratosAcceso.Include(c => c.HistorialVigencia).ToListAsync();
            var activo = contratos.Single(c => c.Notas == InboundDemoSeeder.Contratos.MoncayoActivo);
            activo.Condiciones.Should().NotBe(CondicionesContratoAcceso.SinFijar);
            activo.CupoTrabajadores.Should().Be(8);
            activo.HistorialVigencia.Select(h => h.Origen).Should().BeEquivalentTo([OrigenCambioVigencia.Edicion, OrigenCambioVigencia.Renovacion]);

            contratos.Single(c => c.Notas == InboundDemoSeeder.Contratos.MoncayoSinVincular).TenantContratistaId.Should().BeNull();
            contratos.Where(c => c.Notas != InboundDemoSeeder.Contratos.MoncayoSinVincular)
                .Should().OnlyContain(c => c.TenantContratistaId != null && tecs.Contains(c.TenantContratistaId.Value));
            return 0;
        });
    }

    /// <summary>
    /// Cada contrato apunta al Tenant TEC de su Empresa contratista, y la Empresa contraparte local que nombra el contrato
    /// tiene el mismo CIF que la Empresa propia de ese Tenant TEC.
    /// </summary>
    [Theory]
    [InlineData(InboundDemoSeeder.Contratos.MoncayoActivo, InboundDemoSeeder.NombreTenantMoncayo)]
    [InlineData(InboundDemoSeeder.Contratos.MoncayoFinalizado, InboundDemoSeeder.NombreTenantMoncayo)]
    [InlineData(InboundDemoSeeder.Contratos.RiberaPendiente, InboundDemoSeeder.NombreTenantRibera)]
    [InlineData(InboundDemoSeeder.Contratos.RiberaSuspendido, InboundDemoSeeder.NombreTenantRibera)]
    [InlineData(InboundDemoSeeder.Contratos.SomontanoRechazado, InboundDemoSeeder.NombreTenantSomontano)]
    [InlineData(InboundDemoSeeder.Contratos.SomontanoCancelado, InboundDemoSeeder.NombreTenantSomontano)]
    public async Task Cada_contrato_va_al_Tenant_TEC_de_su_Empresa_contratista(string notas, string nombreTenantTec)
    {
        var tecId = (await IdsAsync(nombreTenantTec)).Single();
        var (vinculo, cifContraparte) = await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, async db =>
        {
            var contrato = await db.ContratosAcceso.SingleAsync(c => c.Notas == notas);
            var cif = await db.Empresas.Where(e => e.Id == contrato.EmpresaContratistaId).Select(e => e.Cif).SingleAsync();
            return (contrato.TenantContratistaId, cif);
        });
        var cifPropia = await EnTenantAsync(nombreTenantTec, db => db.Empresas.Where(e => e.EsPropia).Select(e => e.Cif).SingleAsync());

        vinculo.Should().Be(tecId);
        cifContraparte.Should().NotBeNullOrEmpty().And.Be(cifPropia);
    }

    [Fact]
    public async Task El_Tenant_TCE_tiene_sus_plantillas_de_requisitos_sin_requisito_base()
    {
        var (plantillas, requisitos, conBase) = await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, async db =>
        {
            var todas = await db.PlantillasRequisitos.Include(p => p.Requisitos).ToListAsync();
            return (todas.Count, todas.Sum(p => p.Requisitos.Count), todas.SelectMany(p => p.Requisitos).Count(r => r.RequisitoBaseId != null));
        });

        plantillas.Should().Be(2);
        requisitos.Should().Be(7);
        conBase.Should().Be(0, "el catálogo base de B-13 no tiene hoy requisitos activos que adoptar");
    }

    /// <summary>
    /// Solo el Tenant TCE tiene la línea Inbound, y la ve como cae_app_runtime dentro de su ámbito: el contexto de bootstrap
    /// escribió una única fila y en el Tenant correcto.
    /// </summary>
    [Fact]
    public async Task Solo_el_Tenant_TCE_tiene_la_linea_Inbound_vigente()
    {
        var lineas = await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, db => db.ProductosContratados.ToListAsync());
        lineas.Should().ContainSingle().Which.Should().Match<ProductoContratado>(p =>
            p.Linea == LineaProductoContratado.Inbound && p.Estado == EstadoProductoContratado.Vigente
            && p.PagadorStripeCustomerId == LineaInboundDemoSeeder.PagadorDemo);

        foreach (var tec in new[] { InboundDemoSeeder.NombreTenantMoncayo, InboundDemoSeeder.NombreTenantRibera, InboundDemoSeeder.NombreTenantSomontano })
            (await EnTenantAsync(tec, db => db.ProductosContratados.CountAsync())).Should().Be(0, $"{tec} no contrata la línea Inbound");
    }

    [Fact]
    public async Task La_Gestora_CAE_del_Tenant_TCE_tiene_la_cartera_del_Tenant_entero()
    {
        var gestoraId = await UsuarioIdAsync(InboundDemoSeeder.EmailGestoraTce);

        var carteras = await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, db =>
            db.AsignacionesCartera.CountAsync(a => a.UsuarioId == gestoraId && a.Estado == EstadoAsignacion.Vigente));

        carteras.Should().Be(1);
    }

    [Theory]
    [InlineData(InboundDemoSeeder.NombreTenantMoncayo, InboundDemoSeeder.EmailUsuarioMoncayo)]
    [InlineData(InboundDemoSeeder.NombreTenantRibera, InboundDemoSeeder.EmailUsuarioRibera)]
    [InlineData(InboundDemoSeeder.NombreTenantSomontano, InboundDemoSeeder.EmailUsuarioSomontano)]
    public async Task Cada_Tenant_TEC_tiene_su_Usuario_unico_con_2FA_y_su_documentacion(string nombreTenant, string email)
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var tenantId = (await IdsAsync(nombreTenant)).Single();
        var usuarios = await bootstrap.Users.IgnoreQueryFilters().Where(u => u.TenantId == tenantId).ToListAsync();
        usuarios.Should().ContainSingle().Which.Should().Match<ApplicationUser>(u => u.Email == email && u.TwoFactorEnabled);

        var (propias, trabajadores, documentos) = await EnTenantAsync(nombreTenant, async db =>
            (await db.Empresas.CountAsync(e => e.EsPropia), await db.Trabajadores.CountAsync(), await db.Documentos.CountAsync()));
        propias.Should().Be(1);
        trabajadores.Should().Be(3);
        documentos.Should().Be(9);
    }

    [Fact]
    public async Task Montajes_Moncayo_autoriza_la_lectura_del_contrato_activo_y_revoco_la_del_finalizado()
    {
        var tceId = (await IdsAsync(InboundDemoSeeder.NombreTenantTce)).Single();
        var contratos = await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, db =>
            db.ContratosAcceso.ToDictionaryAsync(c => c.Notas!, c => c.Id));

        await EnTenantAsync(InboundDemoSeeder.NombreTenantMoncayo, async db =>
        {
            var autorizaciones = await db.AutorizacionesLecturaDocumental.ToListAsync();
            autorizaciones.Should().HaveCount(2).And.OnlyContain(a => a.TenantDestinatarioId == tceId);

            var vigente = autorizaciones.Single(a => a.ContratoAccesoId == contratos[InboundDemoSeeder.Contratos.MoncayoActivo]);
            vigente.EstaRevocada.Should().BeFalse();
            vigente.IncluyeDocumentosEmpresa.Should().BeTrue();
            (await db.AutorizacionesLecturaTrabajadores.CountAsync(t => t.AutorizacionId == vigente.Id)).Should().Be(2);

            autorizaciones.Single(a => a.ContratoAccesoId == contratos[InboundDemoSeeder.Contratos.MoncayoFinalizado])
                .EstaRevocada.Should().BeTrue();
            return 0;
        });

        foreach (var otro in new[] { InboundDemoSeeder.NombreTenantRibera, InboundDemoSeeder.NombreTenantSomontano })
            (await EnTenantAsync(otro, db => db.AutorizacionesLecturaDocumental.CountAsync())).Should().Be(0, $"{otro} no autoriza ninguna lectura");
    }

    [Fact]
    public async Task Ningun_correo_sembrado_es_entregable()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var tenants = await IdsAsync([.. InboundDemoSeeder.NombresTenants]);
        tenants.Should().HaveCount(4);

        (await bootstrap.Users.IgnoreQueryFilters().Where(u => tenants.Contains(u.TenantId)).Select(u => u.Email!).ToListAsync())
            .Should().HaveCount(5).And.OnlyContain(e => e.EndsWith("@caemanager.local"));
    }

    /// <summary>
    /// Ni filas nuevas ni UPDATE: además de contar filas, compara los sellos de concurrencia de las cuentas y las versiones de
    /// las autorizaciones, y cuenta el registro de auditoría de los cuatro Tenants, que recoge cualquier escritura auditada.
    /// </summary>
    [Fact]
    public async Task Una_segunda_siembra_no_escribe_nada()
    {
        var antes = await ContarAsync();
        var sellosAntes = await SellosAsync();
        await fixture.SembrarAsync();
        (await ContarAsync()).Should().BeEquivalentTo(antes);
        (await SellosAsync()).Should().BeEquivalentTo(sellosAntes);
    }

    /// <summary>
    /// Cada Documento del Tenant TEC Montajes Moncayo (los tres de Empresa y los seis de Trabajador) lleva un PDF de prueba
    /// en la carpeta de su Tenant, con el texto de la constante: el de la aptitud médica, solo la conclusión.
    /// </summary>
    [Fact]
    public async Task Cada_Documento_del_Tenant_TEC_Moncayo_lleva_un_PDF_de_prueba_en_su_carpeta()
    {
        var moncayoId = (await IdsAsync(InboundDemoSeeder.NombreTenantMoncayo)).Single();
        var documentos = await EnTenantAsync(InboundDemoSeeder.NombreTenantMoncayo, async db =>
        {
            var tipos = await db.TiposDocumento.ToDictionaryAsync(t => t.Id, t => t.Nombre);
            return (await db.Documentos.ToListAsync()).Select(d => (d.ArchivoUrl, Tipo: tipos[d.TipoDocumentoId])).ToList();
        });

        documentos.Should().HaveCount(9, "control: se miran los nueve Documentos sembrados, no un conjunto vacío");
        documentos.Should().OnlyContain(d => d.ArchivoUrl != null, "MEDIDO: ningún Documento de Moncayo nace sin archivo");
        documentos.Select(d => d.ArchivoUrl!).Should().OnlyHaveUniqueItems("cada Documento tiene su propio fichero");

        foreach (var (clave, tipo) in documentos.Select(d => (d.ArchivoUrl!, d.Tipo)))
        {
            clave.Should().StartWith($"{moncayoId:N}/", "el fichero se guarda en la carpeta del Tenant propietario");
            clave.Should().EndWith(".pdf");

            var bytes = fixture.Almacen.Archivos[clave];
            Encoding.Latin1.GetString(bytes, 0, 5).Should().Be("%PDF-");
            var texto = tipo == "Certificado de aptitud médica" ? InboundDemoPdf.TextoDeAptitud : InboundDemoPdf.TextoGeneral;
            Encoding.Latin1.GetString(bytes).Should().Contain($"({texto}) Tj");
        }

        documentos.Count(d => fixture.Almacen.Archivos[d.ArchivoUrl!].AsSpan().SequenceEqual(InboundDemoPdf.Generar(InboundDemoPdf.TextoDeAptitud)))
            .Should().Be(2, "los dos certificados de aptitud médica sembrados solo dicen «Apto»");
    }

    [Fact]
    public async Task Los_Tenants_TEC_sin_autorizacion_de_lectura_no_llevan_archivos()
    {
        foreach (var otro in new[] { InboundDemoSeeder.NombreTenantRibera, InboundDemoSeeder.NombreTenantSomontano })
            (await EnTenantAsync(otro, db => db.Documentos.CountAsync(d => d.ArchivoUrl != null))).Should().Be(0, otro);

        var moncayoId = (await IdsAsync(InboundDemoSeeder.NombreTenantMoncayo)).Single();
        fixture.Almacen.Archivos.Keys.Should().OnlyContain(k => k.StartsWith($"{moncayoId:N}/"), "ningún fichero cae en otra carpeta");
    }

    /// <summary>La siembra sigue sin crear Evidencias: las crea la validación en directo del Gestor CAE.</summary>
    [Fact]
    public async Task La_siembra_no_crea_Evidencias_Inbound()
    {
        (await ContarAsync())["EvidenciasInbound"].Should().Be(0);
    }

    /// <summary>Una segunda pasada ni escribe un fichero más ni cambia la clave de ninguno.</summary>
    [Fact]
    public async Task Una_segunda_siembra_no_duplica_archivos()
    {
        var clavesAntes = fixture.Almacen.Archivos.Keys.Order().ToList();
        var referenciasAntes = await EnTenantAsync(InboundDemoSeeder.NombreTenantMoncayo, db =>
            db.Documentos.Select(d => d.ArchivoUrl!).OrderBy(c => c).ToListAsync());
        var llamadasAntes = fixture.Almacen.Llamadas;

        await fixture.SembrarAsync();

        fixture.Almacen.Archivos.Keys.Order().Should().Equal(clavesAntes);
        fixture.Almacen.Llamadas.Should().Be(llamadasAntes, "una segunda pasada no toca el almacén");
        (await EnTenantAsync(InboundDemoSeeder.NombreTenantMoncayo, db =>
            db.Documentos.Select(d => d.ArchivoUrl!).OrderBy(c => c).ToListAsync())).Should().Equal(referenciasAntes);
        clavesAntes.Should().HaveCount(9);
    }

    private async Task<Dictionary<Guid, string>> SellosAsync()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var tenants = await IdsAsync([.. InboundDemoSeeder.NombresTenants]);
        var cuentas = await bootstrap.Users.IgnoreQueryFilters().Where(u => tenants.Contains(u.TenantId))
            .Select(u => new { u.Id, Sello = u.ConcurrencyStamp + "|" + u.SecurityStamp + "|" + u.TwoFactorEnabled }).ToListAsync();
        var autorizaciones = await bootstrap.AutorizacionesLecturaDocumental.IgnoreQueryFilters().Where(a => tenants.Contains(a.TenantId))
            .Select(a => new { a.Id, Sello = a.Version.ToString() + "|" + a.RevocadaEnUtc }).ToListAsync();
        return cuentas.Select(c => (c.Id, c.Sello)).Concat(autorizaciones.Select(a => (a.Id, a.Sello)))
            .ToDictionary(x => x.Id, x => x.Sello!);
    }

    private async Task<Dictionary<string, int>> ContarAsync()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var tenants = await IdsAsync([.. InboundDemoSeeder.NombresTenants]);
        return new Dictionary<string, int>
        {
            ["ProductosContratados"] = await bootstrap.ProductosContratados.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Auditoria"] = await bootstrap.RegistrosAuditoria.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Requisitos"] = await bootstrap.PlantillasRequisitos.IgnoreQueryFilters().Where(e => tenants.Contains(e.TenantId)).SelectMany(p => p.Requisitos).CountAsync(),
            ["Operaciones"] = await bootstrap.AsignacionesOperacion.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.PropietarioTenantId)),
            ["Tenants"] = tenants.Count,
            ["Usuarios"] = await bootstrap.Users.IgnoreQueryFilters().CountAsync(u => tenants.Contains(u.TenantId)),
            ["Empresas"] = await bootstrap.Empresas.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Centros"] = await bootstrap.Centros.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Trabajadores"] = await bootstrap.Trabajadores.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Documentos"] = await bootstrap.Documentos.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["EvidenciasInbound"] = await bootstrap.EvidenciasInbound.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["ContratosAcceso"] = await bootstrap.ContratosAcceso.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["HistorialVigencia"] = await bootstrap.HistorialVigenciaContratos.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Plantillas"] = await bootstrap.PlantillasRequisitos.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Autorizaciones"] = await bootstrap.AutorizacionesLecturaDocumental.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["AutorizacionesTrabajador"] = await bootstrap.AutorizacionesLecturaTrabajadores.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Carteras"] = await bootstrap.AsignacionesCartera.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.PropietarioTenantId)),
        };
    }

    private async Task<List<Guid>> IdsAsync(params string[] nombres)
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        return await bootstrap.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
    }

    private async Task<Guid> UsuarioIdAsync(string email)
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        return await bootstrap.Users.IgnoreQueryFilters().Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    /// <summary>Lee como cae_app_runtime dentro del ámbito del Tenant: lo que no sea de ese Tenant no se ve.</summary>
    private async Task<T> EnTenantAsync<T>(string nombreTenant, Func<CaeManagerDbContext, Task<T>> leer)
    {
        var tenantId = (await IdsAsync(nombreTenant)).Single();
        using var ambito = fixture.Arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(tenantId))
            return await leer(contexto);
    }
}

/// <summary>
/// <b>Lo que acota las dos siembras cuando el Tenant no es suyo</b>, en una base aparte: la línea Inbound con el rol
/// propietario no se escribe sin el Tenant TCE ni sobre uno sin el marcador de datos de demo, y la siembra Inbound se niega
/// antes de escribir una sola cuenta si uno de sus nombres lo ocupa un Tenant sin marcador.
/// </summary>
public sealed class InboundDemoSeederMarcadorTests : IAsyncLifetime
{
    private ArnesDeArranqueRuntime _arnes = null!;
    private IConfiguration _configuracion = null!;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        _configuracion = new ConfigurationBuilder()
            .AddConfiguration(_arnes.Servicios.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?> { [InboundDemoSeeder.ClaveConfiguracion] = "true" })
            .Build();
    }

    public Task DisposeAsync() => _arnes.DisposeAsync().AsTask();

    [Fact]
    public async Task Sin_el_Tenant_TCE_o_sin_su_marcador_de_demo_ninguna_de_las_dos_siembras_escribe()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        await using var bootstrap = sp.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

        var sinTenant = () => LineaInboundDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await sinTenant.Should().ThrowAsync<InvalidOperationException>().WithMessage("*necesita el Tenant*");

        // Un Tenant que se llama como el TCE de la siembra pero no lleva el marcador de datos de demo.
        var ajeno = new CaeManager.Domain.Tenants.Tenant(InboundDemoSeeder.NombreTenantTce, CaeManager.Domain.Tenants.PerfilVocabularioTenant.ClienteDirecto);
        using (AmbitoTenantExplicito.Establecer(ajeno.Id))
        {
            bootstrap.Tenants.Add(ajeno);
            await bootstrap.SaveChangesAsync();
        }

        var sinMarcador = () => LineaInboundDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await sinMarcador.Should().ThrowAsync<InvalidOperationException>().WithMessage("*marcador de datos de demo*");

        var siembra = () => InboundDemoSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IUserStore<ApplicationUser>>(), new AlmacenDeSiembraInboundEnMemoria(), _configuracion,
            EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage("*sin el marcador de datos de demo*");

        await using var lectura = sp.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        (await lectura.Users.IgnoreQueryFilters().CountAsync(u => u.Email!.EndsWith(".inbound@caemanager.local")))
            .Should().Be(0, "la siembra se niega antes de crear ninguna cuenta");
        (await lectura.ProductosContratados.IgnoreQueryFilters().CountAsync(p => p.TenantId == ajeno.Id)).Should().Be(0);
        (await lectura.Tenants.CountAsync(t => InboundDemoSeeder.NombresTenants.Contains(t.Nombre))).Should().Be(1,
            "ni siquiera aprovisiona los Tenants TEC");

        // La retirada aplica la misma regla: un Tenant con el nombre de la siembra pero sin marcador podría ser real.
        var retirada = () => RetiradaTenantDemoService.ValidarTenantRetirableAsync(sp.GetRequiredService<CaeManagerDbContext>(), ajeno.Id);
        await retirada.Should().ThrowAsync<InvalidOperationException>().WithMessage("*NO lleva el marcador de datos de demo*");
    }
}

/// <summary>
/// <b>Lo que el revisor cambia desde la interfaz no tumba el arranque ni se vuelve a sembrar encima</b>, en una base aparte:
/// pasar a la Gestora CAE a otro rol, repetir las notas de un contrato de la siembra en otro y dar de baja un Trabajador.
/// </summary>
public sealed class InboundDemoSeederEdicionesTests : IAsyncLifetime
{
    private ArnesDeArranqueRuntime _arnes = null!;
    private IConfiguration _configuracion = null!;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        _configuracion = new ConfigurationBuilder()
            .AddConfiguration(_arnes.Servicios.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?> { [InboundDemoSeeder.ClaveConfiguracion] = "true" })
            .Build();
    }

    public Task DisposeAsync() => _arnes.DisposeAsync().AsTask();

    private readonly AlmacenDeSiembraInboundEnMemoria _almacen = new();

    private async Task SembrarAsync()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        await InboundDemoSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IUserStore<ApplicationUser>>(), _almacen, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
    }

    /// <summary>
    /// Un Documento vivo de Montajes Moncayo sin archivo (una base sembrada antes de este paso) recibe el suyo en la siguiente
    /// pasada, y los que ya lo tienen, ni su clave ni su fichero cambian.
    /// </summary>
    [Fact]
    public async Task Un_Documento_sin_archivo_recibe_su_PDF_y_los_demas_no_se_tocan()
    {
        await SembrarAsync();

        Guid moncayoId;
        await using (var bootstrap = _arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear())
            moncayoId = await bootstrap.Tenants.Where(t => t.Nombre == InboundDemoSeeder.NombreTenantMoncayo).Select(t => t.Id).SingleAsync();

        async Task<Dictionary<Guid, string?>> ReferenciasAsync()
        {
            using var ambito = _arnes.Servicios.CreateScope();
            var db = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            using (AmbitoTenantExplicito.Establecer(moncayoId))
                return await db.Documentos.ToDictionaryAsync(d => d.Id, d => d.ArchivoUrl);
        }

        var antes = await ReferenciasAsync();
        antes.Should().HaveCount(9).And.OnlyContain(r => r.Value != null);
        var sinArchivo = antes.Keys.First();

        using (var ambito = _arnes.Servicios.CreateScope())
        {
            var db = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            using (AmbitoTenantExplicito.Establecer(moncayoId))
                await db.Documentos.Where(d => d.Id == sinArchivo).ExecuteUpdateAsync(s => s.SetProperty(d => d.ArchivoUrl, (string?)null));
        }

        var ficherosAntes = _almacen.Archivos.Count;
        await SembrarAsync();

        var despues = await ReferenciasAsync();
        despues[sinArchivo].Should().NotBeNullOrEmpty().And.NotBe(antes[sinArchivo], "recibe un fichero nuevo, el viejo ya no cuelga de ningún Documento");
        foreach (var (id, clave) in antes.Where(r => r.Key != sinArchivo))
            despues[id].Should().Be(clave, "un Documento que ya tenía archivo no se toca");
        _almacen.Archivos.Should().HaveCount(ficherosAntes + 1, "un solo fichero nuevo");
    }

    [Fact]
    public async Task Tras_editar_desde_la_interfaz_otra_siembra_no_lanza_ni_repone_nada()
    {
        await SembrarAsync();

        Guid tceId, moncayoId;
        await using (var bootstrap = _arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear())
        {
            tceId = await bootstrap.Tenants.Where(t => t.Nombre == InboundDemoSeeder.NombreTenantTce).Select(t => t.Id).SingleAsync();
            moncayoId = await bootstrap.Tenants.Where(t => t.Nombre == InboundDemoSeeder.NombreTenantMoncayo).Select(t => t.Id).SingleAsync();
        }

        using (var ambito = _arnes.Servicios.CreateScope())
        {
            var sp = ambito.ServiceProvider;
            var db = sp.GetRequiredService<CaeManagerDbContext>();
            var usuarios = sp.GetRequiredService<UserManager<ApplicationUser>>();

            using (AmbitoTenantExplicito.Establecer(tceId))
            {
                // La Gestora CAE pasa a Coordinadora CAE.
                var gestora = (await usuarios.FindByEmailAsync(InboundDemoSeeder.EmailGestoraTce))!;
                (await usuarios.RemoveFromRoleAsync(gestora, Roles.GestorCae)).Succeeded.Should().BeTrue();
                (await usuarios.AddToRoleAsync(gestora, Roles.CoordinadorCae)).Succeeded.Should().BeTrue();

                // Un contrato nuevo con las mismas notas que uno de la siembra.
                var original = await db.ContratosAcceso.SingleAsync(c => c.Notas == InboundDemoSeeder.Contratos.MoncayoActivo);
                db.ContratosAcceso.Add(ContratoAcceso.Crear(
                    original.ClienteEmpresarialId, original.CentroId, original.EmpresaContratistaId,
                    DiaDeNegocio.Hoy(), null, InboundDemoSeeder.Contratos.MoncayoActivo));
                await db.SaveChangesAsync();
            }

            using (AmbitoTenantExplicito.Establecer(moncayoId))
            {
                var trabajador = await db.Trabajadores.OrderBy(t => t.Dni).FirstAsync();
                trabajador.MarcarComoEliminado(Guid.NewGuid());
                await db.SaveChangesAsync();
            }
        }

        var resembrar = SembrarAsync;
        await resembrar.Should().NotThrowAsync();

        // Después, el revisor corrige los DNI de los Trabajadores del Tenant TEC (los de la siembra quedan libres en el índice
        // único) y da de baja todo: la guarda cuenta las bajas y no vuelve a sembrar encima. Sin cambiar los DNI, la guarda
        // anterior chocaba con el índice y el test pasaría igual.
        using (var ambito = _arnes.Servicios.CreateScope())
        {
            var db = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            using (AmbitoTenantExplicito.Establecer(moncayoId))
            {
                await db.Trabajadores.IgnoreQueryFilters().Where(t => t.TenantId == moncayoId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Dni, t => "X" + t.Dni));
                foreach (var t in await db.Trabajadores.ToListAsync())
                    t.MarcarComoEliminado(Guid.NewGuid());
                foreach (var d in await db.Documentos.ToListAsync())
                    d.MarcarComoEliminado(Guid.NewGuid());
                await db.SaveChangesAsync();
                (await db.Trabajadores.AnyAsync() || await db.Documentos.AnyAsync()).Should().BeFalse("MEDIDO: no queda nada visible");
            }
        }

        await resembrar.Should().NotThrowAsync();

        await using var lectura = _arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        (await lectura.ContratosAcceso.IgnoreQueryFilters().CountAsync(c => c.TenantId == tceId)).Should().Be(8, "los siete de la siembra y el del revisor");
        (await lectura.AsignacionesCartera.IgnoreQueryFilters().CountAsync(a => a.PropietarioTenantId == tceId)).Should().Be(1,
            "la cartera de la Gestora CAE no se vuelve a emitir");
        (await lectura.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.TenantId == moncayoId)).Should().Be(3, "los dados de baja no se reponen");
        (await lectura.Documentos.IgnoreQueryFilters().CountAsync(d => d.TenantId == moncayoId)).Should().Be(9, "los dados de baja no se reponen");
        (await lectura.AutorizacionesLecturaDocumental.IgnoreQueryFilters().CountAsync(a => a.TenantId == moncayoId)).Should().Be(2);
    }

    [Fact]
    public async Task Una_cuenta_que_una_pasada_cortada_dejo_sin_rol_se_completa_y_la_cartera_se_emite()
    {
        // El corte entre el alta de la cuenta de la Gestora CAE y la de su rol: el Tenant TCE ya existe con su marcador y la
        // cuenta, sin ningún rol.
        // Se aprovisiona como lo hace la siembra: marcador, parámetros y catálogo de Tipos de documento en el mismo guardado.
        Guid tceId;
        using (var ambito = _arnes.Servicios.CreateScope())
        {
            var sp = ambito.ServiceProvider;
            tceId = await DelegacionDemoSeeder.AprovisionarTenantAsync(
                sp.GetRequiredService<CaeManagerDbContext>(), InboundDemoSeeder.NombreTenantTce,
                CaeManager.Domain.Tenants.PerfilVocabularioTenant.ClienteDirecto, NullLogger.Instance, CancellationToken.None,
                esOperadorCaeExterno: false, marcarComoDemo: true);

            var usuarios = sp.GetRequiredService<UserManager<ApplicationUser>>();
            using (AmbitoTenantExplicito.Establecer(tceId))
            {
                var cuenta = new ApplicationUser
                {
                    UserName = InboundDemoSeeder.EmailGestoraTce,
                    Email = InboundDemoSeeder.EmailGestoraTce,
                    NombreCompleto = "Gestora CAE cortada (Inbound demo)",
                    EmailConfirmed = true,
                    TenantId = tceId,
                };
                (await usuarios.CreateAsync(cuenta, CredencialesDemo.Resolver(_configuracion, EntornoDePrueba.Desarrollo).Contrasena))
                    .Succeeded.Should().BeTrue();
            }
        }

        var sembrar = SembrarAsync;
        await sembrar.Should().NotThrowAsync();

        using (var ambito = _arnes.Servicios.CreateScope())
        {
            var usuarios = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            using (AmbitoTenantExplicito.Establecer(tceId))
            {
                var gestora = (await usuarios.FindByEmailAsync(InboundDemoSeeder.EmailGestoraTce))!;
                (await usuarios.GetRolesAsync(gestora)).Should().Equal(Roles.GestorCae);
                (await ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>().AceptacionesTerminos
                    .CountAsync(a => a.UsuarioId == gestora.Id)).Should().Be(1, "la cuenta cortada recibe también la aceptación de términos");
            }
        }

        await using var lectura = _arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        (await lectura.AsignacionesCartera.IgnoreQueryFilters().CountAsync(a => a.PropietarioTenantId == tceId)).Should().Be(1);
        (await lectura.HistorialVigenciaContratos.IgnoreQueryFilters().AnyAsync(h => h.TenantId == tceId)).Should().BeTrue();
    }
}

/// <summary>La guarda de <see cref="InboundDemoSeeder"/>, sin base de datos: decide antes de leer o escribir nada.</summary>
public class InboundDemoSeederArranqueTests
{
    [Theory]
    [InlineData("Production", true, true, "*solo puede activarse en Development*")]
    [InlineData("Staging", true, true, "*solo puede activarse en Development*")]
    [InlineData("Development", true, true, null)]
    [InlineData("Production", false, true, null)]
    [InlineData("Production", true, false, null)]
    public void La_guarda_rechaza_todo_entorno_que_no_sea_Development_y_es_inerte_sin_las_claves(
        string entorno, bool datosPrueba, bool inbound, string? mensajeEsperado)
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = datosPrueba.ToString(),
            [InboundDemoSeeder.ClaveConfiguracion] = inbound.ToString()
        }).Build();

        var llamada = () => InboundDemoSeeder.RechazarFueraDeDevelopment(configuracion, new EntornoDePrueba(entorno));

        if (mensajeEsperado is null)
            llamada.Should().NotThrow();
        else
            llamada.Should().Throw<InvalidOperationException>().WithMessage(mensajeEsperado);
    }

    [Fact]
    public async Task Fuera_de_Development_la_siembra_lanza_antes_de_escribir_nada()
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = "true",
            [InboundDemoSeeder.ClaveConfiguracion] = "true"
        }).Build();

        var almacen = new AlmacenDeSiembraInboundEnMemoria();
        var siembra = () => InboundDemoSeeder.SeedAsync(
            dbContext: null!, userManager: null!, userStore: null!, almacen, configuracion, new EntornoDePrueba("Staging"), NullLogger.Instance);

        await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage("*solo puede activarse en Development*");
        almacen.Llamadas.Should().Be(0, "ni un fichero antes de rechazar el entorno");
    }

    /// <summary>
    /// Los PDF de prueba solo existen con las dos claves: sin <c>DatosPrueba:Activo</c> o sin la clave Inbound la siembra es
    /// inerte y no toca el almacén, en cualquier entorno.
    /// </summary>
    [Theory]
    [InlineData("Production", false, true)]
    [InlineData("Production", true, false)]
    [InlineData("Development", false, true)]
    [InlineData("Development", true, false)]
    public async Task Sin_las_dos_claves_la_siembra_es_inerte_y_no_toca_el_almacen(string entorno, bool datosPrueba, bool inbound)
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = datosPrueba.ToString(),
            [InboundDemoSeeder.ClaveConfiguracion] = inbound.ToString()
        }).Build();

        var almacen = new AlmacenDeSiembraInboundEnMemoria();
        var siembra = () => InboundDemoSeeder.SeedAsync(
            dbContext: null!, userManager: null!, userStore: null!, almacen, configuracion, new EntornoDePrueba(entorno), NullLogger.Instance);

        await siembra.Should().NotThrowAsync();
        almacen.Llamadas.Should().Be(0);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task La_linea_Inbound_con_el_rol_propietario_lanza_fuera_de_Development_antes_de_leer_nada(string entorno)
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = "true",
            [InboundDemoSeeder.ClaveConfiguracion] = "true"
        }).Build();

        var siembra = () => LineaInboundDemoSeeder.SeedAsync(dbContextBootstrap: null!, configuracion, new EntornoDePrueba(entorno), NullLogger.Instance);

        await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage("*solo puede activarse en Development*");
    }

    [Fact]
    public async Task La_linea_Inbound_es_inerte_sin_las_claves()
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = "true"
        }).Build();

        var siembra = () => LineaInboundDemoSeeder.SeedAsync(dbContextBootstrap: null!, configuracion, new EntornoDePrueba("Production"), NullLogger.Instance);

        await siembra.Should().NotThrowAsync();
    }
}
