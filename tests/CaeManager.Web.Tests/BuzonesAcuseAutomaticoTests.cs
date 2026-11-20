using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.FijarAcuseAutomaticoBuzon;
using CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;
using CaeManager.Application.Integraciones.Commands.ReactivarConexion;
using CaeManager.Application.Integraciones.Queries.ComprobarAccesoBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Integraciones.Queries.ObtenerResponsablesElegiblesBuzones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Integraciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Integraciones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La página de Buzones (maqueta <c>Buzones.dc.html</c>): el interruptor «Acuse automático» por fila, «Desactivado» por
/// defecto, y quién lo ve. El envío del acuse no se toca aquí: ningún test envía correo, el mediador es un doble que
/// solo registra qué comandos se piden. La autorización real (Administrador, buzón visible, Tenant) la deciden los
/// comandos y RLS (<c>FijarAcuseAutomaticoBuzonCommandHandlerTests</c>, <c>AcuseAutomaticoBuzonBajoRlsTests</c>).
/// </summary>
public class BuzonesAcuseAutomaticoTests : BunitContext
{
    public BuzonesAcuseAutomaticoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton<IOptions<Microsoft365GraphOptions>>(Options.Create(Opciones));
    }

    /// <summary>La configuración de Microsoft 365 de la página: con URL pública (se puede conectar) salvo que el test la vacíe.</summary>
    private Microsoft365GraphOptions Opciones { get; } = new()
    {
        ClientId = "cliente-secreto-de-prueba",
        ClientSecret = "valor-secreto-de-prueba",
        UrlPublicaBase = "https://app.talveg.test",
    };

    private sealed class MediadorBuzones : IMediator, IActividadBuzones
    {
        public List<ConexionIntegracionListaDto> Conexiones { get; set; } = [];
        public List<FijarAcuseAutomaticoBuzonCommand> ComandosAcuse { get; } = [];
        public List<FijarResponsableBuzonCommand> ComandosResponsable { get; } = [];
        public List<ReactivarConexionCommand> ComandosReactivar { get; } = [];
        public List<ClienteSelectorDto> Clientes { get; set; } = [];

        /// <summary>Si se pone, el comando falla con este error y no cambia nada (lo que haría el handler real).</summary>
        public Error? FalloDelComando { get; set; }

        /// <summary>Si se pone, el comando guarda el cambio y después lanza esta excepción (el handler real puede caer tras confirmar).</summary>
        public Exception? ExcepcionTrasAplicar { get; set; }

        /// <summary>Las cuentas que el servidor da por elegibles; null = todas las que se le preguntan.</summary>
        public HashSet<Guid>? Elegibles { get; set; }

        public List<ObtenerResponsablesElegiblesBuzonesQuery> ConsultasElegibles { get; } = [];

        /// <summary>Si es true, el listado falla: lo que pasa si la relectura tras un comando también cae.</summary>
        public bool FallaElListado { get; set; }

        /// <summary>Si es true, solo falla el cálculo de elegibles, no el listado.</summary>
        public bool FallaElCalculoDeElegibles { get; set; }

        /// <summary>«Último correo» y «Abiertas» por buzón; lo que no esté aquí no sale en la respuesta (como un buzón sin medir).</summary>
        public Dictionary<Guid, ActividadBuzonDto> Actividad { get; } = [];

        public List<IReadOnlyCollection<Guid>> ConsultasActividad { get; } = [];

        /// <summary>Si es true, solo falla la petición de actividad: la tabla sigue y esas dos celdas enseñan «—».</summary>
        public bool FallaLaActividad { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerConexionesIntegracionQuery => FallaElListado
                    ? throw new InvalidOperationException("listado caído")
                    : (IReadOnlyList<ConexionIntegracionListaDto>)Conexiones.ToList(),
                ObtenerResponsablesElegiblesBuzonesQuery q => Evaluar(q),
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)Clientes.ToList(),
                ReactivarConexionCommand c => Reactivar(c),
                ComprobarAccesoBuzonQuery q => Comprobar(q),
                FijarAcuseAutomaticoBuzonCommand c => Aplicar(c),
                FijarResponsableBuzonCommand c => Aplicar(c),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)respuesta);
        }

        public Task<IReadOnlyDictionary<Guid, ActividadBuzonDto>> ObtenerAsync(IReadOnlyCollection<Guid> conexionIds, CancellationToken cancellationToken = default)
        {
            if (FallaLaActividad) throw new InvalidOperationException("actividad caída");
            ConsultasActividad.Add(conexionIds);
            return Task.FromResult<IReadOnlyDictionary<Guid, ActividadBuzonDto>>(
                Actividad.Where(a => conexionIds.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value));
        }

        private IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> Evaluar(ObtenerResponsablesElegiblesBuzonesQuery consulta)
        {
            ConsultasElegibles.Add(consulta);
            if (FallaElCalculoDeElegibles) throw new InvalidOperationException("elegibles caído");
            return consulta.ConexionIds.ToDictionary(
                id => id,
                id => (IReadOnlySet<Guid>)consulta.CandidatoIds.Where(c => Elegibles is null || Elegibles.Contains(c)).ToHashSet());
        }

        public List<ComprobarAccesoBuzonQuery> ConsultasComprobar { get; } = [];

        /// <summary>Lo que responde «Comprobar acceso»; un fallo si se pone <see cref="FalloDelComando"/>.</summary>
        public ResultadoComprobacionAccesoBuzon ResultadoComprobacion { get; set; } = ResultadoComprobacionAccesoBuzon.Ok;

        private Result<ResultadoComprobacionAccesoBuzon> Comprobar(ComprobarAccesoBuzonQuery consulta)
        {
            ConsultasComprobar.Add(consulta);
            return FalloDelComando is { } fallo
                ? Result.Fallo<ResultadoComprobacionAccesoBuzon>(fallo)
                : Result.Exito(ResultadoComprobacion);
        }

        private Result Reactivar(ReactivarConexionCommand comando)
        {
            ComandosReactivar.Add(comando);
            return FalloDelComando is { } fallo ? Result.Fallo(fallo) : Result.Exito();
        }

        private Result Aplicar(FijarAcuseAutomaticoBuzonCommand comando)
        {
            ComandosAcuse.Add(comando);
            if (FalloDelComando is { } fallo) return Result.Fallo(fallo);
            Conexiones = Conexiones.Select(c => c.Id == comando.ConexionId ? c with { AcuseAutomaticoActivo = comando.Activo } : c).ToList();
            if (ExcepcionTrasAplicar is { } excepcion) throw excepcion;
            return Result.Exito();
        }

        private Result Aplicar(FijarResponsableBuzonCommand comando)
        {
            ComandosResponsable.Add(comando);
            if (FalloDelComando is { } fallo) return Result.Fallo(fallo);
            Conexiones = Conexiones.Select(c => c.Id == comando.ConexionId ? c with { ResponsableUsuarioId = comando.ResponsableUsuarioId } : c).ToList();
            if (ExcepcionTrasAplicar is { } excepcion) throw excepcion;
            return Result.Exito();
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>La consulta real del rol se prueba contra PostgreSQL en la integración.</summary>
    private sealed class DirectorioConGestores(
        IReadOnlyDictionary<string, IReadOnlyList<CuentaVisibleEnRol>> cuentasPorRol,
        Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> userManager, CaeManagerDbContext identidad)
        : DirectorioUsuariosTenant(
            userManager, new ConexionesGen2Tests.ContextoTenantsFalso(), new ConexionesGen2Tests.TenantActualFalso(),
            new PuertaAccesoDatos(), identidad, null!)
    {
        public override Task<IReadOnlyList<CuentaVisibleEnRol>> ObtenerVisiblesEnRolAsync(
            string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult(cuentasPorRol.TryGetValue(rol, out var cuentas) ? cuentas : []);

        /// <summary>Todas las del directorio, sea cual sea la clave de rol con que el test las agrupe.</summary>
        public override Task<IReadOnlyList<CuentaVisibleEnRol>> ObtenerCuentasVisiblesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CuentaVisibleEnRol>>(cuentasPorRol.Values.SelectMany(c => c).ToList());
    }

    private static DirectorioUsuariosTenant Directorio(IReadOnlyDictionary<string, IReadOnlyList<CuentaVisibleEnRol>> cuentasPorRol)
    {
        var identidad = new CaeManagerDbContext(new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(BuzonesAcuseAutomaticoTests)), new ConexionesGen2Tests.TenantActualFalso());
        var usuarios = new Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>(
            new ConexionesGen2Tests.AlmacenUsuariosFalso(), null!, null!, null!, null!, null!, null!, null!, null!);
        return new DirectorioConGestores(cuentasPorRol, usuarios, identidad);
    }

    private static ConexionIntegracionListaDto Buzon(
        string nombre, bool acuse = false, Guid? propietario = null, Guid? responsable = null,
        EstadoConexionIntegracion estado = EstadoConexionIntegracion.Habilitada, string? ultimoError = null) =>
        new(Guid.NewGuid(), $"{nombre}@ejemplo.test", nombre, null, null, estado, DateTime.UtcNow,
            propietario, ultimoError, responsable, acuse, EsBuzonCompartido: propietario is null);

    private IRenderedComponent<Buzones> Renderizar(
        MediadorBuzones mediador, string? rol = Roles.Administrador, IReadOnlyList<CuentaVisibleEnRol>? gestores = null,
        IReadOnlyDictionary<string, IReadOnlyList<CuentaVisibleEnRol>>? cuentasPorRol = null)
    {
        var autorizacion = AddAuthorization();
        if (rol is not null) autorizacion.SetAuthorized("usuario").SetRoles(rol);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<IActividadBuzones>(_ => mediador);
        Services.AddScoped<ToastService>();
        var directorio = cuentasPorRol ?? new Dictionary<string, IReadOnlyList<CuentaVisibleEnRol>>
        {
            [Roles.GestorCae] = gestores ?? []
        };
        Services.AddScoped(_ => Directorio(directorio));
        return Render<Buzones>();
    }

    private static string Fila(IRenderedComponent<Buzones> cut, string nombre) =>
        cut.FindAll("[data-testid=fila-buzon]").Single(f => f.TextContent.Contains($"{nombre}@ejemplo.test")).TextContent;

    [Fact]
    public void La_columna_se_llama_Acuse_automatico_y_cada_buzon_trae_su_interruptor_desactivado_por_defecto()
    {
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae"), Buzon("proyectos")] });

        cut.FindAll("th").Select(t => t.TextContent.Trim()).Should().Contain("Acuse automático");
        var interruptores = cut.FindAll("[data-testid=interruptor-acuse]");
        interruptores.Should().HaveCount(2, "control positivo: un interruptor por buzón");
        interruptores.Should().OnlyContain(i => i.GetAttribute("role") == "switch" && i.GetAttribute("aria-checked") == "false");
        interruptores[0].GetAttribute("aria-label").Should().Be("Acuse automático de cae");
        cut.FindAll("[data-testid=estado-acuse]").Should().OnlyContain(e => e.TextContent.Trim() == "Desactivado");
    }

    [Fact]
    public void Un_buzon_con_el_acuse_activo_se_ve_activado()
    {
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae", acuse: true), Buzon("proyectos")] });

        Fila(cut, "cae").Should().Contain("Activado");
        Fila(cut, "proyectos").Should().Contain("Desactivado");
        cut.FindAll("[data-testid=fila-buzon]").Single(f => f.TextContent.Contains("cae@"))
            .QuerySelector("[data-testid=interruptor-acuse]")!.GetAttribute("aria-checked").Should().Be("true");
    }

    [Fact]
    public void Activar_el_acuse_pide_confirmar_con_las_reglas_y_no_envia_nada_hasta_guardar()
    {
        var mediador = new MediadorBuzones { Conexiones = [Buzon("cae")] };
        var cut = Renderizar(mediador);

        cut.Find("[data-testid=interruptor-acuse]").Click();

        var modal = cut.Find(".modal-contenido").TextContent;
        modal.Should().Contain("Acuse automático al recibir un correo").And.Contain("cae@ejemplo.test");
        modal.Should().Contain("Solo se envía en el primer correo de una conversación nueva")
            .And.Contain("respuesta automática").And.Contain("lista de distribución").And.Contain("no-reply");
        modal.Should().Contain("puede fallar", "la detección de automáticos y listas es heurística y el texto no promete lo contrario")
            .And.Contain("24 horas").And.Contain("autenticación");
        modal.Should().NotContain("Nunca se envía a listas").And.NotContain("cada conversación nueva");
        mediador.ComandosAcuse.Should().BeEmpty("el interruptor solo abre la confirmación: activar envía correo externo");
        cut.Find("[data-testid=interruptor-acuse]").GetAttribute("aria-checked").Should().Be("false");
    }

    [Fact]
    public void La_confirmacion_avisa_de_que_el_texto_del_acuse_sigue_siendo_borrador()
    {
        PlantillaAcuseBuzon.EsBorrador.Should().BeTrue("control: la plantilla sigue pendiente de aprobación");
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] });

        cut.Find("[data-testid=interruptor-acuse]").Click();

        cut.Find("[data-testid=aviso-acuse-borrador]").TextContent.Should().Contain("provisional");
    }

    [Fact]
    public void Guardar_en_la_confirmacion_activa_el_acuse_recarga_el_estado_y_lo_avisa()
    {
        var buzon = Buzon("cae");
        var mediador = new MediadorBuzones { Conexiones = [buzon] };
        var cut = Renderizar(mediador);
        cut.Find("[data-testid=interruptor-acuse]").Click();

        cut.Find("[data-testid=guardar-acuse]").Click();

        mediador.ComandosAcuse.Should().ContainSingle().Which.Should().Be(new FijarAcuseAutomaticoBuzonCommand(buzon.Id, true));
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=interruptor-acuse]").GetAttribute("aria-checked").Should().Be("true");
            cut.Find("[data-testid=estado-acuse]").TextContent.Trim().Should().Be("Activado");
            cut.FindAll(".modal-contenido").Should().BeEmpty("la confirmación se cierra al guardar");
        });
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().StartWith("Acuse automático activado en cae.")
            .And.NotContain("cada conversación nueva", "el aviso no promete un acuse a todas las conversaciones");
    }

    [Fact]
    public void Cancelar_la_confirmacion_no_cambia_nada()
    {
        var mediador = new MediadorBuzones { Conexiones = [Buzon("cae")] };
        var cut = Renderizar(mediador);
        cut.Find("[data-testid=interruptor-acuse]").Click();

        cut.FindAll(".modal-contenido button").Single(b => b.TextContent.Trim() == "Cancelar").Click();

        mediador.ComandosAcuse.Should().BeEmpty();
        cut.FindAll(".modal-contenido").Should().BeEmpty();
        cut.Find("[data-testid=interruptor-acuse]").GetAttribute("aria-checked").Should().Be("false");
    }

    [Fact]
    public void Desactivar_el_acuse_no_pide_confirmar()
    {
        var buzon = Buzon("cae", acuse: true);
        var mediador = new MediadorBuzones { Conexiones = [buzon] };
        var cut = Renderizar(mediador);

        cut.Find("[data-testid=interruptor-acuse]").Click();

        mediador.ComandosAcuse.Should().ContainSingle().Which.Should().Be(new FijarAcuseAutomaticoBuzonCommand(buzon.Id, false));
        cut.WaitForAssertion(() => cut.Find("[data-testid=estado-acuse]").TextContent.Trim().Should().Be("Desactivado"));
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Be("Acuse automático desactivado en cae.");
    }

    /// <summary>
    /// Un buzón que no es del alcance de quien pide (otro Tenant, Empresa fuera de su cartera) responde «no encontrado»,
    /// igual que uno inexistente: la pantalla muestra el error del servidor y vuelve a leer el estado guardado, no el
    /// que el interruptor sugería.
    /// </summary>
    [Fact]
    public void Si_el_servidor_rechaza_el_cambio_se_avisa_y_el_estado_que_se_ve_es_el_guardado()
    {
        var mediador = new MediadorBuzones
        {
            Conexiones = [Buzon("cae", acuse: true)],
            FalloDelComando = Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión.")
        };
        var cut = Renderizar(mediador);

        cut.Find("[data-testid=interruptor-acuse]").Click();

        cut.WaitForAssertion(() =>
        {
            Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
                .Which.Tono.Should().Be(TonoToast.Error);
            cut.Find("[data-testid=estado-acuse]").TextContent.Trim().Should().Be("Activado");
        });
    }

    [Theory]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.Consulta)]
    [InlineData(null)]
    public void El_componente_renderizado_por_quien_no_es_Administrador_muestra_el_estado_sin_interruptor_ni_selector(string? rol)
    {
        var cut = Renderizar(
            new MediadorBuzones { Conexiones = [Buzon("cae", acuse: true), Buzon("proyectos")] },
            rol,
            [new CuentaVisibleEnRol(Guid.NewGuid(), "Lucía Sanz", "lucia@ejemplo.test", null)]);

        cut.FindAll("[data-testid=fila-buzon]").Should().HaveCount(2, "control positivo: el listado se ve");
        cut.FindAll("[data-testid=interruptor-acuse]").Should().BeEmpty();
        cut.FindAll("[data-testid=selector-responsable]").Should().BeEmpty();
        cut.FindAll("[data-testid=anadir-buzon]").Should().BeEmpty("con la URL pública configurada solo el Administrador ve «Añadir buzón»");
        cut.FindAll("[data-testid=estado-acuse]").Select(e => e.TextContent.Trim()).Should().Equal("Activado", "Desactivado");
    }

    /// <summary>
    /// El test anterior renderiza el componente directamente: prueba qué dibuja, no quién llega a la página. El acceso a la
    /// ruta lo decide el <c>[Authorize]</c> de la página, que se lee aquí por reflexión (un bUnit sin router no evalúa
    /// <c>[Authorize]</c>); el efecto de ese atributo sobre cada rol lo evalúa el enrutador de la aplicación.
    /// </summary>
    [Fact]
    public void La_ruta_de_Buzones_solo_admite_al_Administrador()
    {
        typeof(Buzones).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Select(r => r.Template)
            .Should().Equal("/integraciones/buzones");

        var autorizacion = typeof(Buzones).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Should().ContainSingle("la página declara un único [Authorize] con Roles=").Subject;
        autorizacion.Roles.Should().Be(Roles.Administrador, "ni Dirección CAE, ni Coordinador CAE, ni Gestor CAE, ni Consulta");
    }

    /// <summary>
    /// El selector no tiene lista de roles propia: pregunta al servidor quién es elegible (<c>ResponsableBuzonElegible</c>), con
    /// todas las cuentas visibles como universo. Administrador, Dirección CAE y Coordinador CAE aparecen si el servidor los da
    /// por elegibles; un Gestor CAE y una cuenta Consulta que el servidor descarta no aparecen, aunque se le pregunte por ellas.
    /// </summary>
    [Fact]
    public void El_selector_de_responsable_ofrece_exactamente_las_cuentas_que_el_servidor_da_por_elegibles()
    {
        var admin = new CuentaVisibleEnRol(Guid.NewGuid(), "Ana Admin", "ana@ejemplo.test", null);
        var direccion = new CuentaVisibleEnRol(Guid.NewGuid(), "Berta Dirección", "berta@ejemplo.test", null);
        var coordinador = new CuentaVisibleEnRol(Guid.NewGuid(), "Carla Coordinadora", "carla@ejemplo.test", null);
        var gestorConCartera = new CuentaVisibleEnRol(Guid.NewGuid(), "Diego Gestor", "diego@ejemplo.test", null);
        var gestorSinCartera = new CuentaVisibleEnRol(Guid.NewGuid(), "Elena Sincartera", "elena@ejemplo.test", null);
        var consulta = new CuentaVisibleEnRol(Guid.NewGuid(), "Fran Consulta", "fran@ejemplo.test", null);
        var buzon = Buzon("cae");
        var mediador = new MediadorBuzones
        {
            Conexiones = [buzon],
            Elegibles = [admin.Id, direccion.Id, coordinador.Id, gestorConCartera.Id]
        };
        var cut = Renderizar(mediador, cuentasPorRol: new Dictionary<string, IReadOnlyList<CuentaVisibleEnRol>>
        {
            [Roles.Administrador] = [admin],
            [Roles.DireccionCae] = [direccion],
            [Roles.CoordinadorCae] = [coordinador],
            [Roles.GestorCae] = [gestorConCartera, gestorSinCartera],
            [Roles.Consulta] = [consulta]
        });

        cut.Find("[data-testid=selector-responsable]").QuerySelectorAll("option").Select(o => o.TextContent.Trim())
            .Should().Equal("Sin responsable", "Ana Admin", "Berta Dirección", "Carla Coordinadora", "Diego Gestor");
        var pregunta = mediador.ConsultasElegibles.Should().ContainSingle().Subject;
        pregunta.ConexionIds.Should().Equal(buzon.Id);
        pregunta.CandidatoIds.Should().BeEquivalentTo(
            [admin.Id, direccion.Id, coordinador.Id, gestorConCartera.Id, gestorSinCartera.Id, consulta.Id],
            "el universo son todas las cuentas visibles: quién entra lo decide la regla del servidor, no un filtro por rol aquí");
    }

    /// <summary>
    /// Hallazgo de la revisión r2: el pie no puede prometer que toda conversación nace asignada (queda sin asignar si no hay
    /// Responsable o si deja de ser elegible) ni que sin Asignación de Cartera no se ve (Administrador, Dirección CAE y
    /// Consulta alcanzan toda la organización, <c>Roles.AlcanzaTodaLaOrganizacion</c>). Hallazgo de la r3: la regla es la de
    /// las conversaciones que abren un hilo nuevo; la continuación de un hilo cerrado conserva a la persona asignada a la
    /// anterior (<c>Conversacion.ContinuarCorreo</c>, <c>IngestaWebhookService</c>) y el pie lo dice. En es y en ca-ES.
    /// </summary>
    [Theory]
    [InlineData("es-ES", "queda sin asignar si el buzón no tiene Responsable",
        "Administrador, Dirección CAE y Consulta ven las conversaciones sin necesitar empresas asignadas", "nace asignada",
        "Cada conversación que abre un hilo nuevo se asigna al Responsable del buzón",
        "Si el correo continúa un hilo cerrado, la conversación nueva conserva a la persona que tenía asignada la anterior",
        "Cada conversación nueva")]
    [InlineData("ca-ES", "queda sense assignar si la bústia no té Responsable",
        "Administrador, Direcció CAE i Consulta veuen les converses sense necessitar empreses assignades", "neix assignada",
        "Cada conversa que obre un fil nou s'assigna al Responsable de la bústia",
        "Si el correu continua un fil tancat, la conversa nova conserva la persona que tenia assignada l'anterior",
        "Cada conversa nova")]
    public void El_pie_dice_cuando_una_conversacion_queda_sin_asignar_y_quien_ve_sin_Asignacion_de_Cartera(
        string cultura, string sinAsignar, string sinCartera, string afirmacionFalsa,
        string reglaDeHiloNuevo, string excepcionDeHiloCerrado, string generalizacion)
    {
        var anterior = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(cultura);
        try
        {
            var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] });

            var pie = cut.Find(".pie-buzones").TextContent;
            pie.Should().Contain(sinAsignar).And.Contain(sinCartera).And.NotContain(afirmacionFalsa);
            pie.Should().Contain(reglaDeHiloNuevo).And.Contain(excepcionDeHiloCerrado)
                .And.NotContain(generalizacion, "la regla del Responsable no cubre la continuación de un hilo cerrado");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = anterior;
        }
    }

    /// <summary>
    /// Hallazgo de la revisión r2: <c>ResponsableBuzonElegible</c> acepta a una cuenta de un Operador CAE externo por el rol
    /// guardado de la vía heredada (Gestor CAE) aunque su rol de origen sea Consulta. Un selector que construyera su universo
    /// por rol de origen no la preguntaría y no la ofrecería; aquí, si el servidor la da por elegible, aparece.
    /// </summary>
    [Fact]
    public void Una_cuenta_delegada_con_rol_de_origen_Consulta_que_el_servidor_acepta_aparece_en_el_selector()
    {
        var delegada = new CuentaVisibleEnRol(Guid.NewGuid(), "Gema Delegada", "gema@ejemplo.test", null);
        var buzon = Buzon("cae");
        var mediador = new MediadorBuzones { Conexiones = [buzon], Elegibles = [delegada.Id] };

        var cut = Renderizar(mediador, cuentasPorRol: new Dictionary<string, IReadOnlyList<CuentaVisibleEnRol>>
        {
            [Roles.Consulta] = [delegada]
        });

        cut.Find("[data-testid=selector-responsable]").QuerySelectorAll("option").Select(o => o.TextContent.Trim())
            .Should().Equal("Sin responsable", "Gema Delegada");
        mediador.ConsultasElegibles.Should().ContainSingle().Which.CandidatoIds.Should().Equal(delegada.Id);
    }

    [Fact]
    public void Un_buzon_personal_no_entra_en_la_consulta_de_elegibles()
    {
        var compartido = Buzon("cae");
        var mediador = new MediadorBuzones { Conexiones = [Buzon("marta", propietario: Guid.NewGuid()), compartido] };

        Renderizar(mediador, gestores: [new CuentaVisibleEnRol(Guid.NewGuid(), "Lucía Sanz", "lucia@ejemplo.test", null)]);

        mediador.ConsultasElegibles.Should().ContainSingle().Which.ConexionIds.Should().Equal(compartido.Id);
    }

    /// <summary>
    /// Una excepción del comando no dice si guardó. Aquí guarda y luego lanza: la página no puede quedarse con el
    /// «Desactivado» anterior, tiene que releer el estado del servidor.
    /// </summary>
    [Fact]
    public void Si_el_comando_del_acuse_guarda_y_despues_lanza_la_fila_enseña_el_estado_guardado()
    {
        var mediador = new MediadorBuzones
        {
            Conexiones = [Buzon("cae", acuse: true)],
            ExcepcionTrasAplicar = new InvalidOperationException("cae tras confirmar")
        };
        var cut = Renderizar(mediador);

        cut.Find("[data-testid=interruptor-acuse]").Click();

        cut.WaitForAssertion(() =>
        {
            mediador.ComandosAcuse.Should().ContainSingle();
            Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Which.Tono.Should().Be(TonoToast.Error);
            cut.Find("[data-testid=estado-acuse]").TextContent.Trim().Should().Be("Desactivado",
                "el comando ya lo desactivó: la fila lo enseña tras releer, no el «Activado» de antes");
            cut.Find("[data-testid=interruptor-acuse]").GetAttribute("disabled").Should().BeNull("la fila vuelve a ser operable");
        });
    }

    /// <summary>Si además la relectura cae, no se deja un listado con controles sobre un estado dudoso: se vacía y se avisa.</summary>
    [Fact]
    public void Si_el_comando_lanza_y_la_relectura_tambien_falla_la_pagina_se_vacia_y_avisa()
    {
        var mediador = new MediadorBuzones
        {
            Conexiones = [Buzon("cae", acuse: true)],
            ExcepcionTrasAplicar = new InvalidOperationException("cae tras confirmar")
        };
        var cut = Renderizar(mediador);
        mediador.FallaElListado = true;

        cut.Find("[data-testid=interruptor-acuse]").Click();

        cut.WaitForAssertion(() =>
        {
            mediador.ComandosAcuse.Should().ContainSingle();
            cut.Find("[data-testid=aviso-buzones-no-cargados]").Should().NotBeNull();
            cut.FindAll("[data-testid=interruptor-acuse]").Should().BeEmpty();
        });
    }

    /// <summary>El cálculo de elegibles es auxiliar del selector: si cae, el listado y el acuse siguen y el selector ofrece al Responsable actual.</summary>
    [Fact]
    public void Si_falla_el_calculo_de_elegibles_el_listado_sigue_y_cada_selector_ofrece_solo_a_su_responsable()
    {
        var lucia = new CuentaVisibleEnRol(Guid.NewGuid(), "Lucía Sanz", "lucia@ejemplo.test", null);
        var andres = new CuentaVisibleEnRol(Guid.NewGuid(), "Andrés Rey", "andres@ejemplo.test", null);
        var mediador = new MediadorBuzones
        {
            Conexiones = [Buzon("cae", responsable: lucia.Id)],
            FallaElCalculoDeElegibles = true
        };

        var cut = Renderizar(mediador, gestores: [lucia, andres]);

        cut.FindAll("[data-testid=aviso-buzones-no-cargados]").Should().BeEmpty();
        cut.Find("[data-testid=interruptor-acuse]").Should().NotBeNull();
        mediador.ConsultasElegibles.Should().ContainSingle("control: la consulta se intentó");
        cut.Find("[data-testid=selector-responsable]").QuerySelectorAll("option").Select(o => o.TextContent.Trim())
            .Should().Equal("Sin responsable", "—");
    }

    [Fact]
    public void Si_el_comando_del_responsable_guarda_y_despues_lanza_el_selector_enseña_el_guardado()
    {
        var lucia = new CuentaVisibleEnRol(Guid.NewGuid(), "Lucía Sanz", "lucia@ejemplo.test", null);
        var andres = new CuentaVisibleEnRol(Guid.NewGuid(), "Andrés Rey", "andres@ejemplo.test", null);
        var mediador = new MediadorBuzones
        {
            Conexiones = [Buzon("cae", responsable: lucia.Id)],
            ExcepcionTrasAplicar = new InvalidOperationException("cae tras confirmar")
        };
        var cut = Renderizar(mediador, gestores: [lucia, andres]);

        cut.Find("[data-testid=selector-responsable]").Change(andres.Id.ToString());

        cut.WaitForAssertion(() =>
        {
            Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Which.Tono.Should().Be(TonoToast.Error);
            cut.Find("[data-testid=selector-responsable] option[selected]").TextContent.Trim().Should().Be("Andrés Rey");
        });
    }

    [Fact]
    public void Un_buzon_personal_no_tiene_acuse_ni_se_cambia_su_responsable()
    {
        var cut = Renderizar(new MediadorBuzones
        {
            Conexiones = [Buzon("marta", propietario: Guid.NewGuid()), Buzon("cae")]
        });

        var fila = cut.FindAll("[data-testid=fila-buzon]").Single(f => f.TextContent.Contains("marta@"));
        fila.QuerySelector("[data-testid=interruptor-acuse]").Should().BeNull();
        fila.QuerySelector("[data-testid=selector-responsable]").Should().BeNull();
        fila.QuerySelector("[data-testid=acuse-no-aplica]")!.TextContent.Should().Be("No aplica");
        cut.FindAll("[data-testid=interruptor-acuse]").Should().ContainSingle("control: el compartido sí lo tiene");
    }

    [Fact]
    public void El_Administrador_cambia_el_responsable_con_el_selector_y_el_estado_vuelve_del_servidor()
    {
        var lucia = new CuentaVisibleEnRol(Guid.NewGuid(), "Lucía Sanz", "lucia@ejemplo.test", null);
        var andres = new CuentaVisibleEnRol(Guid.NewGuid(), "Andrés Rey", "andres@ejemplo.test", null);
        var buzon = Buzon("cae", responsable: lucia.Id);
        var mediador = new MediadorBuzones { Conexiones = [buzon] };
        var cut = Renderizar(mediador, gestores: [lucia, andres]);

        var selector = cut.Find("[data-testid=selector-responsable]");
        selector.QuerySelectorAll("option").Select(o => o.TextContent.Trim())
            .Should().Equal("Sin responsable", "Andrés Rey", "Lucía Sanz");
        selector.QuerySelector("option[selected]")!.TextContent.Trim().Should().Be("Lucía Sanz");

        selector.Change(andres.Id.ToString());

        mediador.ComandosResponsable.Should().ContainSingle().Which.Should().Be(new FijarResponsableBuzonCommand(buzon.Id, andres.Id));
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid=selector-responsable] option[selected]").TextContent.Trim().Should().Be("Andrés Rey"));
    }

    /// <summary>
    /// Si el servidor rechaza el cambio de responsable (p. ej. sin Asignación de Cartera), el estado guardado no cambia y Blazor
    /// no parchearía el DOM: el navegador seguiría mostrando lo elegido. La página recrea el selector (<c>@key</c> con una versión)
    /// para que enseñe lo guardado. bUnit vuelve a parsear el marcado, así que aquí solo se fija el estado que se muestra; que el
    /// <c>&lt;select&gt;</c> se recree de verdad en el navegador no es observable en bUnit (hueco declarado).
    /// </summary>
    [Fact]
    public void Si_el_servidor_rechaza_el_responsable_el_selector_se_recrea_y_enseña_el_guardado()
    {
        var lucia = new CuentaVisibleEnRol(Guid.NewGuid(), "Lucía Sanz", "lucia@ejemplo.test", null);
        var andres = new CuentaVisibleEnRol(Guid.NewGuid(), "Andrés Rey", "andres@ejemplo.test", null);
        var mediador = new MediadorBuzones
        {
            Conexiones = [Buzon("cae", responsable: lucia.Id)],
            FalloDelComando = Error.Crear("Buzon.ResponsableSinCartera", "Esa persona no tiene Asignación de Cartera.")
        };
        var cut = Renderizar(mediador, gestores: [lucia, andres]);
        var selectorInicial = cut.Find("[data-testid=selector-responsable]");

        selectorInicial.Change(andres.Id.ToString());

        cut.WaitForAssertion(() =>
        {
            mediador.ComandosResponsable.Should().ContainSingle();
            Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Which.Tono.Should().Be(TonoToast.Error);
            cut.Find("[data-testid=selector-responsable] option[selected]").TextContent.Trim().Should().Be("Lucía Sanz");
        });
    }

    [Fact]
    public void Sin_buzones_la_pagina_lo_dice_y_no_dibuja_la_tabla()
    {
        var cut = Renderizar(new MediadorBuzones());

        cut.Markup.Should().Contain("Aún no hay buzones conectados.");
        cut.Markup.Should().Contain("Añade el primero con su dirección.");
        cut.FindAll("table").Should().BeEmpty();
    }

    // ---- PR-B1: columnas «Último correo» y «Abiertas» (lienzo «Comunicaciones multi-buzón», tablero Pulida-Buzones) ----

    [Fact]
    public void Las_cabeceras_siguen_el_orden_del_lienzo_con_Ultimo_correo_tras_Estado_y_Abiertas_tras_Responsable()
    {
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("compartido")] });

        var cabeceras = cut.FindAll("thead th").Select(t => t.TextContent.Trim()).ToList();

        cabeceras.Should().Equal("Buzón", "Tipo", "Enruta a", "Estado", "Último correo", "Responsable", "Abiertas", "Acuse automático", "Acciones");
    }

    [Fact]
    public void Una_sola_peticion_de_actividad_lleva_todos_los_buzones_de_la_tabla()
    {
        var mediador = new MediadorBuzones { Conexiones = [Buzon("uno"), Buzon("dos"), Buzon("tres")] };

        Renderizar(mediador);

        mediador.ConsultasActividad.Should().ContainSingle().Which.Should().BeEquivalentTo(mediador.Conexiones.Select(c => c.Id));
    }

    [Fact]
    public void Abiertas_enseña_la_cifra_de_cada_buzon_y_un_guion_si_no_hay_dato()
    {
        var conCifra = Buzon("conCifra");
        var sinDato = Buzon("sinDato");
        var mediador = new MediadorBuzones { Conexiones = [conCifra, sinDato] };
        mediador.Actividad[conCifra.Id] = new ActividadBuzonDto(null, 7);

        var cut = Renderizar(mediador);

        cut.FindAll("[data-testid=fila-buzon]").Single(f => f.TextContent.Contains("conCifra@"))
            .QuerySelector("[data-testid=abiertas-buzon]")!.TextContent.Trim().Should().Be("7");
        cut.FindAll("[data-testid=fila-buzon]").Single(f => f.TextContent.Contains("sinDato@"))
            .QuerySelector("[data-testid=abiertas-buzon]")!.TextContent.Trim().Should().Be("—");
    }

    [Fact]
    public void Ultimo_correo_se_escribe_Hoy_Ayer_o_la_fecha_en_hora_peninsular_y_Sin_correos_visibles_si_no_se_ve_ninguno()
    {
        // Reloj fijado a las 22:30 UTC del 15/06/2026, que en Madrid (UTC+2) ya es el 16 a las 00:30: el día de negocio es el
        // peninsular, no el UTC. Un correo de las 22:10 UTC (00:10 del 16) es «Hoy»; uno de las 10:00 UTC del 15 es «Ayer».
        // La hora va en dos dígitos (D-19).
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijoBuzones(new DateTimeOffset(2026, 6, 15, 22, 30, 0, TimeSpan.Zero)));
        var ahora = new DateTime(2026, 6, 15, 22, 10, 0, DateTimeKind.Utc);
        var ayerAMediodia = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var antiguo = new DateTime(2026, 1, 15, 9, 5, 0, DateTimeKind.Utc);
        var hoy = Buzon("hoy");
        var ayer = Buzon("ayer");
        var viejo = Buzon("viejo");
        var vacio = Buzon("vacio");
        var mediador = new MediadorBuzones { Conexiones = [hoy, ayer, viejo, vacio] };
        mediador.Actividad[hoy.Id] = new ActividadBuzonDto(ahora, 1);
        mediador.Actividad[ayer.Id] = new ActividadBuzonDto(ayerAMediodia, 1);
        mediador.Actividad[viejo.Id] = new ActividadBuzonDto(antiguo, 1);
        mediador.Actividad[vacio.Id] = new ActividadBuzonDto(null, 0);

        var cut = Renderizar(mediador);

        string Celda(string nombre) => cut.FindAll("[data-testid=fila-buzon]").Single(f => f.TextContent.Contains($"{nombre}@"))
            .QuerySelector("[data-testid=ultimo-correo]")!.TextContent.Trim();
        Celda("hoy").Should().Be("Hoy, 00:10");
        Celda("ayer").Should().Be("Ayer, 12:00");
        Celda("viejo").Should().Be("15/01/2026 10:05");
        Celda("vacio").Should().Be("Sin correos visibles");
    }

    /// <summary>
    /// DN-M (Chris, 2026-11-20): cuando la petición no ve ningún correo el texto no dice «aún» ni «ninguno», porque no puede
    /// afirmar que el buzón esté vacío (puede haber correo fuera del alcance de quien mira). El «—» de «no medido» no cambia.
    /// </summary>
    [Theory]
    [InlineData("es-ES", "Sin correos visibles", "Sin correos aún")]
    [InlineData("ca-ES", "Sense correus visibles", "Encara sense correus")]
    public void Sin_ningun_correo_visible_el_texto_no_afirma_que_el_buzon_este_vacio_y_sin_dato_sigue_el_guion(
        string cultura, string esperado, string textoAnterior)
    {
        var anterior = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(cultura);
        try
        {
            var sinVisibles = Buzon("sinVisibles");
            var sinDato = Buzon("sinDato");
            var mediador = new MediadorBuzones { Conexiones = [sinVisibles, sinDato] };
            mediador.Actividad[sinVisibles.Id] = new ActividadBuzonDto(null, 0);

            var cut = Renderizar(mediador);

            string Celda(string nombre) => cut.FindAll("[data-testid=fila-buzon]").Single(f => f.TextContent.Contains($"{nombre}@"))
                .QuerySelector("[data-testid=ultimo-correo]")!.TextContent.Trim();
            Celda("sinVisibles").Should().Be(esperado).And.NotBe(textoAnterior);
            Celda("sinDato").Should().Be("—");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = anterior;
        }
    }

    private sealed class RelojFijoBuzones(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }

    [Fact]
    public void Si_falla_la_actividad_la_tabla_sigue_y_las_dos_celdas_enseñan_un_guion_en_vez_de_un_cero()
    {
        var mediador = new MediadorBuzones { Conexiones = [Buzon("compartido")], FallaLaActividad = true };

        var cut = Renderizar(mediador);

        cut.FindAll("[data-testid=aviso-buzones-no-cargados]").Should().BeEmpty();
        var fila = cut.Find("[data-testid=fila-buzon]");
        fila.QuerySelector("[data-testid=ultimo-correo]")!.TextContent.Trim().Should().Be("—");
        fila.QuerySelector("[data-testid=abiertas-buzon]")!.TextContent.Trim().Should().Be("—");
    }

    [Fact]
    public void La_franja_de_error_ocupa_las_nueve_columnas_de_la_tabla()
    {
        var cut = Renderizar(new MediadorBuzones
        {
            Conexiones = [Buzon("roto", estado: EstadoConexionIntegracion.ConError, ultimoError: "Token caducado")]
        });

        cut.Find(".fila-franja-error > td").GetAttribute("colspan").Should().Be("9", "las ocho de datos y la de acciones (L-57)");
    }

    // ---- Añadir buzón (cajón), franja de error con «Reconectar» y punto de estado ----

    /// <summary>
    /// Sin la URL pública de la aplicación el endpoint de conectar no puede arrancar el flujo OAuth: no se ofrece «Añadir buzón»
    /// y la página lo explica sin enseñar ningún valor de configuración.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_URL_publica_no_hay_Anadir_buzon_y_el_aviso_no_enseña_la_configuracion(string? urlPublica)
    {
        Opciones.UrlPublicaBase = urlPublica;

        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] });

        cut.FindAll("[data-testid=anadir-buzon]").Should().BeEmpty();
        var aviso = cut.Find("[data-testid=aviso-conexion-no-disponible]");
        aviso.GetAttribute("role").Should().Be("status");
        aviso.TextContent.Should().Contain("Soporte TALVEG");
        cut.Markup.Should().NotContain("cliente-secreto-de-prueba").And.NotContain("valor-secreto-de-prueba")
            .And.NotContain("Integraciones:Microsoft365").And.NotContain("UrlPublicaBase").And.NotContain("app.talveg.test");
        cut.FindAll("[data-testid=fila-buzon]").Should().ContainSingle("control positivo: el listado sigue");
    }

    [Fact]
    public void Con_URL_publica_hay_Anadir_buzon_y_no_hay_aviso()
    {
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] });

        cut.Find("[data-testid=anadir-buzon]").TextContent.Trim().Should().Be("Añadir buzón");
        cut.FindAll("[data-testid=aviso-conexion-no-disponible]").Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty("el cajón está cerrado hasta pulsar «Añadir buzón»");
    }

    /// <summary>
    /// El cajón trae los tres campos de propietario de Conexiones, con las Empresas y los Gestores CAE que carga al abrirse, y
    /// «Continuar en Microsoft» navega a la URL de conectar con página completa: la navegación mejorada de Blazor haría un fetch
    /// al endpoint y la CSP bloquearía el salto a Microsoft.
    /// </summary>
    [Fact]
    public void El_cajon_trae_los_campos_de_propietario_y_Continuar_en_Microsoft_navega_con_forceLoad()
    {
        var gestora = new CuentaVisibleEnRol(Guid.NewGuid(), "Marta Gestora", "marta@ejemplo.test", null);
        var mediador = new MediadorBuzones
        {
            Conexiones = [Buzon("cae")],
            Clientes = [new ClienteSelectorDto(Guid.NewGuid(), "Obras del Norte S.L.")],
        };
        var cut = Renderizar(mediador, gestores: [gestora]);

        cut.Find("[data-testid=anadir-buzon]").Click();

        var cajon = cut.Find("[role=dialog]");
        cajon.TextContent.Should().Contain("Empresa sin rol CAE (opcional)")
            .And.Contain("Buzón personal de un Gestor CAE (opcional)").And.Contain("Obras del Norte S.L.").And.Contain("Marta Gestora");
        cajon.QuerySelectorAll("select").Should().HaveCount(2);
        cajon.QuerySelectorAll("input[type=email]").Should().ContainSingle();

        cut.Find("[role=dialog]").QuerySelectorAll("select")[1].Change(gestora.Id.ToString());
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Continuar en Microsoft").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<BunitNavigationManager>().History.Should().ContainSingle());
        var navegacion = Services.GetRequiredService<BunitNavigationManager>().History.Should().ContainSingle().Subject;
        navegacion.Uri.Should().Be($"/integraciones/conectar-microsoft365?gestorPropietarioId={gestora.Id}");
        navegacion.Options.ForceLoad.Should().BeTrue("página completa: sin navegación mejorada hacia el endpoint OAuth");
        navegacion.State.Should().Be(NavigationState.Succeeded, "el guardián del cajón no detiene la salida que el propio cajón lanza");
        cut.FindComponents<PreguntaSalirSinGuardar>().Should().BeEmpty();
    }

    [Fact]
    public void Continuar_en_Microsoft_sin_eleccion_conecta_el_buzon_propio()
    {
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] });

        cut.Find("[data-testid=anadir-buzon]").Click();
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Continuar en Microsoft").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<BunitNavigationManager>().History.Should().ContainSingle());
        var navegacion = Services.GetRequiredService<BunitNavigationManager>().History.Should().ContainSingle().Subject;
        navegacion.Uri.Should().Be("/integraciones/conectar-microsoft365");
        navegacion.Options.ForceLoad.Should().BeTrue();
        navegacion.State.Should().Be(NavigationState.Succeeded);
    }

    /// <summary>El guardián del cajón es real: con una elección hecha y no enviada, «Cancelar» pregunta antes de perderla.</summary>
    [Fact]
    public void Cancelar_el_cajon_con_una_eleccion_hecha_pregunta_antes_de_descartarla()
    {
        var gestora = new CuentaVisibleEnRol(Guid.NewGuid(), "Marta Gestora", "marta@ejemplo.test", null);
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, gestores: [gestora]);

        cut.Find("[data-testid=anadir-buzon]").Click();
        cut.Find("[role=dialog]").QuerySelectorAll("select")[1].Change(gestora.Id.ToString());
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Cancelar").Click();

        cut.FindComponents<ConfirmarDescartarCambios>().Should().ContainSingle("una elección no enviada no se tira sin preguntar");
        Services.GetRequiredService<BunitNavigationManager>().History.Should().BeEmpty();
    }

    /// <summary>Un buzón con error muestra su último error en una franja <c>role=alert</c> y «Reconectar» lanza «Reactivar».</summary>
    [Fact]
    public void La_franja_de_error_muestra_el_ultimo_error_y_Reconectar_lanza_ReactivarConexion()
    {
        var conError = Buzon("cae", estado: EstadoConexionIntegracion.ConError, ultimoError: "Microsoft revocó el permiso del buzón.");
        var mediador = new MediadorBuzones { Conexiones = [conError, Buzon("proyectos")] };
        var cut = Renderizar(mediador);

        var franja = cut.Find("[data-testid=franja-error-buzon]");
        franja.GetAttribute("role").Should().Be("alert");
        franja.TextContent.Should().Contain("cae@ejemplo.test").And.Contain("Microsoft revocó el permiso del buzón.");
        cut.FindAll("[data-testid=franja-error-buzon]").Should().ContainSingle("solo el buzón con error tiene franja");

        cut.Find("[data-testid=reconectar-buzon]").Click();

        mediador.ComandosReactivar.Should().ContainSingle().Which.Should().Be(new ReactivarConexionCommand(conError.Id));
        cut.WaitForAssertion(() => Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Tono.Should().Be(TonoToast.Exito));
    }

    [Fact]
    public void Si_Reconectar_falla_se_avisa_con_el_error_del_servidor()
    {
        var conError = Buzon("cae", estado: EstadoConexionIntegracion.ConError, ultimoError: "Token caducado.");
        var mediador = new MediadorBuzones
        {
            Conexiones = [conError],
            FalloDelComando = Error.Crear("ReactivarConexion.SinComprobacion", "No pudimos comprobar el buzón en Microsoft."),
        };
        var cut = Renderizar(mediador);

        cut.Find("[data-testid=reconectar-buzon]").Click();

        mediador.ComandosReactivar.Should().ContainSingle();
        cut.WaitForAssertion(() => Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Tono.Should().Be(TonoToast.Error));
    }

    // ---- Comprobar acceso (L-57) ----

    /// <summary>
    /// Cada fila sin franja de error trae «Comprobar acceso» (fantasma, pequeño, con el nombre del buzón en el aria-label); la
    /// que ya ofrece «Reconectar» en su franja no, como en el lienzo. La columna no tiene rótulo visible.
    /// </summary>
    [Fact]
    public void Comprobar_acceso_sale_en_las_filas_sin_franja_de_error_y_no_en_la_que_ofrece_Reconectar()
    {
        var cut = Renderizar(new MediadorBuzones
        {
            Conexiones = [Buzon("cae"), Buzon("roto", estado: EstadoConexionIntegracion.ConError, ultimoError: "Token caducado."),
                Buzon("parado", estado: EstadoConexionIntegracion.Deshabilitada)]
        });

        var botones = cut.FindAll("[data-testid=comprobar-acceso-buzon]");
        botones.Should().HaveCount(2, "cae y parado sí; roto ya tiene «Reconectar»");
        botones.Should().OnlyContain(b => b.TextContent.Trim() == "Comprobar acceso" && b.ClassList.Contains("boton-fantasma"));
        botones[0].GetAttribute("aria-label").Should().Be("Comprobar acceso: cae");
        cut.FindAll("[data-testid=fila-buzon]").Single(f => f.TextContent.Contains("roto@"))
            .QuerySelector("[data-testid=comprobar-acceso-buzon]").Should().BeNull();
        cut.Find("th .sr-solo").TextContent.Trim().Should().Be("Acciones");
    }

    /// <summary>Quien no es Administrador ve el listado y no la acción: la guarda real es la de la consulta en el servidor.</summary>
    [Fact]
    public void Sin_rol_Administrador_no_hay_Comprobar_acceso()
    {
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, rol: Roles.GestorCae);

        cut.FindAll("[data-testid=fila-buzon]").Should().ContainSingle("control positivo: el listado sigue");
        cut.FindAll("[data-testid=comprobar-acceso-buzon]").Should().BeEmpty();
    }

    /// <summary>Los tres resultados con los textos del lienzo; la comprobación pide la consulta de esa conexión y no recarga la tabla.</summary>
    [Theory]
    [InlineData(ResultadoComprobacionAccesoBuzon.Ok, "TALVEG puede leer este buzón.", TonoToast.Exito)]
    [InlineData(ResultadoComprobacionAccesoBuzon.SinAcceso, "TALVEG no tiene acceso a este buzón. Pide a tu equipo de IT que lo incluya.", TonoToast.Error)]
    [InlineData(ResultadoComprobacionAccesoBuzon.Ocupado, "Este buzón ya está conectado en esta Empresa gestionada.", TonoToast.Error)]
    public void Comprobar_acceso_avisa_del_resultado_con_el_texto_del_lienzo(
        ResultadoComprobacionAccesoBuzon resultado, string texto, TonoToast tono)
    {
        var buzon = Buzon("cae");
        var mediador = new MediadorBuzones { Conexiones = [buzon, Buzon("proyectos")], ResultadoComprobacion = resultado };
        var cut = Renderizar(mediador);

        cut.FindAll("[data-testid=comprobar-acceso-buzon]")[0].Click();

        mediador.ConsultasComprobar.Should().ContainSingle().Which.Should().Be(new ComprobarAccesoBuzonQuery(buzon.Id));
        cut.WaitForAssertion(() => Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Should().Match<ToastMensaje>(m => m.Mensaje == texto && m.Tono == tono));
        mediador.ComandosReactivar.Should().BeEmpty("comprobar no reactiva ni cambia nada");
    }

    [Fact]
    public void Si_no_se_puede_comprobar_se_avisa_con_el_error_del_servidor()
    {
        var mediador = new MediadorBuzones
        {
            Conexiones = [Buzon("cae")],
            FalloDelComando = Error.Crear("Integraciones.Microsoft365.BuzonSinComprobar", "No pudimos comprobar el buzón en Microsoft. Inténtalo de nuevo."),
        };
        var cut = Renderizar(mediador);

        cut.Find("[data-testid=comprobar-acceso-buzon]").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Should().Match<ToastMensaje>(m => m.Tono == TonoToast.Error && m.Mensaje.Contains("No pudimos comprobar el buzón")));
    }

    /// <summary>
    /// La tabla lleva <c>tabla-buzones</c>: es el gancho de Buzones.razor.css que, en móvil, la hace contenedor de tamaño
    /// (<c>container-type: inline-size</c>) para acotar la franja de error con <c>100cqi</c> y que «Reconectar» no quede fuera de la
    /// vista. El CSS no se prueba en bUnit; sin la clase la regla no se aplicaría.
    /// </summary>
    [Fact]
    public void La_tabla_lleva_la_clase_que_acota_la_franja_de_error_en_movil()
    {
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae", estado: EstadoConexionIntegracion.ConError, ultimoError: "Token caducado.")] });

        cut.Find("table").ClassList.Should().Contain(["tabla-datos", "tabla-buzones"]);
        cut.Find("table.tabla-buzones [data-testid=franja-error-buzon]").Should().NotBeNull();
    }

    [Theory]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.Consulta)]
    [InlineData(null)]
    public void Sin_rol_Administrador_la_franja_se_ve_pero_no_hay_Reconectar(string? rol)
    {
        var cut = Renderizar(
            new MediadorBuzones { Conexiones = [Buzon("cae", estado: EstadoConexionIntegracion.ConError, ultimoError: "Token caducado.")] },
            rol);

        cut.Find("[data-testid=franja-error-buzon]").TextContent.Should().Contain("Token caducado.");
        cut.FindAll("[data-testid=reconectar-buzon]").Should().BeEmpty();
    }

    [Theory]
    [InlineData(EstadoConexionIntegracion.Habilitada, "Token caducado.")]
    [InlineData(EstadoConexionIntegracion.Deshabilitada, "Token caducado.")]
    [InlineData(EstadoConexionIntegracion.ConError, null)]
    [InlineData(EstadoConexionIntegracion.ConError, "   ")]
    public void Sin_estado_de_error_o_sin_ultimo_error_no_hay_franja(EstadoConexionIntegracion estado, string? ultimoError)
    {
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae", estado: estado, ultimoError: ultimoError)] });

        cut.FindAll("[data-testid=franja-error-buzon]").Should().BeEmpty();
        cut.FindAll("[data-testid=fila-buzon]").Should().ContainSingle("control positivo");
    }

    [Theory]
    [InlineData(EstadoConexionIntegracion.Habilitada, "estado-exito")]
    [InlineData(EstadoConexionIntegracion.ConError, "estado-peligro")]
    [InlineData(EstadoConexionIntegracion.Deshabilitada, "estado-neutro")]
    public void El_estado_sale_con_su_punto_y_su_clase(EstadoConexionIntegracion estadoConexion, string clase)
    {
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae", estado: estadoConexion, ultimoError: "x")] });

        var estado = cut.Find("[data-testid=estado-buzon]");
        estado.ClassList.Should().Contain("estado-buzon").And.Contain(clase);
        var punto = estado.QuerySelector(".punto-estado");
        punto.Should().NotBeNull();
        punto!.GetAttribute("aria-hidden").Should().Be("true");
        estado.TextContent.Trim().Should().NotBeEmpty("el punto acompaña al texto, no lo sustituye");
    }
}
