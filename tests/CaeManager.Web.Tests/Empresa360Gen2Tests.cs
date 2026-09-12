using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.Empresas.Commands.EliminarEmpresa;
using CaeManager.Application.Empresas.Commands.GuardarCredencialAccesoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresaSinContrasena;
using CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Empresas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>Las esperas retenidas se liberan desde InvokeAsync para observar su continuación.</summary>
public class Empresa360Gen2Tests : BunitContext
{
    public Empresa360Gen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediadorFalso : IMediator
    {
        public Dictionary<Guid, EmpresaDetalleDto?> Detalles { get; } = [];
        public Dictionary<Guid, int?> Cumplimientos { get; } = [];
        public Dictionary<Guid, IReadOnlyList<ClienteDeEmpresaDto>> Clientes { get; } = [];
        public Result Baja { get; set; } = Result.Exito();
        public Result Edicion { get; set; } = Result.Exito();
        public Result Credenciales { get; set; } = Result.Exito();
        public Func<object, Task?>? Retener { get; set; }
        public Func<object, Exception?>? Fallar { get; set; }
        public List<object> Enviadas { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request); Tokens.Add(cancellationToken);
            if (Retener?.Invoke(request) is { } espera) await espera;
            if (Fallar?.Invoke(request) is { } error) throw error;
            // El switch tiene ramas de tipos distintos, asi que se unifica en
            // object? y se convierte una sola vez: sin esto, CS0029 por rama.
            object? valor = request switch
            {
                ObtenerEmpresaPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
                ObtenerCumplimientoEmpresaQuery q => Cumplimientos.GetValueOrDefault(q.EmpresaId),
                ObtenerClientesDeEmpresaQuery q => Clientes.GetValueOrDefault(q.EmpresaId) ?? [],
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[],
                ObtenerCredencialAccesoEmpresaSinContrasenaQuery => null,
                EliminarEmpresaCommand => Baja,
                EditarEmpresaCommand => Edicion,
                GuardarCredencialAccesoEmpresaCommand => Credenciales,
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return (T)valor!;
        }
        public Task Send<T>(T request, CancellationToken ct = default) where T : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> r, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish(object n, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<T>(T n, CancellationToken ct = default) where T : INotification => Task.CompletedTask;
    }

    private static EmpresaDetalleDto Detalle(Guid id, string nombre) => new(id, nombre, "B-50", DateTime.UtcNow, [], Guid.NewGuid());
    private MediadorFalso Registrar(MediadorFalso m) { Services.AddScoped<IMediator>(_ => m); Services.AddScoped<ToastService>(); Services.AddScoped<ContextWorkspaceService>(); return m; }
    private IRenderedComponent<EmpresaWorkspacePanel> Renderizar(Guid id, string pestana = "informacion") => Render<EmpresaWorkspacePanel>(p => p.Add(x => x.EntidadId, id).Add(x => x.PestanaActiva, pestana).Add(x => x.PestanaActivaChanged, EventCallback.Factory.Create<string>(this, _ => { })));
    private static IElement Boton(IRenderedComponent<EmpresaWorkspacePanel> cut, string texto) => cut.FindAll("button").Where(x => x.TextContent.Trim() == texto).Should().ContainSingle().Subject;
    private static IElement ConfirmarBaja(IRenderedComponent<EmpresaWorkspacePanel> cut) => cut.Find("[role=dialog] .modal-pie").QuerySelectorAll("button").Where(x => x.TextContent.Trim() == "Eliminar").Should().ContainSingle().Subject;
    private static DialogoConfirmacion DialogoBaja(IRenderedComponent<EmpresaWorkspacePanel> cut) => cut.FindComponents<DialogoConfirmacion>().Single(x => x.Instance.Titulo.EndsWith("esta empresa?")).Instance;
    private static IElement Control(IRenderedComponent<EmpresaWorkspacePanel> cut, string etiqueta) { var id = cut.FindAll("label").Where(x => x.TextContent.Trim() == etiqueta).Should().ContainSingle().Subject.GetAttribute("for"); return cut.Find($"#{id}"); }
    private IReadOnlyList<ToastMensaje> Toasts => Services.GetRequiredService<ToastService>().Mensajes;

    [Fact]
    public void La_pestana_clientes_empresariales_cuenta_la_lista_que_carga_el_panel()
    {
        var id = Guid.NewGuid(); var m = Registrar(new MediadorFalso());
        m.Detalles[id] = Detalle(id, "Montajes Ebro S.L."); m.Cumplimientos[id] = 80;
        m.Clientes[id] = [new(Guid.NewGuid(), "Refrielectric S.A.", "A-01"), new(Guid.NewGuid(), "Ibertec S.A.", "A-02")];
        var cut = Renderizar(id, "clientes");
        cut.FindAll("[role=tab]").Single(x => x.TextContent.Contains("Clientes empresariales")).TextContent.Should().Contain("(2)");
        cut.Markup.Should().Contain("2 clientes empresariales");
    }

    [Fact]
    public void El_recuento_de_clientes_empresariales_de_la_cabecera_solo_se_muestra_en_su_pestana()
    {
        var id = Guid.NewGuid(); var m = Registrar(new MediadorFalso());
        m.Detalles[id] = Detalle(id, "Montajes Ebro S.L."); m.Cumplimientos[id] = 80;
        m.Clientes[id] = [new(Guid.NewGuid(), "Refrielectric S.A.", "A-01")];
        var cut = Renderizar(id, "clientes");

        cut.FindAll(".recuentos-empresa-360").Should().ContainSingle("la pestaña de clientes empresariales está activa")
            .Which.TextContent.Trim().Should().Be("1 cliente empresarial");

        cut.Render(p => p.Add(x => x.EntidadId, id).Add(x => x.PestanaActiva, "informacion"));
        cut.FindAll(".recuentos-empresa-360").Should().BeEmpty("el recuento de clientes empresariales no pertenece a las demás pestañas");
    }

    [Fact]
    public async Task Las_etiquetas_de_credenciales_usan_la_terminologia_canonica()
    {
        var id = Guid.NewGuid(); var m = Registrar(new MediadorFalso());
        m.Detalles[id] = Detalle(id, "Montajes Ebro S.L."); m.Cumplimientos[id] = 80;
        var cut = Renderizar(id);

        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());

        cut.FindAll("label").Select(x => x.TextContent.Trim()).Should().Contain("Credenciales de acceso a Plataforma CAE")
            .And.Contain("Empresa / Cliente empresarial / Proveedor");
        cut.Markup.Should().NotContain("Credenciales de acceso a plataforma externa")
            .And.NotContain("Empresa / Cliente / Proveedor");
    }

    [Fact]
    public async Task Retirar_el_panel_cancela_todas_sus_consultas()
    {
        var id = Guid.NewGuid(); var m = Registrar(new MediadorFalso()); m.Detalles[id] = Detalle(id, "Montajes Ebro S.L."); m.Cumplimientos[id] = 80;
        var cut = Renderizar(id);
        m.Tokens.Should().HaveCount(2).And.OnlyContain(x => x.CanBeCanceled && !x.IsCancellationRequested);
        await DisposeComponentsAsync();
        m.Tokens.Should().OnlyContain(x => x.IsCancellationRequested, "DisposeComponentsAsync retira el panel y cancela su ciclo");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_baja_tardia_anuncia_siempre_la_empresa_original_incluso_si_falla(bool falla)
    {
        const string aNombre = "Montajes Ebro S.L."; const string bNombre = "Ibertec S.A."; const string motivo = "Todavía tiene trabajadores.";
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var espera = new TaskCompletionSource();
        var m = Registrar(new MediadorFalso { Retener = x => x is EliminarEmpresaCommand ? espera.Task : null, Baja = falla ? Result.Fallo(Error.Crear("Empresa.TieneTrabajadores", motivo)) : Result.Exito() });
        m.Detalles[a] = Detalle(a, aNombre); m.Detalles[b] = Detalle(b, bNombre); m.Cumplimientos[a] = m.Cumplimientos[b] = 80;
        var cut = Renderizar(a); await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        var confirmar = ConfirmarBaja(cut).ClickAsync(new MouseEventArgs());
        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
        cut.Find(".titulo-empresa-360").TextContent.Trim().Should().Be(bNombre, "la ficha ya es B");
        await cut.InvokeAsync(espera.SetResult); await confirmar;
        var toast = Toasts.Should().ContainSingle().Subject;
        toast.Mensaje.Should().Contain(aNombre).And.NotContain(bNombre); toast.Tono.Should().Be(falla ? TonoToast.Error : TonoToast.Exito);
        if (falla) toast.Mensaje.Should().Contain(motivo);
    }

    [Fact]
    public async Task Dos_invocaciones_del_OnConfirmar_del_hijo_mandan_una_sola_baja()
    {
        var id = Guid.NewGuid(); var espera = new TaskCompletionSource(); var m = Registrar(new MediadorFalso { Retener = x => x is EliminarEmpresaCommand ? espera.Task : null });
        m.Detalles[id] = Detalle(id, "Montajes Ebro S.L."); m.Cumplimientos[id] = 80; var cut = Renderizar(id);
        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs()); var dialogo = DialogoBaja(cut);
        var primero = cut.InvokeAsync(() => dialogo.OnConfirmar.InvokeAsync()); var segundo = cut.InvokeAsync(() => dialogo.OnConfirmar.InvokeAsync());
        m.Enviadas.OfType<EliminarEmpresaCommand>().Should().ContainSingle("la guarda del panel también cubre llamadores que no son el botón del diálogo");
        await cut.InvokeAsync(espera.SetResult); await primero; await segundo;
        m.Enviadas.OfType<EliminarEmpresaCommand>().Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_rechazo_de_edicion_va_al_formulario_actual_o_al_aviso_global_con_su_empresa(bool cambiarAB)
    {
        const string aNombre = "Montajes Ebro S.L."; const string bNombre = "Ibertec S.A."; const string motivo = "Ya existe otra empresa con ese CIF.";
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var espera = new TaskCompletionSource();
        var m = Registrar(new MediadorFalso { Retener = x => cambiarAB && x is EditarEmpresaCommand ? espera.Task : null, Edicion = Result.Fallo(Error.Crear("Empresa.CifDuplicado", motivo)) });
        m.Detalles[a] = Detalle(a, aNombre); m.Detalles[b] = Detalle(b, bNombre); m.Cumplimientos[a] = m.Cumplimientos[b] = 80;
        var cut = Renderizar(a); await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs()); await Control(cut, "Razón social").InputAsync(new ChangeEventArgs { Value = "Montajes Norte" });
        var guardar = Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());
        if (cambiarAB) { cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion")); await cut.InvokeAsync(espera.SetResult); }
        await guardar; Toasts.Should().NotContain(x => x.Tono == TonoToast.Exito, "un Result fallido no es un guardado");
        if (!cambiarAB) { cut.FindAll(".alerta-formulario[role=alert]").Select(x => x.TextContent.Trim()).Should().Equal([motivo]); Toasts.Should().BeEmpty("el formulario de A ya identifica la ficha"); }
        else { cut.Find(".titulo-empresa-360").TextContent.Trim().Should().Be(bNombre); cut.FindAll(".alerta-formulario[role=alert]").Should().BeEmpty(); Toasts.Should().ContainSingle(x => x.Tono == TonoToast.Error && x.Mensaje.Contains(aNombre) && x.Mensaje.Contains(motivo)); }
    }

    [Fact]
    public async Task El_rechazo_de_credenciales_vigente_se_muestra_en_su_formulario()
    {
        const string motivo = "La URL no es válida.";
        var id = Guid.NewGuid(); var m = Registrar(new MediadorFalso { Credenciales = Result.Fallo(Error.Crear("Empresa.UrlInvalida", motivo)) });
        m.Detalles[id] = Detalle(id, "Montajes Ebro S.L."); m.Cumplimientos[id] = 80;
        var cut = Renderizar(id); await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());

        await Boton(cut, "Guardar credenciales").ClickAsync(new MouseEventArgs());

        cut.FindAll(".alerta-formulario[role=alert]").Should().ContainSingle("la edición de la empresa sigue siendo vigente")
            .Which.TextContent.Trim().Should().Be(motivo);
        Toasts.Should().BeEmpty("el formulario vigente ya identifica la empresa");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public async Task El_fallo_tardio_al_guardar_identidad_o_credenciales_se_anuncia_con_la_empresa_original(bool credenciales, int claseFallo)
    {
        const string aNombre = "Montajes Ebro S.L."; const string bNombre = "Ibertec S.A."; const string motivo = "El dato ya existe.";
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var espera = new TaskCompletionSource();
        Func<object, bool> esComando = x => credenciales ? x is GuardarCredencialAccesoEmpresaCommand : x is EditarEmpresaCommand;
        Exception? excepcion = claseFallo switch
        {
            1 => new FluentValidation.ValidationException("Validación rechazada."),
            2 => new InvalidOperationException(),
            _ => null
        };
        var m = Registrar(new MediadorFalso
        {
            Retener = x => esComando(x) ? espera.Task : null,
            Fallar = x => esComando(x) ? excepcion : null,
            Edicion = Result.Fallo(Error.Crear("Empresa.DatoDuplicado", motivo)),
            Credenciales = Result.Fallo(Error.Crear("Empresa.CredencialInvalida", motivo))
        });
        m.Detalles[a] = Detalle(a, aNombre); m.Detalles[b] = Detalle(b, bNombre); m.Cumplimientos[a] = m.Cumplimientos[b] = 80;
        var cut = Renderizar(a); await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        var guardar = Boton(cut, credenciales ? "Guardar credenciales" : "Guardar").ClickAsync(new MouseEventArgs());
        m.Enviadas.Where(esComando).Should().ContainSingle("el guardado de A quedó retenido antes de cambiar de ficha");

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
        cut.Find(".titulo-empresa-360").TextContent.Trim().Should().Be(bNombre, "la ficha ya es B");
        await cut.InvokeAsync(espera.SetResult); await guardar;

        var toast = Toasts.Should().ContainSingle(x => x.Tono == TonoToast.Error).Subject;
        toast.Mensaje.Should().Contain(aNombre).And.NotContain(bNombre);
        toast.Mensaje.Should().Contain(claseFallo == 0 ? motivo : "No pudimos guardar");
        cut.FindAll(".alerta-formulario[role=alert]").Should().BeEmpty("el error de A no debe dibujarse sobre el formulario de B");
    }

    [Fact]
    public async Task Un_guardado_de_A_en_vuelo_no_bloquea_el_guardado_de_B()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var espera = new TaskCompletionSource();
        var m = Registrar(new MediadorFalso { Retener = x => x is EditarEmpresaCommand q && q.Id == a ? espera.Task : null });
        m.Detalles[a] = Detalle(a, "Montajes Ebro S.L."); m.Detalles[b] = Detalle(b, "Ibertec S.A."); m.Cumplimientos[a] = m.Cumplimientos[b] = 80;
        var cut = Renderizar(a); await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        var guardarA = Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());
        m.Enviadas.OfType<EditarEmpresaCommand>().Should().ContainSingle("el guardado de A sigue retenido");

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        var guardarB = Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());
        m.Enviadas.OfType<EditarEmpresaCommand>().Select(x => x.Id).Should().Equal([a, b], "B reinicia su guarda aunque A continúe en vuelo");

        await guardarB;
        await cut.InvokeAsync(espera.SetResult); await guardarA;
    }

    [Fact]
    public async Task Cambiar_de_empresa_cierra_la_confirmacion_y_el_formulario_que_pertenecian_a_la_anterior()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var m = Registrar(new MediadorFalso());
        m.Detalles[a] = Detalle(a, "Montajes Ebro S.L."); m.Detalles[b] = Detalle(b, "Ibertec S.A."); m.Cumplimientos[a] = m.Cumplimientos[b] = 80;
        var cut = Renderizar(a); await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs()); cut.FindAll("[role=dialog]").Should().ContainSingle("la confirmación se preparó para A");
        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion")); cut.FindAll("[role=dialog]").Should().BeEmpty("una confirmación de A no puede quedar sobre B");
        cut.Render(p => p.Add(x => x.EntidadId, a).Add(x => x.PestanaActiva, "informacion")); await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs()); Control(cut, "Razón social");
        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
        cut.Find(".titulo-empresa-360").TextContent.Trim().Should().Be("Ibertec S.A."); cut.FindAll("input").Should().BeEmpty("el formulario de A no puede editar B"); m.Enviadas.OfType<EliminarEmpresaCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task La_respuesta_tardia_de_la_cabecera_de_A_no_pinta_encima_de_B()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var espera = new TaskCompletionSource();
        var m = Registrar(new MediadorFalso { Retener = x => x is ObtenerEmpresaPorIdQuery q && q.Id == a ? espera.Task : null });
        m.Detalles[a] = Detalle(a, "Montajes Ebro S.L."); m.Detalles[b] = Detalle(b, "Ibertec S.A."); m.Cumplimientos[b] = 80;
        var cut = Renderizar(a); cut.FindAll(".titulo-empresa-360").Should().BeEmpty("la cabecera de A sigue en vuelo");
        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion")); cut.Find(".titulo-empresa-360").TextContent.Trim().Should().Be("Ibertec S.A.");
        await cut.InvokeAsync(espera.SetResult);
        cut.Find(".titulo-empresa-360").TextContent.Trim().Should().Be("Ibertec S.A.", "la generación de A ya no es vigente");
        m.Enviadas.OfType<ObtenerCumplimientoEmpresaQuery>().Select(x => x.EmpresaId).Should().Equal([b], "la cadena de A se corta antes de pedir su cumplimiento");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Un_resumen_o_cumplimiento_fallido_no_se_disfraza_de_cero_ni_de_vacio(bool fallaCumplimiento)
    {
        var id = Guid.NewGuid(); var m = Registrar(new MediadorFalso { Fallar = x => fallaCumplimiento && x is ObtenerCumplimientoEmpresaQuery ? new InvalidOperationException() : null });
        m.Detalles[id] = fallaCumplimiento ? Detalle(id, "Montajes Ebro S.L.") : null;
        var cut = Renderizar(id);
        cut.FindAll(".estado-vacio").Should().ContainSingle("un fallo de carga se presenta como fallo, no como datos vacíos")
            .Which.TextContent.Should().Contain("No pudimos cargar la empresa");
        cut.Markup.Should().NotContain("0%", "un fallo no equivale a cumplimiento cero").And.NotContain("Sin clientes empresariales", "un resumen fallido no es una lista vacía");
    }
    /// <summary>
    /// El caso anterior no observa `_error`: el panel pinta el estado con
    /// `_error || _detalle is null`, y en sus dos filas el detalle acaba nulo,
    /// asi que el vacio sale por la otra causa. Medido por mutacion: poner
    /// `_error = false` no lo hacia caer. Lo que `_error` sostiene de verdad es
    /// esto — una recarga fallida no deja los datos de la empresa anterior en
    /// pantalla como si fueran los de la nueva.
    /// </summary>
    [Fact]
    public void Una_recarga_fallida_no_deja_en_pantalla_los_datos_de_la_empresa_anterior()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var m = Registrar(new MediadorFalso());
        m.Detalles[a] = Detalle(a, "Montajes Ebro S.L.");
        m.Cumplimientos[a] = 80;
        var cut = Renderizar(a);

        // Control del instrumento: A esta en pantalla, asi que hay un dato
        // viejo que puede quedarse pegado si nadie lo tapa.
        cut.Markup.Should().Contain("Montajes Ebro S.L.");

        m.Fallar = x => x is ObtenerEmpresaPorIdQuery q && q.Id == b
            ? new InvalidOperationException()
            : null;
        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));

        cut.FindAll(".estado-vacio").Should().ContainSingle(
            "la carga de B fallo: eso se dice, no se tapa con lo de A")
            .Which.TextContent.Should().Contain("No pudimos cargar la empresa");
        cut.Markup.Should().NotContain("Montajes Ebro S.L.",
            "seria la empresa anterior presentada como la actual");
    }
}
