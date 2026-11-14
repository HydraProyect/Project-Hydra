using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.FijarAcuseAutomaticoBuzon;
using CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
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

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerConexionesIntegracionQuery => (IReadOnlyList<ConexionIntegracionListaDto>)Conexiones.ToList(),
                FijarAcuseAutomaticoBuzonCommand c => Aplicar(c),
                FijarResponsableBuzonCommand c => Aplicar(c),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)respuesta);
        }

        private Result Aplicar(FijarAcuseAutomaticoBuzonCommand comando)
        {
            ComandosAcuse.Add(comando);
            if (FalloDelComando is { } fallo) return Result.Fallo(fallo);
            Conexiones = Conexiones.Select(c => c.Id == comando.ConexionId ? c with { AcuseAutomaticoActivo = comando.Activo } : c).ToList();
            return Result.Exito();
        }

        private Result Aplicar(FijarResponsableBuzonCommand comando)
        {
            ComandosResponsable.Add(comando);
            if (FalloDelComando is { } fallo) return Result.Fallo(fallo);
            Conexiones = Conexiones.Select(c => c.Id == comando.ConexionId ? c with { ResponsableUsuarioId = comando.ResponsableUsuarioId } : c).ToList();
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
        IReadOnlyList<CuentaVisibleEnRol> gestores, Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> userManager,
        CaeManagerDbContext identidad)
        : DirectorioUsuariosTenant(
            userManager, new ConexionesGen2Tests.ContextoTenantsFalso(), new ConexionesGen2Tests.TenantActualFalso(),
            new PuertaAccesoDatos(), identidad, null!)
    {
        public override Task<IReadOnlyList<CuentaVisibleEnRol>> ObtenerVisiblesEnRolAsync(
            string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult(rol == Roles.GestorCae ? gestores : []);
    }

    private static DirectorioUsuariosTenant Directorio(IReadOnlyList<CuentaVisibleEnRol> gestores)
    {
        var identidad = new CaeManagerDbContext(new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(BuzonesAcuseAutomaticoTests)), new ConexionesGen2Tests.TenantActualFalso());
        var usuarios = new Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>(
            new ConexionesGen2Tests.AlmacenUsuariosFalso(), null!, null!, null!, null!, null!, null!, null!, null!);
        return new DirectorioConGestores(gestores, usuarios, identidad);
    }

    private static ConexionIntegracionListaDto Buzon(string nombre, bool acuse = false, Guid? propietario = null, Guid? responsable = null) =>
        new(Guid.NewGuid(), $"{nombre}@ejemplo.test", nombre, null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow,
            propietario, null, responsable, acuse, EsBuzonCompartido: propietario is null);

    private IRenderedComponent<Buzones> Renderizar(
        MediadorBuzones mediador, string? rol = Roles.Administrador, IReadOnlyList<CuentaVisibleEnRol>? gestores = null)
    {
        var autorizacion = AddAuthorization();
        if (rol is not null) autorizacion.SetAuthorized("usuario").SetRoles(rol);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped(_ => Directorio(gestores ?? []));
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
        modal.Should().Contain("Solo se envía en el primer correo de una conversación nueva.")
            .And.Contain("respuestas automáticas").And.Contain("listas de distribución").And.Contain("no-reply");
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
    public void Quien_no_es_Administrador_ve_el_estado_del_acuse_sin_interruptor_ni_selector(string? rol)
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
            .Should().Equal("Sin responsable", "Lucía Sanz", "Andrés Rey");
        selector.QuerySelector("option[selected]")!.TextContent.Trim().Should().Be("Lucía Sanz");

        selector.Change(andres.Id.ToString());

        mediador.ComandosResponsable.Should().ContainSingle().Which.Should().Be(new FijarResponsableBuzonCommand(buzon.Id, andres.Id));
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid=selector-responsable] option[selected]").TextContent.Trim().Should().Be("Andrés Rey"));
    }

    [Fact]
    public void Sin_buzones_la_pagina_lo_dice_y_no_dibuja_la_tabla()
    {
        var cut = Renderizar(new MediadorBuzones());

        cut.Markup.Should().Contain("Aún no hay buzones conectados.");
        cut.FindAll("table").Should().BeEmpty();
    }
}
