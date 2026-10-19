using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.ApoyoCartera.Commands;
using CaeManager.Application.Operaciones.ApoyoCartera.Queries;
using CaeManager.Application.Usuarios.Queries.ObtenerOperadoresCaeDeMiTenant;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Clientes.Components;
using CaeManager.Web.Features.IncorporacionCartera.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Fila «Gestor CAE» de Cliente 360 (<see cref="GestorCaeCliente"/>) con «Dar acceso» y «Desasignarme» (maqueta
/// del 17-10). Por rol: quién ve la fila, quién ve las acciones y qué Command sale.
///
/// <para>
/// <b>No observa</b> la autorización: qué personas devuelven las consultas a cada rol, quién puede proponer un
/// apoyo y quién desasignarse lo deciden los handlers (<c>PropuestaApoyoCarteraTests</c>,
/// <c>FinDeApoyoCarteraTests</c>) y la RLS. Aquí el mediador es falso y cada caso fija lo que esas consultas
/// devolverían a ese rol.
/// </para>
/// </summary>
public class GestorCaeClienteTests : BunitContext
{
    private static readonly Guid Yo = Guid.NewGuid();
    private static readonly Guid Tenant = Guid.NewGuid();

    private readonly MediatorFalso _mediador = new();

    public GestorCaeClienteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioActualFalso(Yo));
        Services.AddScoped<ITenantActual>(_ => new TenantFijo(Tenant));
    }

    private static PersonaConCartera Persona(Guid id, string nombre, string rol = Roles.GestorCae) => new(id, nombre, rol, null);

    private static CarterasDeOperacion Operacion(PersonaConCartera? principal, params PersonaConCartera[] apoyos) =>
        new(Guid.NewGuid(), Tenant, "Talleres Norte Demo", principal, apoyos);

    private static ApoyoDeCarteraDto ApoyoMio(CarterasDeOperacion operacion) =>
        new(Guid.NewGuid(), operacion.TenantId, operacion.NombreTenant, operacion.AsignacionOperacionId,
            Yo, "Yo", operacion.Principal!.UsuarioId, operacion.Principal.Nombre, null);

    private int _avisosDeCarga;

    private IRenderedComponent<GestorCaeCliente> Renderizar(string rol, bool cargar = true, Action? alDesasignarme = null)
    {
        this.ConRolDeEscritura(rol);
        return Render<GestorCaeCliente>(p => p
            .Add(c => c.Cargar, cargar)
            .Add(c => c.AlCargar, () => _avisosDeCarga++)
            .Add(c => c.AlDesasignarme, alDesasignarme ?? (() => { })));
    }

    private static string Personas(IRenderedComponent<GestorCaeCliente> cut) =>
        cut.Find("[data-gestor-cae-cliente-personas]").TextContent.Trim();

    private static List<string> Botones(IRenderedComponent<GestorCaeCliente> cut) =>
        cut.FindAll("[data-gestor-cae-cliente-acciones] button").Select(b => b.TextContent.Trim()).ToList();

    [Fact]
    public void El_principal_ve_la_fila_con_el_principal_primero_y_Dar_acceso_sobre_el_Tenant_activo()
    {
        var operacion = Operacion(Persona(Yo, "Laura P."), Persona(Guid.NewGuid(), "Andrés M."));
        _mediador.Operaciones = [operacion];

        var cut = Renderizar(Roles.GestorCae);

        cut.Find(".dato-rejilla-etiqueta").TextContent.Should().Be("Gestor CAE");
        Personas(cut).Should().Be("Laura P., Andrés M.", "el principal va antes que el apoyo, aunque el orden alfabético diga otra cosa");
        Botones(cut).Should().Equal(["+ Dar acceso"], "soy el principal y no tengo apoyo propio que soltar");
        cut.FindComponent<PanelDarAcceso>().Instance.TenantId.Should().Be(Tenant, "las acciones son sobre el Tenant activo y nada más");
        _mediador.Enviadas.OfType<ObtenerPersonasConCarteraQuery>().Should().OnlyContain(q => q.TenantId == Tenant);
        _mediador.Enviadas.OfType<ObtenerOperadoresCaeDeMiTenantQuery>().Should().BeEmpty("desde el Operador CAE no hace falta la lectura del propietario");
    }

    [Fact]
    public async Task El_Gestor_CAE_de_apoyo_se_desasigna_tras_confirmar_y_la_pagina_se_entera()
    {
        var operacion = Operacion(Persona(Guid.NewGuid(), "Laura P."), Persona(Yo, "Yo"));
        var mio = ApoyoMio(operacion);
        _mediador.Operaciones = [operacion];
        _mediador.Apoyos = new ApoyosDeCarteraDto([mio], [], []);
        var desasignaciones = 0;
        var cut = Renderizar(Roles.GestorCae, alDesasignarme: () => desasignaciones++);

        Botones(cut).Should().Equal(["Desasignarme"], "no soy el principal: no doy acceso; sí suelto mi apoyo");
        var lecturasAntes = _mediador.Enviadas.OfType<ObtenerPersonasConCarteraQuery>().Count();
        await cut.InvokeAsync(() => cut.Find("[data-desasignarme-cabecera]").Click());
        _mediador.Enviadas.OfType<DesasignarmeDeApoyoCommand>().Should().BeEmpty("primero pregunta");
        var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Visible);
        dialogo.Instance.Titulo.Should().NotBeNullOrWhiteSpace();
        await cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());

        _mediador.Enviadas.OfType<DesasignarmeDeApoyoCommand>().Should().ContainSingle()
            .Which.Should().Be(new DesasignarmeDeApoyoCommand(mio.PropuestaId), "solo viaja la propuesta: ni usuario ni Tenant");
        desasignaciones.Should().Be(1);
        _mediador.Enviadas.OfType<ObtenerPersonasConCarteraQuery>().Count().Should().BeGreaterThan(lecturasAntes, "tras la acción se relee la fila");
    }

    [Fact]
    public void Un_Gestor_CAE_sin_marca_ni_apoyo_propio_ve_la_fila_sin_acciones()
    {
        _mediador.Operaciones = [Operacion(Persona(Guid.NewGuid(), "Laura P."))];

        var cut = Renderizar(Roles.GestorCae);

        Personas(cut).Should().Be("Laura P.");
        Botones(cut).Should().BeEmpty("ni es el principal ni tiene un apoyo propio");
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public void Sin_rol_de_escritura_la_fila_sale_sin_acciones_ni_PanelDarAcceso(string rol)
    {
        // Aunque la consulta devolviera personas (hoy no lo hace para estos roles), las acciones no se ofrecen.
        _mediador.Operaciones = [Operacion(Persona(Yo, "Laura P."))];

        var cut = Renderizar(rol);

        Personas(cut).Should().Be("Laura P.", "control: la fila está");
        cut.FindAll("button").Should().BeEmpty();
        cut.FindComponents<PanelDarAcceso>().Should().BeEmpty("SoloConEscritura no lo monta");
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public void Si_las_consultas_no_le_devuelven_a_nadie_la_fila_no_se_pinta(string rol)
    {
        var cut = Renderizar(rol);

        cut.Markup.Trim().Should().BeEmpty();
        _mediador.Enviadas.Select(e => e.GetType()).Should().Equal(
            [typeof(ObtenerPersonasConCarteraQuery), typeof(ObtenerOperadoresCaeDeMiTenantQuery)],
            "control: preguntó por las dos miradas y ninguna le dio a nadie");
    }

    [Fact]
    public void El_Administrador_del_Tenant_propietario_ve_quien_lo_gestiona_sin_acciones()
    {
        _mediador.OperacionesDelPropietario =
            [Operacion(Persona(Guid.NewGuid(), "Laura P."), Persona(Guid.NewGuid(), "Andrés M.")) with { NombreOperador = "Operador Demo" }];

        var cut = Renderizar(Roles.Administrador);

        Personas(cut).Should().Be("Laura P., Andrés M.");
        cut.FindComponents<PanelDarAcceso>().Should().BeEmpty("dar acceso y desasignarse son del Operador CAE, no del Tenant propietario");
        _mediador.Enviadas.OfType<ObtenerApoyosDeCarteraQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Las_carteras_de_otro_Tenant_no_salen_aunque_la_consulta_las_devolviera()
    {
        _mediador.Operaciones = [new CarterasDeOperacion(Guid.NewGuid(), Guid.NewGuid(), "Otro Tenant", Persona(Yo, "Laura P."), [])];

        var cut = Renderizar(Roles.GestorCae);

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Hasta_que_la_pagina_no_termina_su_carga_no_pregunta_nada()
    {
        _mediador.Operaciones = [Operacion(Persona(Yo, "Laura P."))];

        var cut = Renderizar(Roles.GestorCae, cargar: false);

        _mediador.Enviadas.Should().BeEmpty("las consultas comparten el DbContext del circuito con las de la página");
        cut.Markup.Trim().Should().BeEmpty();

        cut.Render(p => p.Add(c => c.Cargar, true));

        Personas(cut).Should().Be("Laura P.");
    }

    [Fact]
    public void Un_fallo_de_lectura_se_dice_en_la_fila_y_no_ofrece_acciones()
    {
        _mediador.FallaLectura = true;

        var cut = Renderizar(Roles.GestorCae);

        cut.Find(".gestor-cae-cliente-error").TextContent.Should().Be("No pudimos cargarlo");
        cut.FindComponents<PanelDarAcceso>().Should().BeEmpty();
    }

    [Fact]
    public void El_Administrador_del_Operador_CAE_sin_cartera_propia_no_recibe_un_hueco_de_acciones_vacio()
    {
        _mediador.Operaciones = [Operacion(Persona(Guid.NewGuid(), "Laura P."), Persona(Guid.NewGuid(), "Andrés M."))];

        var cut = Renderizar(Roles.Administrador);

        Personas(cut).Should().Be("Laura P., Andrés M.", "control: ve quién lo gestiona");
        cut.FindAll("[data-gestor-cae-cliente-acciones]").Should().BeEmpty("no es el principal ni un apoyo: no hay acciones que ofrecerle");
        cut.FindComponents<PanelDarAcceso>().Should().BeEmpty();
        _avisosDeCarga.Should().Be(1, "sin panel, la fila avisa a la página nada más leer");
    }

    [Fact]
    public void Quien_hoy_es_el_principal_no_ve_Desasignarme_aunque_le_quede_un_apoyo_de_esa_operacion()
    {
        // Entró como apoyo y luego recibió la marca: soltarla es un relevo, no un fin de apoyo.
        var operacion = Operacion(Persona(Yo, "Laura P."));
        _mediador.Operaciones = [operacion];
        _mediador.Apoyos = new ApoyosDeCarteraDto([ApoyoMio(operacion)], [], []);

        var cut = Renderizar(Roles.GestorCae);

        Botones(cut).Should().Equal(["+ Dar acceso"]);
        cut.FindAll("[data-desasignarme-cabecera]").Should().BeEmpty();
    }

    [Fact]
    public void La_mirada_del_Tenant_propietario_tambien_se_filtra_por_el_Tenant_activo()
    {
        _mediador.OperacionesDelPropietario = [new CarterasDeOperacion(Guid.NewGuid(), Guid.NewGuid(), "Otro Tenant", Persona(Guid.NewGuid(), "Laura P."), [])];

        var cut = Renderizar(Roles.Administrador);

        _mediador.Enviadas.OfType<ObtenerOperadoresCaeDeMiTenantQuery>().Should().ContainSingle("control: llegó a la segunda consulta");
        cut.Markup.Trim().Should().BeEmpty("las carteras de otro Tenant no se pintan aunque la consulta las devolviera");
    }

    [Fact]
    public void Si_falla_la_lectura_del_panel_la_fila_sigue_dice_el_error_y_avisa_a_la_pagina()
    {
        _mediador.Operaciones = [Operacion(Persona(Yo, "Laura P."))];
        _mediador.FallaLecturaDelPanel = true;

        var cut = Renderizar(Roles.GestorCae);

        Personas(cut).Should().Be("Laura P.", "el fallo del panel no tumba la fila");
        cut.Find("[data-dar-acceso-error]").TextContent.Should().Be("No pudimos cargar los accesos.");
        Botones(cut).Should().BeEmpty("sin lectura, el panel no ofrece acciones");
        _avisosDeCarga.Should().Be(1, "también con error el panel termina su primera lectura y la página puede seguir");
    }

    [Fact]
    public void Con_acciones_la_pagina_recibe_un_solo_aviso_y_despues_de_la_lectura_del_panel()
    {
        _mediador.Operaciones = [Operacion(Persona(Yo, "Laura P."))];

        var cut = Renderizar(Roles.GestorCae);

        _avisosDeCarga.Should().Be(1);
        _mediador.Enviadas.OfType<ObtenerApoyosDeCarteraQuery>().Should().ContainSingle("control: el panel leyó antes del aviso");
        cut.Render(p => p.Add(c => c.Cargar, true));
        _avisosDeCarga.Should().Be(1, "volver a pasar el mismo parámetro no es una nueva habilitación");
    }

    private sealed class TenantFijo(Guid? id) : ITenantActual
    {
        public Guid? TenantId => id;
    }

    private sealed class UsuarioActualFalso(Guid id) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(id);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(Roles.GestorCae);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public IReadOnlyList<CarterasDeOperacion> Operaciones { get; set; } = [];
        public IReadOnlyList<CarterasDeOperacion> OperacionesDelPropietario { get; set; } = [];
        public ApoyosDeCarteraDto Apoyos { get; set; } = ApoyosDeCarteraDto.Vacio;
        public bool FallaLectura { get; set; }
        public bool FallaLecturaDelPanel { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (FallaLectura && request is ObtenerPersonasConCarteraQuery)
                throw new InvalidOperationException("Fallo simulado de lectura.");
            if (FallaLecturaDelPanel && request is ObtenerApoyosDeCarteraQuery)
                throw new InvalidOperationException("Simula la colisión sobre el DbContext del circuito.");

            object respuesta = request switch
            {
                ObtenerPersonasConCarteraQuery => Operaciones,
                ObtenerOperadoresCaeDeMiTenantQuery => OperacionesDelPropietario,
                ObtenerApoyosDeCarteraQuery => Apoyos,
                DesasignarmeDeApoyoCommand fin => Terminar(fin.PropuestaId),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta);
        }

        private Result Terminar(Guid propuestaId)
        {
            Apoyos = new ApoyosDeCarteraDto(Apoyos.Mios.Where(a => a.PropuestaId != propuestaId).ToList(), [], []);
            return Result.Exito();
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
