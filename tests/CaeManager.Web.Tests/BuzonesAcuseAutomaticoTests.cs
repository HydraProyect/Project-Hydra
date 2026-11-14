using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.FijarAcuseAutomaticoBuzon;
using CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Integraciones.Queries.ObtenerResponsablesElegiblesBuzones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Integraciones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    }

    private sealed class MediadorBuzones : IMediator
    {
        public List<ConexionIntegracionListaDto> Conexiones { get; set; } = [];
        public List<FijarAcuseAutomaticoBuzonCommand> ComandosAcuse { get; } = [];
        public List<FijarResponsableBuzonCommand> ComandosResponsable { get; } = [];

        /// <summary>Si se pone, el comando falla con este error y no cambia nada (lo que haría el handler real).</summary>
        public Error? FalloDelComando { get; set; }

        /// <summary>Si se pone, el comando guarda el cambio y después lanza esta excepción (el handler real puede caer tras confirmar).</summary>
        public Exception? ExcepcionTrasAplicar { get; set; }

        /// <summary>Las cuentas que el servidor da por elegibles; null = todas las que se le preguntan.</summary>
        public HashSet<Guid>? Elegibles { get; set; }

        public List<ObtenerResponsablesElegiblesBuzonesQuery> ConsultasElegibles { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerConexionesIntegracionQuery => (IReadOnlyList<ConexionIntegracionListaDto>)Conexiones.ToList(),
                ObtenerResponsablesElegiblesBuzonesQuery q => Evaluar(q),
                FijarAcuseAutomaticoBuzonCommand c => Aplicar(c),
                FijarResponsableBuzonCommand c => Aplicar(c),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)respuesta);
        }

        private IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> Evaluar(ObtenerResponsablesElegiblesBuzonesQuery consulta)
        {
            ConsultasElegibles.Add(consulta);
            return consulta.ConexionIds.ToDictionary(
                id => id,
                id => (IReadOnlySet<Guid>)consulta.CandidatoIds.Where(c => Elegibles is null || Elegibles.Contains(c)).ToHashSet());
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
    }

    private static DirectorioUsuariosTenant Directorio(IReadOnlyDictionary<string, IReadOnlyList<CuentaVisibleEnRol>> cuentasPorRol)
    {
        var identidad = new CaeManagerDbContext(new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(BuzonesAcuseAutomaticoTests)), new ConexionesGen2Tests.TenantActualFalso());
        var usuarios = new Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>(
            new ConexionesGen2Tests.AlmacenUsuariosFalso(), null!, null!, null!, null!, null!, null!, null!, null!);
        return new DirectorioConGestores(cuentasPorRol, usuarios, identidad);
    }

    private static ConexionIntegracionListaDto Buzon(string nombre, bool acuse = false, Guid? propietario = null, Guid? responsable = null) =>
        new(Guid.NewGuid(), $"{nombre}@ejemplo.test", nombre, null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow,
            propietario, null, responsable, acuse, EsBuzonCompartido: propietario is null);

    private IRenderedComponent<Buzones> Renderizar(
        MediadorBuzones mediador, string? rol = Roles.Administrador, IReadOnlyList<CuentaVisibleEnRol>? gestores = null,
        IReadOnlyDictionary<string, IReadOnlyList<CuentaVisibleEnRol>>? cuentasPorRol = null)
    {
        var autorizacion = AddAuthorization();
        if (rol is not null) autorizacion.SetAuthorized("usuario").SetRoles(rol);
        Services.AddScoped<IMediator>(_ => mediador);
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
            .Which.Mensaje.Should().StartWith("Acuse automático activado en cae.");
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
    /// todos los roles que esa regla puede aceptar como universo. Administrador, Dirección CAE y Coordinador CAE aparecen si
    /// el servidor los da por elegibles; un Gestor CAE que el servidor descarta (sin cartera) no aparece.
    /// </summary>
    [Fact]
    public void El_selector_de_responsable_ofrece_exactamente_las_cuentas_que_el_servidor_da_por_elegibles()
    {
        var admin = new CuentaVisibleEnRol(Guid.NewGuid(), "Ana Admin", "ana@ejemplo.test", null);
        var direccion = new CuentaVisibleEnRol(Guid.NewGuid(), "Berta Dirección", "berta@ejemplo.test", null);
        var coordinador = new CuentaVisibleEnRol(Guid.NewGuid(), "Carla Coordinadora", "carla@ejemplo.test", null);
        var gestorConCartera = new CuentaVisibleEnRol(Guid.NewGuid(), "Diego Gestor", "diego@ejemplo.test", null);
        var gestorSinCartera = new CuentaVisibleEnRol(Guid.NewGuid(), "Elena Sincartera", "elena@ejemplo.test", null);
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
            [Roles.Consulta] = [new CuentaVisibleEnRol(Guid.NewGuid(), "Fran Consulta", "fran@ejemplo.test", null)]
        });

        cut.Find("[data-testid=selector-responsable]").QuerySelectorAll("option").Select(o => o.TextContent.Trim())
            .Should().Equal("Sin responsable", "Ana Admin", "Berta Dirección", "Carla Coordinadora", "Diego Gestor");
        var consulta = mediador.ConsultasElegibles.Should().ContainSingle().Subject;
        consulta.ConexionIds.Should().Equal(buzon.Id);
        consulta.CandidatoIds.Should().BeEquivalentTo(
            [admin.Id, direccion.Id, coordinador.Id, gestorConCartera.Id, gestorSinCartera.Id],
            "el universo son los cuatro roles que la regla puede aceptar, y Consulta no entra");
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
        cut.FindAll("table").Should().BeEmpty();
    }
}
