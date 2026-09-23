using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.CrearNotaInternaConversacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerNotasInternasConversacion;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Notas internas del Unified Timeline (MVP de mensajería interna, decisiones
/// D0/D1/D3). Prueba en Application sobre PostgreSQL real, con el filtro de
/// tenant de EF activo:
/// <list type="bullet">
/// <item>la matriz de lectura por rol (solo los cuatro roles operativos);</item>
/// <item>que ninguna sesión privilegiada de plataforma —Soporte TALVEG
/// incluido— las lee, aunque su rol de negocio fuera de los que sí;</item>
/// <item>el alcance: fuera de cartera no se lee ni se escribe;</item>
/// <item>el aislamiento entre Tenants, en lectura y en escritura;</item>
/// <item>que crear una nota no produce ningún Mensaje (nada sale del equipo).</item>
/// </list>
/// El enforcement en la capa de datos (RLS y la revocación a
/// <c>cae_app_soporte</c>) lo prueban <c>NotasInternasRlsPostgresTests</c> y
/// los trinquetes de cobertura RLS; esto no les presta evidencia.
/// </summary>
public class NotasInternasConversacionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _clienteEnCartera = Guid.NewGuid();
    private readonly Guid _autorId = Guid.NewGuid();

    private Guid _conversacionA;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantA);
        await contexto.Database.MigrateAsync();

        var conversacion = new Conversacion("Hilo con nota", clienteId: _clienteEnCartera);
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();
        _conversacionA = conversacion.Id;

        contexto.NotasInternasConversacion.Add(
            new NotaInternaConversacion(_conversacionA, _autorId, "El contacto pide que llamemos antes de las 10.", DateTime.UtcNow));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // --- Lectura: matriz por rol --------------------------------------------

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Un_rol_operativo_con_el_hilo_en_alcance_lee_la_nota(string rol)
    {
        var notas = await LeerAsync(_tenantA, new CurrentUserServiceFalso(Guid.NewGuid(), rol), SinSesion());

        notas.Should().ContainSingle().Which.Should().Match<NotaInternaDetalleDto>(n =>
            n.AutorUsuarioId == _autorId && n.Texto == "El contacto pide que llamemos antes de las 10.");
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData("RolInventado")]
    [InlineData(null)]
    public async Task Un_rol_no_operativo_no_lee_la_nota_aunque_el_hilo_este_en_alcance(string? rol)
    {
        var notas = await LeerAsync(_tenantA, new CurrentUserServiceFalso(Guid.NewGuid(), rol), SinSesion());

        notas.Should().BeEmpty();
    }

    // --- Lectura: plano de plataforma (D3-Soporte) --------------------------

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento)]
    public async Task Ninguna_sesion_privilegiada_lee_la_nota_ni_con_rol_de_Administrador(CapacidadPrivilegio capacidad)
    {
        var sesion = new SesionFalsa(new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), _tenantA, capacidad, null));

        var notas = await LeerAsync(_tenantA, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"), sesion);

        notas.Should().BeEmpty();
    }

    // --- Lectura: alcance y aislamiento -------------------------------------

    [Fact]
    public async Task Un_Gestor_CAE_sin_el_hilo_en_su_cartera_no_lee_la_nota()
    {
        var notas = await LeerAsync(_tenantA, new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae"), SinSesion(),
            new AlcanceDatosServiceFalso(clienteIds: [Guid.NewGuid()]));

        notas.Should().BeEmpty();
    }

    [Fact]
    public async Task Desde_otro_Tenant_no_se_lee_la_nota_ni_con_acceso_total()
    {
        var notas = await LeerAsync(_tenantB, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"), SinSesion());

        notas.Should().BeEmpty();
    }

    // --- Escritura -----------------------------------------------------------

    [Fact]
    public async Task Crear_una_nota_la_guarda_con_su_autor_y_no_produce_ningun_mensaje()
    {
        var autor = Guid.NewGuid();
        DateTime ultimoMensajeAntes;
        await using (var previo = CrearContexto(_tenantA))
            ultimoMensajeAntes = (await previo.Conversaciones.SingleAsync(c => c.Id == _conversacionA)).FechaUltimoMensajeUtc;

        var resultado = await CrearAsync(_tenantA, new CurrentUserServiceFalso(autor, "GestorCae"), "  Ya lo he llamado.  ");

        resultado.EsExitoso.Should().BeTrue();
        await using var lectura = CrearContexto(_tenantA);
        var nota = await lectura.NotasInternasConversacion.SingleAsync(n => n.Id == resultado.Valor);
        nota.AutorUsuarioId.Should().Be(autor);
        nota.Texto.Should().Be("Ya lo he llamado.");
        nota.ConversacionId.Should().Be(_conversacionA);

        (await lectura.Mensajes.CountAsync(m => m.ConversacionId == _conversacionA)).Should().Be(0,
            "una nota interna nunca se convierte en un mensaje del hilo: no sale del equipo");
        (await lectura.Conversaciones.SingleAsync(c => c.Id == _conversacionA)).FechaUltimoMensajeUtc
            .Should().Be(ultimoMensajeAntes, "la nota no mueve la actividad visible del hilo hacia el contacto");
    }

    [Fact]
    public async Task No_se_crea_una_nota_en_un_hilo_fuera_de_cartera()
    {
        var resultado = await CrearAsync(_tenantA, new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae"), "Nota",
            new AlcanceDatosServiceFalso(clienteIds: [Guid.NewGuid()]));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.NoEncontrada");
        await using var lectura = CrearContexto(_tenantA);
        (await lectura.NotasInternasConversacion.CountAsync()).Should().Be(1, "solo la nota sembrada");
    }

    [Fact]
    public async Task No_se_crea_una_nota_en_un_hilo_de_otro_Tenant()
    {
        var resultado = await CrearAsync(_tenantB, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"), "Nota");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.NoEncontrada");
        await using var lecturaB = CrearContexto(_tenantB);
        (await lecturaB.NotasInternasConversacion.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Sin_usuario_identificado_no_se_crea_la_nota()
    {
        var resultado = await CrearAsync(_tenantA, new CurrentUserServiceFalso(usuarioId: null, rol: "GestorCae"), "Nota");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("NotaInterna.SinAutor");
    }

    // --- Arnés ---------------------------------------------------------------

    private async Task<IReadOnlyList<NotaInternaDetalleDto>> LeerAsync(
        Guid tenant, ICurrentUserService usuario, ISesionPrivilegiadaActual sesion, IAlcanceDatosService? alcance = null)
    {
        await using var contexto = CrearContexto(tenant);
        var handler = new ObtenerNotasInternasConversacionQueryHandler(
            contexto, alcance ?? new AlcanceDatosServiceFalso(), usuario, sesion);
        return await handler.Handle(new ObtenerNotasInternasConversacionQuery(_conversacionA), CancellationToken.None);
    }

    private async Task<CaeManager.Domain.Common.Result<Guid>> CrearAsync(
        Guid tenant, ICurrentUserService usuario, string texto, IAlcanceDatosService? alcance = null)
    {
        await using var contexto = CrearContexto(tenant);
        var handler = new CrearNotaInternaConversacionCommandHandler(
            contexto, new NotaInternaConversacionRepository(contexto), alcance ?? new AlcanceDatosServiceFalso(), usuario, contexto);
        return await handler.Handle(new CrearNotaInternaConversacionCommand(_conversacionA, texto), CancellationToken.None);
    }

    private static ISesionPrivilegiadaActual SinSesion() => new SesionFalsa(null);

    private sealed class SesionFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }

    private CaeManagerDbContext CrearContexto(Guid tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
