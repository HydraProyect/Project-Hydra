using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Empresas;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Tenants;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Alcance de cartera al CREAR un Documento. Existir en el Tenant no basta: un
/// Gestor CAE solo crea Documentos de propietarios dentro de su Asignación de
/// Cartera, con el mismo criterio (<see cref="DocumentoAlcanceExtensions.DocumentoVisibleAsync"/>)
/// que Renovar/Eliminar/MarcarAcreditacion* aplican al Documento ya existente.
///
/// Composición real: <c>AddApplication()</c> (pipeline completo, incluido
/// <c>AutorizacionEscrituraBehavior</c>) y <see cref="AlcanceDatosService"/> real
/// sobre PostgreSQL, sin fakes de alcance — la cartera sale de una
/// <see cref="AsignacionCartera"/> de verdad.
///
/// Escenario, Tenant propietario A:
/// <list type="bullet">
/// <item>Cliente empresarial A (en la cartera del Gestor CAE) con un Centro donde trabaja la
/// Empresa propia; un Trabajador de la Empresa propia asignado a ese Centro; un Vehículo de la
/// Empresa propia; un Proyecto del Cliente empresarial A.</item>
/// <item>Cliente empresarial B (fuera de la cartera) con un Centro donde trabaja una Subcontrata
/// (no propia, sin Relación Empresarial con A); un Trabajador y un Vehículo de esa Subcontrata;
/// un Proyecto del Cliente empresarial B.</item>
/// </list>
/// El propietario fuera de cartera nunca es la Empresa propia ni de su plantilla: la Empresa
/// propia es estructuralmente visible para cualquier Gestor CAE con cartera en el Tenant (D-8),
/// así que no serviría como negativo.
/// </summary>
public class CrearDocumentoCommandAlcanceCarteraTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    private Escenario _a = null!;
    private Escenario _b = null!;
    private Guid _gestorA;

    private sealed record Escenario(
        Guid ClienteDentro, Guid Propia, Guid TrabajadorDentro, Guid VehiculoDentro, Guid ProyectoDentro,
        Guid ClienteFuera, Guid SubcontrataFuera, Guid TrabajadorFuera, Guid VehiculoFuera, Guid ProyectoFuera,
        IReadOnlyDictionary<AmbitoAplicacion, Guid> Tipos);

    public async Task InitializeAsync()
    {
        await using (var contexto = CrearContexto(_tenant))
            await contexto.Database.MigrateAsync();

        _a = await SembrarAsync(_tenant, "A", ["B10380186", "B10380194", "B10380202"]);
        _b = await SembrarAsync(_otroTenant, "B", ["B10380210", "B10380228", "B10380236"]);
        _gestorA = await OtorgarCarteraAsync(_tenant, _a.ClienteDentro);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    public static TheoryData<AmbitoAplicacion> Ambitos() =>
    [
        AmbitoAplicacion.Trabajador, AmbitoAplicacion.Cliente, AmbitoAplicacion.Empresa,
        AmbitoAplicacion.Vehiculo, AmbitoAplicacion.Proyecto
    ];

    /// <summary>
    /// Control positivo: la misma composición, el mismo Gestor CAE y el mismo tipo de propietario
    /// SÍ crean el Documento cuando el propietario está en su cartera. Sin él, los negativos
    /// podrían estar en verde por una dependencia mal resuelta o un escenario que nunca llega al
    /// handler.
    /// </summary>
    [Theory]
    [MemberData(nameof(Ambitos))]
    public async Task Gestor_CAE_crea_el_Documento_de_un_propietario_dentro_de_su_cartera(AmbitoAplicacion ambito)
    {
        var propietario = PropietarioDentro(_a, ambito);

        var resultado = await EnviarAsync(_tenant, _gestorA, "GestorCae", Command(_a, ambito, propietario));

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        (await ContarDocumentosAsync(_tenant, ambito, propietario)).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Ambitos))]
    public async Task Gestor_CAE_no_crea_el_Documento_de_un_propietario_fuera_de_su_cartera(AmbitoAplicacion ambito)
    {
        var propietario = PropietarioFuera(_a, ambito);

        var resultado = await EnviarAsync(_tenant, _gestorA, "GestorCae", Command(_a, ambito, propietario));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.PropietarioNoEncontrado",
            "fuera de cartera se responde igual que si no existiera, sin revelar qué hay fuera del alcance");
        (await ContarDocumentosAsync(_tenant, ambito, propietario)).Should().Be(0,
            "el propietario existe en el Tenant, pero no está en la Asignación de Cartera del Gestor CAE");
    }

    /// <summary>
    /// Pre-incorporación (D-8, #797): la plantilla de la Empresa propia es visible por cartera
    /// aunque el Trabajador todavía no tenga ninguna Asignación, así que el Gestor CAE puede
    /// subirle documentación antes de su primera Asignación. Sin este caso, el alcance de
    /// CrearDocumento podría quedarse en «solo por Asignación» y nadie lo vería.
    /// </summary>
    [Fact]
    public async Task Gestor_CAE_crea_el_Documento_de_un_Trabajador_de_la_Empresa_propia_sin_Asignacion()
    {
        Guid trabajadorSinAsignacion;
        await using (var contexto = CrearContexto(_tenant))
        {
            var trabajador = Trabajador.DeEmpresa(_a.Propia, "Leire", "Sin Asignación", "55667788Z");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            trabajadorSinAsignacion = trabajador.Id;
        }

        var resultado = await EnviarAsync(_tenant, _gestorA, "GestorCae",
            Command(_a, AmbitoAplicacion.Trabajador, trabajadorSinAsignacion));

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        (await ContarDocumentosAsync(_tenant, AmbitoAplicacion.Trabajador, trabajadorSinAsignacion)).Should().Be(1);
    }

    [Fact]
    public async Task Gestor_CAE_sin_cartera_no_crea_ni_el_Documento_de_la_Empresa_propia()
    {
        var resultado = await EnviarAsync(_tenant, Guid.NewGuid(), "GestorCae",
            Command(_a, AmbitoAplicacion.Empresa, _a.Propia));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.PropietarioNoEncontrado");
        (await ContarDocumentosAsync(_tenant, AmbitoAplicacion.Empresa, _a.Propia)).Should().Be(0,
            "sin cartera el alcance es [] (falla cerrado), no «sin restricción»");
    }

    [Fact]
    public async Task El_rol_Cliente_no_crea_Documentos_ni_siquiera_de_su_propio_Cliente_empresarial()
    {
        var usuarioPortal = await CrearUsuarioPortalAsync(_tenant, _a.ClienteDentro);

        var resultado = await EnviarAsync(_tenant, usuarioPortal, "Cliente",
            Command(_a, AmbitoAplicacion.Trabajador, _a.TrabajadorDentro));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        (await ContarDocumentosAsync(_tenant, AmbitoAplicacion.Trabajador, _a.TrabajadorDentro)).Should().Be(0);
    }

    /// <summary>
    /// Segunda barrera, independiente del behavior de rol: llamando al handler directamente
    /// (sin pipeline), el alcance de GESTIÓN de la rama Empresa sigue negando al rol Cliente —
    /// ve la Empresa que trabaja en su Centro, pero no opera sobre ella (REC-149).
    /// </summary>
    [Fact]
    public async Task El_rol_Cliente_tampoco_pasa_el_alcance_de_gestion_si_se_salta_el_pipeline()
    {
        var usuarioPortal = await CrearUsuarioPortalAsync(_tenant, _a.ClienteDentro);
        await using var proveedor = ConstruirProveedor(_tenant, usuarioPortal, "Cliente");
        var handler = proveedor.GetRequiredService<IRequestHandler<CrearDocumentoCommand, Result<Guid>>>();

        var resultado = await handler.Handle(Command(_a, AmbitoAplicacion.Empresa, _a.Propia), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.PropietarioNoEncontrado");
        (await ContarDocumentosAsync(_tenant, AmbitoAplicacion.Empresa, _a.Propia)).Should().Be(0);
    }

    /// <summary>
    /// Otro Tenant, las dos direcciones: con el Tenant A activo, un propietario del Tenant B no
    /// existe (aislamiento de Tenant); con el Tenant B activo, el Gestor CAE no tiene cartera allí
    /// (aislamiento de cartera) — ni siquiera para lo que en B estaría "dentro".
    /// </summary>
    [Theory]
    [MemberData(nameof(Ambitos))]
    public async Task La_cartera_de_un_Tenant_no_da_alcance_sobre_otro_Tenant(AmbitoAplicacion ambito)
    {
        var propietarioDeB = PropietarioDentro(_b, ambito);

        var desdeA = await EnviarAsync(_tenant, _gestorA, "GestorCae", Command(_a, ambito, propietarioDeB));
        desdeA.EsFallido.Should().BeTrue();
        desdeA.Error.Codigo.Should().Be("Documento.PropietarioNoEncontrado");

        var desdeB = await EnviarAsync(_otroTenant, _gestorA, "GestorCae", Command(_b, ambito, propietarioDeB));
        desdeB.EsFallido.Should().BeTrue();
        desdeB.Error.Codigo.Should().Be("Documento.PropietarioNoEncontrado");

        (await ContarDocumentosAsync(_tenant, ambito, propietarioDeB)).Should().Be(0);
        (await ContarDocumentosAsync(_otroTenant, ambito, propietarioDeB)).Should().Be(0);
    }

    private static Guid PropietarioDentro(Escenario e, AmbitoAplicacion ambito) => ambito switch
    {
        AmbitoAplicacion.Trabajador => e.TrabajadorDentro,
        AmbitoAplicacion.Cliente => e.ClienteDentro,
        AmbitoAplicacion.Vehiculo => e.VehiculoDentro,
        AmbitoAplicacion.Proyecto => e.ProyectoDentro,
        _ => e.Propia
    };

    private static Guid PropietarioFuera(Escenario e, AmbitoAplicacion ambito) => ambito switch
    {
        AmbitoAplicacion.Trabajador => e.TrabajadorFuera,
        AmbitoAplicacion.Cliente => e.ClienteFuera,
        AmbitoAplicacion.Vehiculo => e.VehiculoFuera,
        AmbitoAplicacion.Proyecto => e.ProyectoFuera,
        _ => e.SubcontrataFuera
    };

    private static CrearDocumentoCommand Command(Escenario e, AmbitoAplicacion ambito, Guid propietario) => new(
        TrabajadorId: ambito == AmbitoAplicacion.Trabajador ? propietario : null,
        ClienteId: ambito == AmbitoAplicacion.Cliente ? propietario : null,
        EmpresaId: ambito == AmbitoAplicacion.Empresa ? propietario : null,
        VehiculoId: ambito == AmbitoAplicacion.Vehiculo ? propietario : null,
        ProyectoId: ambito == AmbitoAplicacion.Proyecto ? propietario : null,
        TipoDocumentoId: e.Tipos[ambito], FechaEmision: new DateOnly(2026, 1, 1),
        FechaVencimientoManual: null, ArchivoUrl: null, Comentarios: null);

    private async Task<Result<Guid>> EnviarAsync(Guid tenant, Guid usuarioId, string rol, CrearDocumentoCommand command)
    {
        await using var proveedor = ConstruirProveedor(tenant, usuarioId, rol);
        return await proveedor.GetRequiredService<IMediator>().Send(command);
    }

    private async Task<int> ContarDocumentosAsync(Guid tenant, AmbitoAplicacion ambito, Guid propietario)
    {
        await using var contexto = CrearContexto(tenant);
        return ambito switch
        {
            AmbitoAplicacion.Trabajador => await contexto.Documentos.CountAsync(d => d.TrabajadorId == propietario),
            AmbitoAplicacion.Cliente => await contexto.Documentos.CountAsync(d => d.ClienteId == propietario),
            AmbitoAplicacion.Vehiculo => await contexto.Documentos.CountAsync(d => d.VehiculoId == propietario),
            AmbitoAplicacion.Proyecto => await contexto.Documentos.CountAsync(d => d.ProyectoId == propietario),
            _ => await contexto.Documentos.CountAsync(d => d.EmpresaId == propietario)
        };
    }

    private async Task<Escenario> SembrarAsync(Guid tenant, string sufijo, string[] cifs)
    {
        await using var contexto = CrearContexto(tenant);

        var clienteDentro = Empresa.CrearComoCliente($"Cliente empresarial dentro {sufijo}", cifs[0], false, null, null);
        var clienteFuera = Empresa.CrearComoCliente($"Cliente empresarial fuera {sufijo}", cifs[1], false, null, null);
        var propia = new Empresa($"Empresa propia {sufijo}", cifs[2]);
        var subcontrataFuera = Empresa.CrearComoSubcontrata($"Subcontrata fuera {sufijo}", null, NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.AddRange(clienteDentro, clienteFuera, propia, subcontrataFuera);

        var centroDentro = new Centro(clienteDentro.Id, propia.Id, $"Centro dentro {sufijo}");
        var centroFuera = new Centro(clienteFuera.Id, subcontrataFuera.Id, $"Centro fuera {sufijo}");
        contexto.Centros.AddRange(centroDentro, centroFuera);

        var trabajadorDentro = Trabajador.DeEmpresa(propia.Id, "Nora", "Dentro", "22334455Y");
        var trabajadorFuera = Trabajador.DeSubcontrata(subcontrataFuera.Id, "Iker", "Fuera", "33445566R");
        contexto.Trabajadores.AddRange(trabajadorDentro, trabajadorFuera);

        var vehiculoDentro = Vehiculo.DeEmpresa(propia.Id, "Furgoneta dentro", "Modelo", sufijo == "A" ? "1111BCD" : "3333BCD");
        var vehiculoFuera = Vehiculo.DeSubcontrata(subcontrataFuera.Id, "Furgoneta fuera", "Modelo", sufijo == "A" ? "2222BCD" : "4444BCD");
        contexto.Vehiculos.AddRange(vehiculoDentro, vehiculoFuera);

        var tipos = new Dictionary<AmbitoAplicacion, Guid>();
        foreach (var ambito in new[] { AmbitoAplicacion.Trabajador, AmbitoAplicacion.Cliente, AmbitoAplicacion.Empresa, AmbitoAplicacion.Vehiculo, AmbitoAplicacion.Proyecto })
        {
            var tipo = new TipoDocumento($"Tipo {ambito} {sufijo}", 12, true, 1, ambito);
            contexto.TiposDocumento.Add(tipo);
            tipos[ambito] = tipo.Id;
        }

        await contexto.SaveChangesAsync();

        var proyectoDentro = Proyecto.Crear(clienteDentro.Id, centroDentro.Id, $"Proyecto dentro {sufijo}", new DateOnly(2026, 1, 1), null, null);
        var proyectoFuera = Proyecto.Crear(clienteFuera.Id, centroFuera.Id, $"Proyecto fuera {sufijo}", new DateOnly(2026, 1, 1), null, null);
        contexto.Proyectos.AddRange(proyectoDentro, proyectoFuera);
        contexto.Asignaciones.Add(new Asignacion(trabajadorDentro.Id, centroDentro.Id, new DateOnly(2026, 1, 1)));
        contexto.Asignaciones.Add(new Asignacion(trabajadorFuera.Id, centroFuera.Id, new DateOnly(2026, 1, 1)));
        await contexto.SaveChangesAsync();

        return new Escenario(
            clienteDentro.Id, propia.Id, trabajadorDentro.Id, vehiculoDentro.Id, proyectoDentro.Id,
            clienteFuera.Id, subcontrataFuera.Id, trabajadorFuera.Id, vehiculoFuera.Id, proyectoFuera.Id,
            tipos);
    }

    private async Task<Guid> OtorgarCarteraAsync(Guid tenant, Guid clienteId)
    {
        await using var contexto = CrearContexto(tenant);
        var usuarioId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        var raiz = AsignacionOperacion.Raiz(tenant, ServicioCae.Outbound, ahora, ahora);
        contexto.AsignacionesOperacion.Add(raiz);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
            raiz, usuarioId, AmbitoAsignacion.DeRelacionCliente(clienteId), ahora, null, ahora));
        await contexto.SaveChangesAsync();
        return usuarioId;
    }

    private async Task<Guid> CrearUsuarioPortalAsync(Guid tenant, Guid clienteId)
    {
        var usuarioId = Guid.NewGuid();
        await using var contexto = CrearContexto(tenant);
        contexto.Users.Add(new ApplicationUser
        {
            Id = usuarioId,
            UserName = $"portal-{usuarioId:N}@ejemplo.test",
            Email = $"portal-{usuarioId:N}@ejemplo.test",
            ClienteId = clienteId,
            TenantId = tenant
        });
        await contexto.SaveChangesAsync();
        return usuarioId;
    }

    private ServiceProvider ConstruirProveedor(Guid tenant, Guid usuarioId, string rol)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var usuario = new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: tenant);

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddApplication();

        servicios.AddSingleton<ICurrentUserService>(usuario);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<ISesionPrivilegiadaActual>(new SesionPrivilegiadaAusente());

        // El contenedor es dueño del contexto y lo libera con el proveedor.
        servicios.AddSingleton(_ => CrearContexto(tenant));
        servicios.AddSingleton<IAlcanceDatosService>(sp => new AlcanceDatosService(
            sp.GetRequiredService<CaeManagerDbContext>(), usuario, tenantActual, new SesionPrivilegiadaAusente()));

        servicios.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IEmpresasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITrabajadoresQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITiposDocumentoQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IVehiculosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IProyectosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IAsignacionesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ICentrosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        // Dependencias que MediatR resuelve al construir el pipeline y los
        // INotificationHandler de DocumentacionCambiadaEvent — mismo motivo
        // que en CrearDocumentoCommandBloqueadoParaConsultaTests.
        servicios.AddSingleton<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IVisitasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IDocumentosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IConfiguracionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IVisitaRepository, VisitaRepository>();

        servicios.AddSingleton<IDocumentoRepository, DocumentoRepository>();
        servicios.AddSingleton<ITrabajoAnalisisDocumentoRepository, TrabajoAnalisisDocumentoRepository>();
        servicios.AddSingleton<IAcreditacionDocumentoPlataformaRepository, AcreditacionDocumentoPlataformaRepository>();

        return servicios.BuildServiceProvider();
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
