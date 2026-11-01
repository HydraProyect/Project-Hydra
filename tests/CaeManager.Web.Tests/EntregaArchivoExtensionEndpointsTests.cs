using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.PrepararEntregaArchivoExtension;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Extension;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las dos rutas de la entrega del archivo a la extensión, sobre el enrutado y la autorización reales de sus <c>Map*</c>:
/// la política solo de token, el límite de ritmo y el ticket ligado a usuario y Acreditación. El alcance y el aislamiento
/// entre Tenants bajo RLS están en <c>DescargasConIdDeOtroTenantBajoRuntimeTests</c>.
/// </summary>
public class EntregaArchivoExtensionEndpointsTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Acreditacion = Guid.NewGuid();
    private static readonly Guid Documento = Guid.NewGuid();
    private static readonly string RutaEntrega = $"/extension/acreditaciones/{Acreditacion}/entrega";
    private static readonly string RutaArchivo = $"/extension/acreditaciones/{Acreditacion}/archivo";

    private static readonly byte[] Pdf = Encoding.UTF8.GetBytes("%PDF-prueba");

    private readonly EphemeralDataProtectionProvider _proteccion = new();

    public static TheoryData<string, HttpStatusCode> FallosDeLaComprobacion => new()
    {
        { "Acreditacion.NoEncontrada", HttpStatusCode.NotFound },
        { ConfirmarSubidaDesdeExtensionCommandHandler.CodigoConectorInactivo, HttpStatusCode.Conflict },
        { ConfirmarSubidaDesdeExtensionCommandHandler.CodigoPortalAjeno, HttpStatusCode.Conflict },
        { ConfirmarSubidaDesdeExtensionCommandHandler.CodigoPortalNoDeclarado, HttpStatusCode.BadRequest },
    };

    [Theory]
    [MemberData(nameof(FallosDeLaComprobacion))]
    public async Task Sin_autorizacion_del_servidor_no_hay_ticket_y_se_explica_el_motivo(string codigo, HttpStatusCode esperado)
    {
        var mediador = new Mediador(Error.Crear(codigo, "Detalle para el Gestor CAE"));
        await using var app = await ArrancarAsync(mediador);
        using var cliente = Cliente(app);

        using var respuesta = await PedirEntregaAsync(cliente);

        respuesta.StatusCode.Should().Be(esperado);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        cuerpo.Should().Contain("Detalle para el Gestor CAE").And.NotContain("ticket");
    }

    [Fact]
    public async Task La_entrega_comprobada_emite_un_ticket_ligado_al_usuario_la_Acreditacion_el_Documento_y_el_host()
    {
        var mediador = new Mediador(null);
        await using var app = await ArrancarAsync(mediador);
        using var cliente = Cliente(app);

        using var respuesta = await PedirEntregaAsync(cliente);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        TicketEntregaExtension.Leer(_proteccion, json.RootElement.GetProperty("ticket").GetString())
            .Should().Be(new CargaUtilTicketEntrega(Usuario, Tenant, null, Acreditacion, Documento, "app.dokify.net"));
        mediador.Preparaciones.Should().ContainSingle().Which.DominioPortal.Should().Be("app.dokify.net");
    }

    [Fact]
    public async Task El_archivo_con_ticket_valido_se_sirve_y_queda_registrado_como_entrega_a_la_extension()
    {
        var mediador = new Mediador(null);
        var registro = new RegistroEspia();
        await using var app = await ArrancarAsync(mediador, registro);
        using var cliente = Cliente(app);

        using var respuesta = await PedirArchivoAsync(cliente, TicketDe(Usuario, Acreditacion));

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        (await respuesta.Content.ReadAsByteArrayAsync()).Should().Equal(Pdf);
        registro.Accesos.Should().ContainSingle().Which.Should().Be((Documento, TipoAccesoDocumentoSensible.EntregaExtension));
        mediador.Preparaciones.Should().ContainSingle("el ticket no es una foto: se vuelve a comprobar al servir")
            .Which.DominioPortal.Should().Be("app.dokify.net");
    }

    [Fact]
    public async Task Si_el_registro_de_acceso_falla_no_sale_el_archivo()
    {
        await using var app = await ArrancarAsync(new Mediador(null), new RegistroEspia { Rechazar = true });
        using var cliente = Cliente(app);

        using var respuesta = await PedirArchivoAsync(cliente, TicketDe(Usuario, Acreditacion));

        respuesta.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await respuesta.Content.ReadAsByteArrayAsync()).Should().NotEqual(Pdf);
    }

    [Fact]
    public async Task Sin_ticket_o_con_uno_ajeno_responde_el_mismo_404_sin_llegar_a_la_comprobacion()
    {
        var mediador = new Mediador(null);
        var registro = new RegistroEspia();
        await using var app = await ArrancarAsync(mediador, registro);
        using var cliente = Cliente(app);

        foreach (var ticket in new string?[]
        {
            null, "no-es-un-ticket", TicketDe(Guid.NewGuid(), Acreditacion), TicketDe(Usuario, Guid.NewGuid()),
        })
        {
            using var respuesta = await PedirArchivoAsync(cliente, ticket);

            respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await respuesta.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        }

        mediador.Preparaciones.Should().BeEmpty();
        registro.Accesos.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Acreditacion.NoEncontrada", HttpStatusCode.NotFound)]
    [InlineData(ConfirmarSubidaDesdeExtensionCommandHandler.CodigoConectorInactivo, HttpStatusCode.Conflict)]
    public async Task Con_ticket_valido_lo_que_cambio_despues_de_emitirlo_tambien_para_el_archivo(string codigo, HttpStatusCode esperado)
    {
        var registro = new RegistroEspia();
        await using var app = await ArrancarAsync(new Mediador(Error.Crear(codigo, "Detalle")), registro);
        using var cliente = Cliente(app);

        using var respuesta = await PedirArchivoAsync(cliente, TicketDe(Usuario, Acreditacion));

        respuesta.StatusCode.Should().Be(esperado);
        (await respuesta.Content.ReadAsByteArrayAsync()).Should().NotEqual(Pdf);
        registro.Accesos.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_la_Acreditacion_ya_apunta_a_otro_Documento_el_ticket_no_vale()
    {
        var registro = new RegistroEspia();
        await using var app = await ArrancarAsync(new Mediador(null) { DocumentoVigente = Guid.NewGuid() }, registro);
        using var cliente = Cliente(app);

        using var respuesta = await PedirArchivoAsync(cliente, TicketDe(Usuario, Acreditacion));

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        registro.Accesos.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_token_de_extension_ninguna_ruta_responde()
    {
        var mediador = new Mediador(null);
        await using var app = await ArrancarAsync(mediador);
        using var cliente = Cliente(app, autenticado: false);

        using var entrega = await PedirEntregaAsync(cliente);
        using var archivo = await PedirArchivoAsync(cliente, TicketDe(Usuario, Acreditacion));

        entrega.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        archivo.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        mediador.Preparaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Superado_el_cupo_responde_429_sin_llegar_a_la_comprobacion()
    {
        var mediador = new Mediador(null);
        await using var app = await ArrancarAsync(mediador, limitePorMinuto: 2);
        using var cliente = Cliente(app);

        using var primera = await PedirEntregaAsync(cliente);
        using var segunda = await PedirArchivoAsync(cliente, TicketDe(Usuario, Acreditacion));
        using var tercera = await PedirEntregaAsync(cliente);

        primera.StatusCode.Should().Be(HttpStatusCode.OK);
        segunda.StatusCode.Should().Be(HttpStatusCode.OK, "la entrega y el archivo gastan el mismo cupo");
        tercera.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        mediador.Preparaciones.Should().HaveCount(2);
    }

    [Fact]
    public async Task Otro_token_de_la_misma_sesion_no_da_cupo_y_uno_de_otra_sesion_conserva_el_suyo()
    {
        var sesion = Guid.NewGuid();
        await using var app = await ArrancarAsync(new Mediador(null), limitePorMinuto: 1);
        using var revocado = Cliente(app, sesion: sesion, sello: "SELLO-ANTIGUO");
        using var mismaSesion = Cliente(app, sesion: sesion);
        using var otraSesion = Cliente(app);

        using var primera = await PedirEntregaAsync(revocado);
        using var segunda = await PedirEntregaAsync(mismaSesion);
        using var tercera = await PedirEntregaAsync(otraSesion);

        primera.StatusCode.Should().Be(HttpStatusCode.OK);
        segunda.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "emitir otro token en la misma sesión no da cupo nuevo");
        tercera.StatusCode.Should().Be(HttpStatusCode.OK, "agotar el cupo con un token viejo no bloquea la sesión viva del usuario");
    }

    private string TicketDe(Guid usuario, Guid acreditacion) =>
        TicketEntregaExtension.Proteger(_proteccion, usuario, Tenant, null, acreditacion, Documento, "app.dokify.net");

    [Fact]
    public async Task Un_ticket_de_otro_Tenant_o_de_otra_Asignacion_de_Operacion_no_recoge_nada()
    {
        var registro = new RegistroEspia();
        await using var app = await ArrancarAsync(new Mediador(null), registro);
        using var cliente = Cliente(app);

        foreach (var ticket in new[]
                 {
                     TicketEntregaExtension.Proteger(_proteccion, Usuario, Guid.NewGuid(), null, Acreditacion, Documento, "app.dokify.net"),
                     TicketEntregaExtension.Proteger(_proteccion, Usuario, Tenant, Guid.NewGuid(), Acreditacion, Documento, "app.dokify.net"),
                 })
        {
            using var respuesta = await PedirArchivoAsync(cliente, ticket);
            respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    private static Task<HttpResponseMessage> PedirEntregaAsync(HttpClient cliente) => cliente.PostAsync(RutaEntrega,
        new StringContent("{\"dominioPortal\":\"app.dokify.net\"}", Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> PedirArchivoAsync(HttpClient cliente, string? ticket)
    {
        var peticion = new HttpRequestMessage(HttpMethod.Get, RutaArchivo);
        if (ticket is not null)
            peticion.Headers.Add(EntregaArchivoExtensionEndpoints.CabeceraTicket, ticket);
        return cliente.SendAsync(peticion);
    }

    private HttpClient Cliente(WebApplication app, bool autenticado = true, Guid? sesion = null, string sello = "SELLO")
    {
        var cliente = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
        };
        if (autenticado)
            cliente.DefaultRequestHeaders.Add("Authorization",
                "Extension " + TokenSesionExtension.Proteger(_proteccion, Usuario, sello, sesion ?? Guid.NewGuid()));
        return cliente;
    }

    private async Task<WebApplication> ArrancarAsync(IMediator mediador, RegistroEspia? registro = null, int limitePorMinuto = 100)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddProblemDetails();
        builder.Services.AddAuthentication(ExtensionAuthenticationSchemeOptions.NombreEsquema)
            .AddScheme<AuthenticationSchemeOptions, ExtensionFalsa>(ExtensionAuthenticationSchemeOptions.NombreEsquema, _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy(Policies.SoloExtension, p => p
            .AddAuthenticationSchemes(ExtensionAuthenticationSchemeOptions.NombreEsquema)
            .RequireAuthenticatedUser()));
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(LimitadorEntregaExtension.Politica, contexto =>
                LimitadorEntregaExtension.ParticionPorUsuario(contexto, limitePorMinuto, _proteccion));
        });
        builder.Services.AddSingleton(mediador);
        builder.Services.AddSingleton<IDataProtectionProvider>(_proteccion);
        builder.Services.AddSingleton(DispatchProxy.Create<ICurrentUserService, UsuarioDelToken>());
        builder.Services.AddSingleton<ITenantActual>(new TenantDeLaPrueba());
        builder.Services.AddSingleton<IClienteActivoSeleccionado>(new SinSeleccion());
        builder.Services.AddSingleton<IFileStorageService>(new AlmacenFijo());
        builder.Services.AddSingleton<IRegistroAccesoDocumentoSensibleService>(registro ?? new RegistroEspia());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapEntregaArchivoExtensionEndpoints();
        await app.StartAsync();
        return app;
    }

    private static DocumentoDetalleDto DetalleDelDocumento() => new(
        Id: Documento,
        Ambito: AmbitoAplicacion.Trabajador,
        PropietarioNombre: "Ana Ruiz",
        TipoDocumentoNombre: "Formación PRL",
        TipoDocumentoAplicaVencimientoAutomatico: true,
        FechaEmision: new DateOnly(2026, 1, 10),
        FechaVencimiento: new DateOnly(2027, 1, 10),
        EstadoVigencia: EstadoVigenciaDocumento.VenceEnFecha,
        ArchivoUrl: "clave/del-fichero.pdf",
        Comentarios: null,
        TipoDocumentoDescripcion: null,
        TipoDocumentoCriteriosValidacion: null,
        TipoDocumentoSeSolicitaA: null,
        TipoDocumentoObservaciones: null,
        Version: Guid.NewGuid(),
        TipoDocumentoPerfilDocumentoOficial: PerfilDocumentoOficial.Ninguno,
        EmpresaId: null,
        NombreArchivoDescarga: "formacion-prl.pdf");

    private sealed class ExtensionFalsa(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.Authorization.ToString().StartsWith("Extension ", StringComparison.Ordinal)
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Usuario.ToString())], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    /// <summary>Los endpoints solo preguntan quién es el usuario: cualquier otra pregunta haría fallar la prueba.</summary>
    public class UsuarioDelToken : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(ICurrentUserService.ObtenerUsuarioActualIdAsync)
                ? Task.FromResult<Guid?>(Usuario)
                : throw new NotSupportedException(targetMethod.Name);
    }

    private sealed class AlmacenFijo : IFileStorageService
    {
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Pdf));

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TenantDeLaPrueba : ITenantActual
    {
        public Guid? TenantId => Tenant;
    }

    private sealed class SinSeleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class RegistroEspia : IRegistroAccesoDocumentoSensibleService
    {
        public bool Rechazar { get; init; }
        public List<(Guid, TipoAccesoDocumentoSensible)> Accesos { get; } = [];

        public Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
        {
            if (Rechazar) throw new AccesoDocumentoSensibleNoRegistradoException(documentoId);
            Accesos.Add((documentoId, tipoAcceso));
            return Task.CompletedTask;
        }

        public Task RegistrarSiSensibleAsync(
            Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default) =>
            RegistrarSiSensibleAsync(recursoId, tipoAcceso, cancellationToken);
    }

    private sealed class Mediador(Error? error) : IMediator
    {
        public Guid DocumentoVigente { get; init; } = Documento;
        public List<PrepararEntregaArchivoExtensionQuery> Preparaciones { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? respuesta = request switch
            {
                PrepararEntregaArchivoExtensionQuery consulta => Preparar(consulta),
                ObtenerDocumentoPorIdQuery => DetalleDelDocumento(),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        private Result<EntregaArchivoExtensionDto> Preparar(PrepararEntregaArchivoExtensionQuery consulta)
        {
            Preparaciones.Add(consulta);
            return error is null
                ? Result.Exito(new EntregaArchivoExtensionDto(DocumentoVigente, consulta.DominioPortal!))
                : Result.Fallo<EntregaArchivoExtensionDto>(error);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }
}
