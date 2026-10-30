using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using CaeManager.Web.Features.Auditoria;
using CaeManager.Web.Features.Centros;
using CaeManager.Web.Features.Comunicaciones;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Extension;
using CaeManager.Web.Features.Subcontratas;
using CaeManager.Web.Features.Visitas;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdfSharp.Fonts;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Refuerzo del aislamiento multi-Tenant (2026-10-25): cada endpoint que sirve un binario del
/// almacenamiento a partir del Id de un recurso, pedido con el Id de un recurso de OTRO Tenant, responde
/// 404 o 403 sin un solo byte y sin <c>Content-Disposition</c>, y no se distingue de un Id inexistente.
///
/// <para>
/// Composición real: <c>AddApplication</c> + <c>AddInfrastructure</c>, el tráfico como
/// <c>cae_app_runtime</c> con RLS efectiva y el <c>DiskFileStorageService</c> real (cada blob, cifrado en la
/// carpeta de su Tenant propietario). La sesión es la de un Administrador de su propio Tenant, sin Tenant
/// beneficiario seleccionado.
/// </para>
///
/// <para>
/// Diseño diferencial: cada recurso se siembra en el Tenant B con su binario. El Administrador de B pide
/// su Id y recibe los bytes (el recurso existe y el endpoint lo sirve); el Administrador de A pide el mismo
/// Id y recibe lo mismo que con un Id que no existe. Así un 404 no puede venir de una siembra rota.
/// </para>
///
/// <para>
/// Las rutas de esta lista son las de <c>EndpointsQueSirvenBinariosEstanCatalogadosTests</c>: un endpoint
/// nuevo que sirva binarios por Id hace fallar ese test de arquitectura hasta que se añade aquí. El logo del
/// Tenant lo cubre <see cref="LogoTenantEndpointBajoRlsTests"/>; <c>/mi-firma/archivo</c> no recibe Id
/// (es la firma del usuario actual).
/// </para>
/// </summary>
public class DescargasConIdDeOtroTenantBajoRuntimeTests(DescargasEntreTenantsFixture entorno)
    : IClassFixture<DescargasEntreTenantsFixture>
{
    public static TheoryData<string> Rutas() =>
    [
        "/documentos/{id:guid}/archivo",
        "/subcontratas/verificaciones/{id:guid}/evidencia",
        "/auditoria/{id:guid}/archivo-anterior",
        "/visitas/{id:guid}/paquete-documental.zip",
        "/comunicaciones/adjuntos/{id:guid}/archivo",
        "/requisitos-documentales/{id:guid}/archivo",
        "/empresas/{id:guid}/sello/archivo",
        "/extension/acreditaciones/{id:guid}/archivo",
    ];

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task El_Administrador_del_Tenant_propietario_descarga_el_binario(string ruta)
    {
        var recurso = entorno.RecursoDeB(ruta);

        var respuesta = await entorno.DescargarAsync(ruta, entorno.AdministradorB, entorno.TenantB, recurso.Id);

        respuesta.Estado.Should().Be(StatusCodes.Status200OK, $"control positivo: {ruta} sirve el recurso de B a su propio Tenant");
        if (recurso.Bytes is { } bytes)
            respuesta.Cuerpo.Should().Equal(bytes);
        else
            respuesta.Cuerpo.Take(2).Should().Equal([(byte)'P', (byte)'K'], "el paquete es un zip");
    }

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Con_el_Id_de_un_recurso_de_otro_Tenant_no_sale_ningun_byte(string ruta)
    {
        var recurso = entorno.RecursoDeB(ruta);

        var ajeno = await entorno.DescargarAsync(ruta, entorno.AdministradorA, entorno.TenantA, recurso.Id);
        var inexistente = await entorno.DescargarAsync(ruta, entorno.AdministradorA, entorno.TenantA, Guid.NewGuid());

        ajeno.Estado.Should().BeOneOf([StatusCodes.Status404NotFound, StatusCodes.Status403Forbidden],
            $"{ruta} con el Id de un recurso del Tenant B, pedido desde el Tenant A");
        ajeno.Cuerpo.Should().BeEmpty("no se sirve ningún byte del recurso ajeno");
        ajeno.Cabeceras.ContainsKey("Content-Disposition").Should().BeFalse("sin cabecera de descarga, en ninguna grafía");
        ajeno.Estado.Should().Be(inexistente.Estado, "un Id ajeno no se distingue de uno que no existe");
        ajeno.Cabeceras.Should().Equal(inexistente.Cabeceras, "ni por las cabeceras");
    }

    [Fact]
    public async Task Preparar_la_entrega_de_una_Acreditacion_de_otro_Tenant_no_emite_ticket()
    {
        var acreditacionDeB = entorno.RecursoDeB("/extension/acreditaciones/{id:guid}/archivo").Id;

        var propia = await entorno.PrepararEntregaAsync(entorno.AdministradorB, entorno.TenantB, acreditacionDeB);
        var ajena = await entorno.PrepararEntregaAsync(entorno.AdministradorA, entorno.TenantA, acreditacionDeB);
        var inexistente = await entorno.PrepararEntregaAsync(entorno.AdministradorA, entorno.TenantA, Guid.NewGuid());

        propia.Should().Be(StatusCodes.Status200OK, "control positivo: el Tenant propietario recibe el ticket");
        ajena.Should().Be(StatusCodes.Status404NotFound, "desde el Tenant A la Acreditación de B no existe");
        ajena.Should().Be(inexistente, "un Id ajeno no se distingue de uno que no existe");
    }

    [Fact]
    public async Task La_sesion_de_la_prueba_es_cae_app_runtime_con_RLS_efectiva()
    {
        var (rol, saltaRls, ve) = await entorno.SesionDelTenantAAsync();

        rol.Should().Be("cae_app_runtime", "el tráfico de la prueba va con el rol de la aplicación, no con el propietario");
        saltaRls.Should().BeFalse("cae_app_runtime no es superusuario ni tiene BYPASSRLS");
        ve.Should().BeFalse("sin el filtro de EF, la RLS del Tenant A no deja ver el documento de B");
    }
}

/// <summary>
/// La base, el contenedor y la siembra de <see cref="DescargasConIdDeOtroTenantBajoRuntimeTests"/>, una vez
/// para toda la clase: cada prueba solo lee.
/// </summary>
public sealed class DescargasEntreTenantsFixture : IAsyncLifetime
{
    private ArnesDeArranqueRuntime _base = null!;
    private ServiceProvider _servicios = null!;
    private readonly string _directorio = Path.Combine(Path.GetTempPath(), "descargas-tenants-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, Recurso> _recursosDeB = [];
    private const string PortalDeLaPrueba = "app.portal-prueba.test";
    private Guid _documentoAcreditado;

    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    public Guid AdministradorA { get; } = Guid.NewGuid();
    public Guid AdministradorB { get; } = Guid.NewGuid();

    /// <summary>El Id que recibe la ruta y los bytes que tiene que servir (null: se genera, como el zip).</summary>
    public sealed record Recurso(Guid Id, byte[]? Bytes);

    public sealed record Respuesta(int Estado, byte[] Cuerpo, Dictionary<string, string> Cabeceras);

    public Recurso RecursoDeB(string ruta) => _recursosDeB[ruta];

    public async Task InitializeAsync()
    {
        // Como Program.cs: el paquete documental puede componer PDF y PDFsharp resuelve la fuente de forma global.
        GlobalFontSettings.FontResolver ??= new CaeManager.Web.Reportes.EmbeddedFontResolver();

        // Solo para crear, migrar y, al final, eliminar la base.
        _base = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        // La misma composición que ArnesPilotoOutbound, salvo la sesión: aquí la identidad de cada
        // petición la fija la prueba (IdentidadDeLaPeticion), no el principal de un circuito.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:CaeManagerDb"] = _base.CadenaPropietario,
            ["ConnectionStrings:CaeManagerDbRuntime"] = BaseDatosPostgresDePruebas.CadenaComoRuntime(_base.CadenaPropietario),
            ["DataProtection:PermitirClavesSinCifrar"] = "true",
            ["AlmacenamientoArchivos:Ruta"] = _directorio,
        });

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddScoped<IdentidadDeLaPeticion>();
        builder.Services.AddScoped<ITenantActual>(sp => sp.GetRequiredService<IdentidadDeLaPeticion>());
        builder.Services.AddScoped<IActorAuditoria>(sp => sp.GetRequiredService<IdentidadDeLaPeticion>());
        builder.Services.AddScoped<ICurrentUserService>(sp => sp.GetRequiredService<IdentidadDeLaPeticion>().Usuario);
        builder.Services.AddScoped<IClienteActivoSeleccionado, SinTenantBeneficiarioSeleccionado>();
        builder.Services.AddScoped<AuthenticationStateProvider, PrincipalAnonimo>();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(
            builder.Services, TimeProvider.System);
        // Ningún correo sale: el último registro gana sobre SmtpEmailService.
        builder.Services.AddSingleton<IEmailService>(new ArnesPilotoOutbound.EspiaDeCorreo());

        // No se arranca el host: los servicios de fondo de Infrastructure no llegan a ejecutarse.
        _servicios = builder.Services.BuildServiceProvider();

        await SembrarEnBAsync();
    }

    public async Task DisposeAsync()
    {
        await _servicios.DisposeAsync();
        await _base.DisposeAsync();
        if (Directory.Exists(_directorio))
            Directory.Delete(_directorio, recursive: true);
    }

    /// <summary>
    /// Todo en el Tenant B, como su Administrador y bajo <c>cae_app_runtime</c>: es el sellado de producción
    /// el que pone el TenantId, y el almacén el que guarda cada blob en la carpeta de B.
    /// </summary>
    private async Task SembrarEnBAsync()
    {
        await using var ambito = AmbitoComo(AdministradorB, TenantB);
        var sp = ambito.ServiceProvider;
        var contexto = sp.GetRequiredService<CaeManagerDbContext>();
        var almacen = sp.GetRequiredService<IFileStorageService>();

        async Task<(string Clave, byte[] Bytes)> GuardarAsync(string nombre)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"%PDF-1.4 {nombre} {Guid.NewGuid():N}");
            using var flujo = new MemoryStream(bytes);
            return (await almacen.GuardarAsync(flujo, nombre), bytes);
        }

        var cliente = Empresa.CrearComoCliente("Cliente de B", "B10380186", false, null, null);
        var propia = new Empresa("Empresa propia de B", "B10380202");
        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata de B", null, "Estandar");
        contexto.Empresas.AddRange(cliente, propia, subcontrata);

        var centro = new Centro(cliente.Id, propia.Id, "Centro de B por correo");
        contexto.Centros.Add(centro);
        var canal = CanalGestionDocumental.PorEmail(centro.Id, "Gestión general", "accesos@centro-b.example", "Marta");
        canal.MarcarComoPrincipal();
        contexto.CanalesGestionDocumental.Add(canal);

        var tipo = new TipoDocumento("Póliza RC", null, false, 2, AmbitoAplicacion.Empresa,
            sensibilidad: SensibilidadDocumental.SinDatosPersonales);
        contexto.TiposDocumento.Add(tipo);

        var hoy = DiaDeNegocio.Hoy();
        var primeraVersion = await GuardarAsync("poliza-v1.pdf");
        var poliza = Documento.DeEmpresa(propia.Id, tipo.Id, hoy.AddDays(-30),
            VigenciaDocumento.VenceEl(hoy.AddDays(200)), primeraVersion.Clave);
        contexto.Documentos.Add(poliza);

        var trabajadora = Trabajador.DeEmpresa(propia.Id, "Ana", "Garcia", "12345678Z");
        contexto.Trabajadores.Add(trabajadora);

        var manana = hoy.AddDays(1);
        var visita = new Visita(centro.Id, manana, manana, null);
        contexto.Visitas.Add(visita);

        var evidencia = await GuardarAsync("evidencia.pdf");
        var verificacion = new VerificacionExternaSubcontrata(
            subcontrata.Id, centro.Id, tipo.Id, hoy, ResultadoVerificacionExterna.Valido, AdministradorB);
        verificacion.AdjuntarEvidencia(evidencia.Clave, "evidencia.pdf");
        contexto.VerificacionesExternaSubcontrata.Add(verificacion);

        var adjunto = await GuardarAsync("adjunto.pdf");
        var conversacion = new Conversacion("Documentación de B", clienteId: cliente.Id);
        var adjuntoMensaje = conversacion
            .AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contratista@b.example", "<p>Adjunto la póliza.</p>")
            .AgregarAdjunto("adjunto.pdf", "application/pdf", adjunto.Bytes.LongLength, adjunto.Clave);
        contexto.Conversaciones.Add(conversacion);

        var plantilla = await GuardarAsync("formulario.pdf");
        var requisito = new TipoDocumentoCentro(tipo.Id, centro.Id, archivoUrl: plantilla.Clave, nombreArchivoOriginal: "formulario.pdf");
        contexto.TiposDocumentoCentros.Add(requisito);

        // La Acreditación pendiente que recoge la extensión: la póliza, por un canal de Plataforma del mismo Centro.
        var proveedor = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma de prueba");
        contexto.ProveedoresPlataformaCae.Add(proveedor);
        contexto.DominiosProveedorPlataformaCae.Add(new DominioProveedorPlataformaCae(proveedor.Id, "portal-prueba.test"));
        var canalPlataforma = CanalGestionDocumental.DePlataforma(centro.Id, "Portal principal", proveedor.Id, null, null, null);
        contexto.CanalesGestionDocumental.Add(canalPlataforma);
        var acreditacion = new AcreditacionDocumentoPlataforma(poliza.Id, canalPlataforma.Id);
        contexto.AcreditacionesDocumentoPlataforma.Add(acreditacion);

        var sello = await GuardarAsync("sello.png");
        contexto.SellosEmpresa.Add(new SelloEmpresa(propia.Id, sello.Clave, DateTime.UtcNow));
        await contexto.SaveChangesAsync();

        contexto.VisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, trabajadora.Id));

        // Sustituir el archivo deja en la auditoría la versión anterior (DatosAntes.ArchivoUrl), que es lo
        // que sirve /auditoria/{id}/archivo-anterior.
        var segundaVersion = await GuardarAsync("poliza-v2.pdf");
        poliza.AdjuntarArchivo(segundaVersion.Clave);
        await contexto.SaveChangesAsync();

        var registroAnterior = await contexto.RegistrosAuditoria.AsNoTracking()
            .Where(r => r.EntidadTipo == "Documento" && r.EntidadId == poliza.Id && r.DatosAntes != null)
            .OrderByDescending(r => r.FechaUtc)
            .Select(r => r.Id)
            .FirstAsync();

        _recursosDeB["/documentos/{id:guid}/archivo"] = new(poliza.Id, segundaVersion.Bytes);
        _recursosDeB["/subcontratas/verificaciones/{id:guid}/evidencia"] = new(verificacion.Id, evidencia.Bytes);
        _recursosDeB["/auditoria/{id:guid}/archivo-anterior"] = new(registroAnterior, primeraVersion.Bytes);
        _recursosDeB["/visitas/{id:guid}/paquete-documental.zip"] = new(visita.Id, null);
        _recursosDeB["/comunicaciones/adjuntos/{id:guid}/archivo"] = new(adjuntoMensaje.Id, adjunto.Bytes);
        _recursosDeB["/requisitos-documentales/{id:guid}/archivo"] = new(requisito.Id, plantilla.Bytes);
        _recursosDeB["/empresas/{id:guid}/sello/archivo"] = new(propia.Id, sello.Bytes);
        _recursosDeB["/extension/acreditaciones/{id:guid}/archivo"] = new(acreditacion.Id, segundaVersion.Bytes);
        _documentoAcreditado = poliza.Id;
    }

    /// <summary>
    /// Una petición de <paramref name="usuario"/>, Administrador de <paramref name="tenant"/>. Los manejadores
    /// con nombre se llaman como los llama el enrutado; los de Comunicaciones y Requisitos documentales se
    /// ejecutan desde el <c>Map*</c> de producción con <c>RequestDelegateFactory</c>, con el enlace de parámetros real.
    /// </summary>
    public async Task<Respuesta> DescargarAsync(string ruta, Guid usuario, Guid tenant, Guid id)
    {
        await using var ambito = AmbitoComo(usuario, tenant);
        var sp = ambito.ServiceProvider;
        var contexto = new DefaultHttpContext { RequestServices = sp };
        contexto.Request.Method = HttpMethods.Get;
        contexto.Response.Body = new MemoryStream();
        var ct = CancellationToken.None;

        IMediator Mediador() => sp.GetRequiredService<IMediator>();
        IFileStorageService Almacen() => sp.GetRequiredService<IFileStorageService>();
        IRegistroAccesoDocumentoSensibleService Registro() => sp.GetRequiredService<IRegistroAccesoDocumentoSensibleService>();
        ILoggerFactory Registros() => sp.GetRequiredService<ILoggerFactory>();

        // Un ticket auténtico, firmado para quien pide y para ese Id: el peor caso. Lo que tiene que pararlo es la
        // lectura bajo el Tenant de la petición, no la firma.
        Task<IResult> ServirEntregaAsync()
        {
            var proteccion = sp.GetRequiredService<IDataProtectionProvider>();
            contexto.Request.Headers[EntregaArchivoExtensionEndpoints.CabeceraTicket] =
                TicketEntregaExtension.Proteger(proteccion, usuario, id, _documentoAcreditado, PortalDeLaPrueba);
            return EntregaArchivoExtensionEndpoints.ServirAsync(
                id, contexto, Mediador(), sp.GetRequiredService<ICurrentUserService>(), proteccion, Almacen(), Registro(), Registros(), ct);
        }

        IResult? resultado = ruta switch
        {
            "/documentos/{id:guid}/archivo" =>
                await DocumentosEndpoints.ServirArchivoAsync(id, contexto, Mediador(), Almacen(), Registro(), Registros(), ct),
            "/subcontratas/verificaciones/{id:guid}/evidencia" =>
                await SubcontratasEndpoints.ServirEvidenciaAsync(id, contexto, Mediador(), Almacen(), Registro(), Registros(), ct),
            "/auditoria/{id:guid}/archivo-anterior" =>
                await AuditoriaEndpoints.ServirArchivoAnteriorAsync(id, contexto, Mediador(), Almacen(), Registro(), Registros(), sp.GetRequiredService<IAlcanceDatosService>(), ct),
            "/visitas/{id:guid}/paquete-documental.zip" =>
                await VisitasEndpoints.ServirPaqueteAsync(id, contexto, Mediador(), Registros(), ct),
            "/empresas/{id:guid}/sello/archivo" =>
                await FirmasGuardadasEndpoints.ServirSelloAsync(id, contexto, Mediador(), Almacen(), Registros(), ct),
            "/extension/acreditaciones/{id:guid}/archivo" =>
                await ServirEntregaAsync(),
            "/comunicaciones/adjuntos/{id:guid}/archivo" or "/requisitos-documentales/{id:guid}/archivo" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(ruta), ruta, "Ruta sin invocación en la prueba."),
        };

        if (resultado is not null)
        {
            await resultado.ExecuteAsync(contexto);
        }
        else
        {
            var rutas = new RutasDePrueba(_servicios);
            if (ruta.StartsWith("/comunicaciones", StringComparison.Ordinal))
                rutas.MapComunicacionesEndpoints();
            else
                rutas.MapRequisitosDocumentalesEndpoints();

            var endpoint = rutas.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
                .Single(e => e.RoutePattern.RawText == ruta);
            contexto.Request.RouteValues["id"] = id.ToString();
            await endpoint.RequestDelegate!(contexto);
        }

        return new Respuesta(
            contexto.Response.StatusCode,
            ((MemoryStream)contexto.Response.Body).ToArray(),
            contexto.Response.Headers
                .Where(c => !string.Equals(c.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(c => c.Key, c => c.Value.ToString(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>El estado de <c>POST /extension/acreditaciones/{id}/entrega</c> desde el portal de la prueba.</summary>
    public async Task<int> PrepararEntregaAsync(Guid usuario, Guid tenant, Guid id)
    {
        await using var ambito = AmbitoComo(usuario, tenant);
        var sp = ambito.ServiceProvider;
        var contexto = new DefaultHttpContext { RequestServices = sp };
        contexto.Request.Method = HttpMethods.Post;
        contexto.Request.ContentType = "application/json";
        contexto.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"{{\"dominioPortal\":\"{PortalDeLaPrueba}\"}}"));
        contexto.Response.Body = new MemoryStream();

        var resultado = await EntregaArchivoExtensionEndpoints.PrepararAsync(
            id, contexto.Request, sp.GetRequiredService<IMediator>(), sp.GetRequiredService<ICurrentUserService>(),
            sp.GetRequiredService<IDataProtectionProvider>(), CancellationToken.None);
        await resultado.ExecuteAsync(contexto);
        return contexto.Response.StatusCode;
    }

    /// <summary>
    /// Lo que ve la sesión del Tenant A en la base: el rol de la conexión, si salta la RLS y si, sin el filtro
    /// de EF, alcanza el documento de B. Sostiene que los 404 de la prueba vienen también de la RLS.
    /// </summary>
    public async Task<(string Rol, bool SaltaRls, bool VeElDocumentoDeBSinFiltro)> SesionDelTenantAAsync()
    {
        await using var ambito = AmbitoComo(AdministradorA, TenantA);
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var rol = await contexto.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
        var saltaRls = await contexto.Database
            .SqlQueryRaw<bool>("SELECT (rolsuper OR rolbypassrls) AS \"Value\" FROM pg_roles WHERE rolname = current_user")
            .SingleAsync();
        var documentoDeB = RecursoDeB("/documentos/{id:guid}/archivo").Id;
        var ve = await contexto.Documentos.IgnoreQueryFilters().AnyAsync(d => d.Id == documentoDeB);
        return (rol, saltaRls, ve);
    }

    private AsyncServiceScope AmbitoComo(Guid usuario, Guid tenant)
    {
        var ambito = _servicios.CreateAsyncScope();
        ambito.ServiceProvider.GetRequiredService<IdentidadDeLaPeticion>().Como(usuario, tenant);
        return ambito;
    }

    /// <summary>
    /// La sesión de la petición: un Administrador en su propio Tenant. Hace de <see cref="ITenantActual"/>
    /// (el ámbito explícito manda, como en <c>TenantActual</c>), de actor de auditoría y de usuario actual.
    /// </summary>
    internal sealed class IdentidadDeLaPeticion : ITenantActual, IActorAuditoria
    {
        private Guid? _usuario;
        private Guid? _tenant;

        public CurrentUserServiceFalso Usuario { get; private set; } = new();

        public void Como(Guid usuario, Guid tenant)
        {
            _usuario = usuario;
            _tenant = tenant;
            Usuario = new CurrentUserServiceFalso(usuario, Roles.Administrador, tenantOrigenId: tenant);
        }

        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? _tenant;

        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(_usuario));

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(_usuario);
    }

    private sealed class SinTenantBeneficiarioSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class PrincipalAnonimo : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>Lo mínimo para que el <c>Map*</c> de producción registre sus endpoints fuera de un host.</summary>
    private sealed class RutasDePrueba(IServiceProvider servicios) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => servicios;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(servicios);
    }
}
